using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Owns the chat turn state machine: it classifies the raw streaming output of a
/// <see cref="ClaudeSession"/> (thinking vs. intermediate commentary vs. tool
/// executions vs. the final answer) and emits already-classified <see cref="ChatOp"/>s.
///
/// Classification is DEFERRED: a text block can't be identified as commentary or as
/// the final answer until we see what follows it. So a text block is BUFFERED and only
/// emitted once the next block (or the result) classifies it. This places each piece of
/// content correctly the first time — nothing is rendered then moved. Live thinking still
/// streams as it arrives (its placement is never ambiguous); the final answer appears,
/// complete, when the turn ends.
///
/// The classification table, which is what the tests in <c>ChatTurnProcessorTests</c> pin:
///
/// <list type="table">
/// <listheader><term>Text is followed by…</term><description>…so it is</description></listheader>
/// <item><term>an ordinary tool call (Bash, Edit, Read…)</term>
///   <description>commentary — folded into the collapsed activity bubble</description></item>
/// <item><term>a human-facing prompt (AskUserQuestion, ExitPlanMode)</term>
///   <description>posted visibly — the user needs it to answer</description></item>
/// <item><term>more text, with no tool call in between</term>
///   <description>the same answer continued — appended, not demoted</description></item>
/// <item><term>end of turn</term>
///   <description>the final answer — posted as a standalone bubble</description></item>
/// </list>
///
/// Two of those rules are corrections, and both were losing text outright rather than
/// merely misplacing it. Text sharing a message with a tool call was never read at all
/// (the tool branch returned first), and text followed by more text was demoted to
/// commentary, so a two-part answer showed only its second part. If you change this
/// method, run those tests: every past regression here was a placement rule that looked
/// obviously right in isolation.
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

    internal void OnStreamDelta(ClaudeStreamDelta delta)
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

    internal void OnAssistantMessage(ClaudeAssistantMessage msg)
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

        // This message's own text. Read BEFORE the tool branch decides anything, because a
        // single assistant message routinely carries both — content: [text, tool_use] is the
        // ordinary shape for "here's what I'm about to do" followed by the call. The tool
        // branch used to return without ever looking at these blocks, so that text was not
        // folded into the activity bubble, not posted, not logged: dropped outright.
        var messageText = ConcatText(msg.Content);

        var toolBlocks = msg.Content.Where(c => c.Type == "tool_use").ToList();
        if (toolBlocks.Count > 0)
        {
            // Everything we're holding: the block buffered from an earlier message, plus this
            // message's own text. Both precede the call, so both are classified the same way.
            var precedingText = Combine(_pendingText, messageText);
            _pendingText = "";
            _pendingHasText = false;

            // A prompt that stops to ask the human something is not a step in the agent's
            // work — it is the agent addressing the user, and the text leading up to it is
            // what the user needs in order to answer. Folding that into a collapsed grey
            // block puts the question on screen and the reason for it out of sight.
            if (toolBlocks.Any(b => IsHumanPrompt(b.Name)))
            {
                ops.Add(new FinalizeThinkingOp());

                if (precedingText.Length > 0)
                {
                    // Seal only when there is actually something to insert between the
                    // bubbles. With no text to post there is no ordering to preserve, and
                    // sealing would split one turn's activity into two bubbles for nothing.
                    ops.Add(new SealActivityOp());
                    ops.Add(new PostAnswerOp(precedingText));
                }
            }
            else
            {
                // Text followed by a tool call is intermediate commentary. Added before the
                // thinking window is closed so it lands inside the open grey block.
                if (precedingText.Length > 0)
                    ops.Add(new CommentaryOp(precedingText));
                ops.Add(new FinalizeThinkingOp());
            }

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

        if (messageText.Length > 0)
        {
            // A completed text block with no tool call in this message. Still can't tell
            // whether it's the answer or commentary, so buffer it.
            //
            // If we were already holding text, this is NOT a demotion. Text followed by more
            // text, with no tool call in between, is one answer arriving in several messages —
            // which is what the CLI does when a reply spans stop-reason boundaries. Treating
            // the earlier block as commentary hid the first half of every two-part answer
            // inside the collapsed activity bubble and showed only the last part.
            _pendingText = Combine(_pendingText, messageText);
            _pendingHasText = true;
        }

        Emit(ops);
    }

    /// <summary>All the text blocks of a message, in order, concatenated.</summary>
    private static string ConcatText(List<ClaudeContentBlock> content)
        => string.Concat(content
            .Where(c => c.Type == "text" && c.Text != null)
            .Select(c => c.Text));

    /// <summary>
    /// Joins two text runs with a blank line, tolerating either being empty. Separate blocks
    /// are separate paragraphs — concatenating them raw would run the last sentence of one
    /// into the first heading of the next and break markdown rendering.
    /// </summary>
    private static string Combine(string first, string second)
    {
        if (first.Length == 0) return second;
        if (second.Length == 0) return first;
        return first + "\n\n" + second;
    }

    /// <summary>
    /// Tools that stop the turn to ask the human something, rather than doing work.
    ///
    /// <c>AskUserQuestion</c> puts a question and its options on screen; <c>ExitPlanMode</c>
    /// puts a plan up for approval. In both cases the agent has just written the context the
    /// user needs to decide, so that text is posted visibly instead of being folded away.
    /// Ordinary tools — Bash, Edit, Read — are work, and the text before them is commentary.
    /// </summary>
    private static bool IsHumanPrompt(string? toolName)
        => toolName is "AskUserQuestion" or "ExitPlanMode";

    internal void OnResult(ClaudeResultMessage result)
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
    internal void OnTaskEvent(ClaudeTaskEvent evt)
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

        var text = ConcatText(msg.Content);
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
