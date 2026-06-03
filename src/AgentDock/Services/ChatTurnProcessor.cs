using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Owns the chat turn state machine: it classifies the raw streaming output of a
/// <see cref="ClaudeSession"/> (thinking vs. intermediate commentary vs. tool
/// executions vs. the final answer) and emits already-classified <see cref="ChatOp"/>s.
///
/// CRUCIALLY this runs on the session's background read-loop thread — it subscribes
/// to the session events directly (no Dispatcher), does the "decide where this
/// content goes" work there, and hands the UI a finished list of ops. The UI only
/// performs the observable mutation. Nothing here touches WPF objects, so it is
/// safe off the UI thread; the processor holds plain classification state only.
///
/// Threading: the session raises its events sequentially from the one read loop,
/// so this type is single-threaded in practice and needs no locking. Each batch of
/// ops for one source event is delivered via <see cref="Ops"/> in order.
/// </summary>
public sealed class ChatTurnProcessor
{
    /// <summary>Raised (on the session's read-loop thread) with the ops produced
    /// by a single source event, in application order.</summary>
    public event Action<IReadOnlyList<ChatOp>>? Ops;

    // Classification state. Mirrors the live-tail the UI is showing, but holds no
    // VM references — only enough to decide the next op.
    private bool _thinkingOpen;
    private int _lastThinkingBlockIndex = -1;
    private bool _responseOpen;

    public void Attach(ClaudeSession session)
    {
        session.ContentBlockStarted += OnContentBlockStarted;
        session.ContentBlockStopped += OnContentBlockStopped;
        session.StreamDelta += OnStreamDelta;
        session.AssistantMessageReceived += OnAssistantMessage;
        session.ResultReceived += OnResult;
    }

    /// <summary>Clears classification state. Call when the UI hard-resets the
    /// transcript (clear / stop / before a new turn) so the two stay in sync.</summary>
    public void Reset()
    {
        _thinkingOpen = false;
        _lastThinkingBlockIndex = -1;
        _responseOpen = false;
    }

    private void Emit(List<ChatOp> ops)
    {
        if (ops.Count > 0)
            Ops?.Invoke(ops);
    }

    private void OnContentBlockStarted(ClaudeContentBlockEvent evt)
    {
        // A thinking block keeps the "Thinking…" placeholder; the window itself is
        // created lazily on the first real thinking delta. A non-thinking block
        // (text/tool_use) ends the placeholder.
        if (evt.BlockType == "thinking")
            return;
        Emit([new RemoveWaitingOp()]);
    }

    private void OnContentBlockStopped(ClaudeContentBlockEvent evt)
    {
        // A thinking block ending does NOT end the thinking phase — the model may
        // open another immediately. Thinking is only finalized when genuinely
        // different content arrives (response/execution) or the turn ends.
    }

    private void OnStreamDelta(ClaudeStreamDelta delta)
    {
        var ops = new List<ChatOp> { new RemoveInactivityOp() };

        if (delta.DeltaType == "thinking_delta")
        {
            // Redacted thinking emits empty/whitespace deltas — don't open a window
            // for those (keeps the placeholder instead).
            if (!_thinkingOpen && string.IsNullOrWhiteSpace(delta.Text))
            {
                Emit(ops);
                return;
            }

            if (!_thinkingOpen)
            {
                ops.Add(new RemoveWaitingOp());
                _thinkingOpen = true;
                _lastThinkingBlockIndex = delta.ContentBlockIndex;
                ops.Add(new AppendThinkingOp(delta.Text));
            }
            else if (delta.ContentBlockIndex != _lastThinkingBlockIndex)
            {
                // A new thinking block with no intervening response/execution —
                // merge into the existing window, separated for readability.
                _lastThinkingBlockIndex = delta.ContentBlockIndex;
                ops.Add(new AppendThinkingOp("\n\n" + delta.Text));
            }
            else
            {
                ops.Add(new AppendThinkingOp(delta.Text));
            }

            Emit(ops);
            return;
        }

        // Real response text. NOTE: unlike the old UI-thread path, we deliberately
        // do NOT finalize the thinking window here. Keeping it open means that if
        // this text turns out to be intermediate commentary (a tool_use follows),
        // it folds back into the SAME thinking window instead of spawning a second
        // one — which is what produced the "lots of thinking windows" pile-up.
        ops.Add(new RemoveWaitingOp());
        _responseOpen = true;
        ops.Add(new AppendResponseOp(delta.Text));
        Emit(ops);
    }

    private void OnAssistantMessage(ClaudeAssistantMessage msg)
    {
        var ops = new List<ChatOp>();

        var fullText = string.Concat(msg.Content
            .Where(c => c.Type == "text" && c.Text != null)
            .Select(c => c.Text));

        // Reconcile the streamed text with the authoritative block text.
        if (_responseOpen && fullText.Length > 0)
            ops.Add(new ReconcileResponseOp(fullText));

        var toolBlocks = msg.Content.Where(c => c.Type == "tool_use").ToList();
        if (toolBlocks.Count > 0)
        {
            // This line calls tools, so any text streamed before it this turn was
            // intermediate commentary, not the answer. Fold it into thinking, then
            // collapse and start the execution bubble.
            ops.Add(new FoldResponseIntoThinkingOp());
            _responseOpen = false;
            ops.Add(new FinalizeThinkingOp());
            _thinkingOpen = false;
            _lastThinkingBlockIndex = -1;
            ops.Add(new EnsureExecutionOp());

            foreach (var block in toolBlocks)
            {
                var inputStr = "";
                if (block.Input is JsonElement input)
                {
                    inputStr = input.ValueKind == JsonValueKind.Object
                        ? FormatToolInput(input)
                        : input.ToString();
                }
                ops.Add(new AddToolOp(block.Name ?? "(unnamed)", inputStr));
            }
        }
        // No tool calls in THIS line: we can't conclude the text is the final
        // answer (the tool_use for this turn may arrive on a later line). Leave the
        // response live; the turn end (OnResult) finalizes it as the answer.

        Emit(ops);
    }

    private void OnResult(ClaudeResultMessage result)
    {
        Emit([
            new RemoveWaitingOp(),
            new RemoveInactivityOp(),
            new FinalizeThinkingOp(),
            new FinalizeResponseOp(),
            new FinalizeExecutionOp(),
            new TurnCompleteOp(result),
        ]);
        Reset();
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
