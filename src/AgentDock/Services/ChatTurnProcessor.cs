using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Owns the chat turn state machine: it classifies the raw streaming output of a
/// <see cref="ClaudeSession"/> (thinking vs. intermediate commentary vs. tool
/// executions vs. the final answer) and emits already-classified <see cref="ChatOp"/>s.
///
/// Classification is DEFERRED: a text block can't be identified as commentary or as
/// the final answer until we see what follows it (a tool call / more text → it was
/// commentary; end-of-turn → it was the answer). So a text block is BUFFERED and
/// only emitted once the next block (or the result) classifies it. This places each
/// piece of content correctly the first time — nothing is rendered then moved.
/// Live thinking still streams as it arrives (its placement is never ambiguous);
/// the final answer appears, complete, when the turn ends.
///
/// CRUCIALLY this runs on the session's background read-loop thread — no UI work
/// here, no WPF objects, just classification state and plain-data ops. The session
/// raises its events sequentially from the one read loop, so this is effectively
/// single-threaded and needs no locking.
/// </summary>
public sealed class ChatTurnProcessor
{
    /// <summary>Raised (on the session's read-loop thread) with the ops produced
    /// by a single source event, in application order.</summary>
    public event Action<IReadOnlyList<ChatOp>>? Ops;

    // Live thinking state (thinking is shown as it streams — placement is known).
    private bool _thinkingOpen;
    private int _lastThinkingBlockIndex = -1;

    // The text block we've seen but not yet classified. Held until the next block
    // (commentary) or the result (answer) tells us where it belongs.
    private string _pendingText = "";
    private bool _pendingHasText;

    // In-flight background work this turn, keyed by task_id, split by kind so the live
    // indicator can distinguish real subagents from background shells. Driven by the
    // CLI's task_* system events. The CLI reports THREE task_types — local_agent (a real
    // subagent), local_bash (a run_in_background shell), local_workflow — which we
    // previously lumped together and all mislabelled "subagents".
    private enum TaskKind { Subagent, Background, Workflow }
    private readonly Dictionary<string, TaskKind> _activeTasks = [];

    // For local_agent tasks only: the accumulating final report, so a subagent's actual
    // output is surfaced when it completes instead of being discarded. Same object is
    // indexed by the spawning Agent tool_use_id (to route the subagent's own assistant
    // text in via parent_tool_use_id) and by task_id (to emit it on the terminal event).
    private sealed class SubagentReport
    {
        public string Label = "";
        public string? Model;
        public string LatestText = "";
    }
    private readonly Dictionary<string, SubagentReport> _subagentByToolUse = [];
    private readonly Dictionary<string, SubagentReport> _subagentByTaskId = [];

    public void Attach(ClaudeSession session)
    {
        session.StreamDelta += OnStreamDelta;
        session.AssistantMessageReceived += OnAssistantMessage;
        session.ResultReceived += OnResult;
        session.TaskEvent += OnTaskEvent;
    }

    /// <summary>Clears classification state. Call when the UI hard-resets the
    /// transcript (clear / stop / before a new turn) so the two stay in sync.</summary>
    public void Reset()
    {
        _thinkingOpen = false;
        _lastThinkingBlockIndex = -1;
        _pendingText = "";
        _pendingHasText = false;
        _activeTasks.Clear();
        _subagentByToolUse.Clear();
        _subagentByTaskId.Clear();
    }

    private void Emit(List<ChatOp> ops)
    {
        if (ops.Count > 0)
            Ops?.Invoke(ops);
    }

