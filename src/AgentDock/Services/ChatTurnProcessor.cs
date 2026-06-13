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

    public void Attach(ClaudeSession session)
    {
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
        _pendingText = "";
        _pendingHasText = false;
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