    private void OnStreamDelta(ClaudeStreamDelta delta)
    {
        var ops = new List<ChatOp> { new RemoveInactivityOp() };

        if (delta.DeltaType == "thinking_delta")
        {
            // Redacted thinking emits empty/whitespace deltas — don't open a window
            // for those (the "Thinking…" placeholder stays instead).
            if (!_thinkingOpen && string.IsNullOrWhiteSpace(delta.Text))
            {
                Emit(ops);
                return;
            }

            if (!_thinkingOpen)
            {
                _thinkingOpen = true;
                _lastThinkingBlockIndex = delta.ContentBlockIndex;
                ops.Add(new AppendThinkingOp(delta.Text));
            }
            else if (delta.ContentBlockIndex != _lastThinkingBlockIndex)
            {
                // A new thinking block with no intervening content — merge into the
                // existing window, separated for readability.
                _lastThinkingBlockIndex = delta.ContentBlockIndex;
                ops.Add(new AppendThinkingOp("\n\n" + delta.Text));
            }
            else
            {
                ops.Add(new AppendThinkingOp(delta.Text));
            }
        }
        // text_delta: deliberately NOT rendered live. The authoritative text arrives
        // on the assistant line (OnAssistantMessage), where it's buffered and
        // classified once we see what follows. We only note output resumed (above).

        Emit(ops);
    }

    private void OnAssistantMessage(ClaudeAssistantMessage msg)
    {
        // Messages produced by a subagent carry the spawning Agent tool's id. Their
        // internal tool calls still don't belong in the top-level transcript (they'd leak
        // in as if the main agent ran them), but we no longer throw the whole message away:
        // we capture the subagent's latest text as its final report, surfaced when it
        // completes. (Its raw tool rows are still dropped.)
        if (msg.ParentToolUseId != null)
        {
            CaptureSubagentText(msg);
            return;
        }

        var ops = new List<ChatOp> { new RemoveInactivityOp() };

        var toolBlocks = msg.Content.Where(c => c.Type == "tool_use").ToList();
        if (toolBlocks.Count > 0)
        {
            // A tool call follows — so any buffered text was intermediate commentary.
            FlushPendingAsCommentary(ops);

            // Collapse the thinking window (now incl. that commentary) and start /
            // extend the execution bubble.
            ops.Add(new FinalizeThinkingOp());
            _thinkingOpen = false;
            _lastThinkingBlockIndex = -1;
            ops.Add(new EnsureExecutionOp());

            foreach (var block in toolBlocks)
            {
                // The subagent-spawn tool itself (Agent/Task) is rendered as a distinct
                // subagent entry from the richer task_started system event — skip the raw
                // tool row so it isn't shown twice.
                if (block.Name is "Agent" or "Task")
                    continue;

                var inputStr = "";
                if (block.Input is JsonElement input)
                {
                    inputStr = input.ValueKind == JsonValueKind.Object
                        ? FormatToolInput(input)
                        : input.ToString();
                }
                ops.Add(new AddToolOp(block.Name ?? "(unnamed)", inputStr));
            }

            Emit(ops);
            return;
        }

        var fullText = string.Concat(msg.Content
            .Where(c => c.Type == "text" && c.Text != null)
            .Select(c => c.Text));

        if (fullText.Length > 0)
        {
            // A completed text block. We can't tell yet whether it's the answer or
            // commentary, so buffer it. If we were already holding a text block, the
            // arrival of this one means the previous was NOT the answer — fold it.
            FlushPendingAsCommentary(ops);
            _pendingText = fullText;
            _pendingHasText = true;
        }

        Emit(ops);
    }

    private void OnResult(ClaudeResultMessage result)
    {
        var ops = new List<ChatOp>
        {
            new RemoveInactivityOp(),
            new FinalizeThinkingOp(),
            new FinalizeExecutionOp(),
        };

        // The text block we were holding when the turn ended IS the final answer.
        if (_pendingHasText)
            ops.Add(new PostAnswerOp(_pendingText));

        ops.Add(new TurnCompleteOp(result));
        Emit(ops);
        Reset();
    }

    // Subagent / background-task lifecycle. task_started adds a distinct entry and bumps
    // the running count for its kind; a terminal task_updated/task_notification drops the
    // count and, for a real subagent, surfaces its captured final report. The split counts
    // feed the "N subagents · M background tasks" suffix on the working status line, so the
    // user can see what kind of work is still in flight even after the main agent's text
    // returns.
    private void OnTaskEvent(ClaudeTaskEvent evt)
    {
        var ops = new List<ChatOp>();

        if (evt.Subtype == "task_started")
        {
            // First sighting of this task — classify, render its entry, count it.
            if (!_activeTasks.ContainsKey(evt.TaskId))
            {
                var kind = ClassifyTask(evt.TaskType);
                _activeTasks[evt.TaskId] = kind;

                var label = LabelFor(kind, evt.SubagentType);
                ops.Add(new AddSubagentOp(label, evt.Description ?? ""));

                // Only real subagents produce a report worth surfacing; start capturing
                // it, indexed both ways so child text (by tool_use_id) and the terminal
                // event (by task_id) can find the same accumulator.
                if (kind == TaskKind.Subagent && evt.ToolUseId != null)
                {
                    var report = new SubagentReport { Label = label };
                    _subagentByToolUse[evt.ToolUseId] = report;
                    _subagentByTaskId[evt.TaskId] = report;
                }

                ops.Add(BuildCountsOp());
            }
        }
        else if (evt.IsTerminal)
        {
            // Completed / killed / stopped — stop counting it as running.
            if (_activeTasks.Remove(evt.TaskId))
            {
                // If it was a subagent that produced text, surface its final report now.
                if (_subagentByTaskId.Remove(evt.TaskId, out var report)
                    && report.LatestText.Length > 0)
                {
                    ops.Add(new AddSubagentReportOp(report.Label, report.Model, report.LatestText));
                }
                ops.Add(BuildCountsOp());
            }
        }

        Emit(ops);
    }

    // Records a subagent's own assistant text as its (running) final report. Each subagent
    // assistant line carries one text block; we keep the LATEST non-empty one — a
    // subagent's last text is almost always its conclusion. Tool_use blocks are ignored
    // (their raw rows don't belong in the top-level transcript).
    private void CaptureSubagentText(ClaudeAssistantMessage msg)
    {
        if (msg.ParentToolUseId == null
            || !_subagentByToolUse.TryGetValue(msg.ParentToolUseId, out var report))
            return;

        if (msg.Model != null)
            report.Model = msg.Model;

        var text = string.Concat(msg.Content
            .Where(c => c.Type == "text" && c.Text != null)
            .Select(c => c.Text));
        if (text.Length > 0)
            report.LatestText = text;
    }

    private static TaskKind ClassifyTask(string? taskType) => taskType switch
    {
        "local_agent" => TaskKind.Subagent,
        "local_workflow" => TaskKind.Workflow,
        _ => TaskKind.Background, // local_bash and anything else
    };

    private static string LabelFor(TaskKind kind, string? subagentType) => kind switch
    {
        TaskKind.Subagent => !string.IsNullOrEmpty(subagentType) ? subagentType! : "Subagent",
        TaskKind.Workflow => "Workflow",
        _ => "Background task",
    };

    private ActivityCountsOp BuildCountsOp()
    {
        int subagents = 0, background = 0, workflows = 0;
        foreach (var kind in _activeTasks.Values)
        {
            switch (kind)
            {
                case TaskKind.Subagent: subagents++; break;
                case TaskKind.Workflow: workflows++; break;
                default: background++; break;
            }
        }
        return new ActivityCountsOp(subagents, background, workflows);
    }

    private void FlushPendingAsCommentary(List<ChatOp> ops)
    {
        if (!_pendingHasText) return;
        ops.Add(new CommentaryOp(_pendingText));
        _pendingText = "";
        _pendingHasText = false;
    }

    internal static string FormatToolInput(JsonElement input)
    {
        try
        {
            return JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return input.ToString();
        }
    }
}
