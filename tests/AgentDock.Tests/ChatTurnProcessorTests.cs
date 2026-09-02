using System.Text.Json;
using AgentDock.Models;
using AgentDock.Services;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Pins the chat turn classification table — which piece of streamed output becomes a visible
/// answer bubble, which gets folded into the collapsed activity bubble, and which is dropped.
///
/// WHY: this is the single most regression-prone code in the app. Every past attempt to adjust
/// placement broke something else — content rendering in the wrong order, the collapsible
/// bubble failing to appear, everything dumping into the transcript root — and none of it was
/// caught before shipping, because the state machine had no tests. It now does.
///
/// These tests drive the processor's handlers directly. The real wiring goes through
/// ClaudeSession's events, which only that class can raise, so <c>Attach</c> is bypassed; the
/// handlers are the whole of the logic under test.
/// </summary>
public class ChatTurnProcessorTests
{
    // ---------------------------------------------------------------- harness

    private readonly ChatTurnProcessor _processor = new();
    private readonly List<ChatOp> _ops = [];

    public ChatTurnProcessorTests()
    {
        _processor.Ops += batch => _ops.AddRange(batch);
    }

    private static ClaudeContentBlock Text(string text) => new() { Type = "text", Text = text };

    private static ClaudeContentBlock Tool(string name, string inputJson = "{}") => new()
    {
        Type = "tool_use",
        Name = name,
        Id = "toolu_" + Guid.NewGuid().ToString("N")[..8],
        Input = JsonDocument.Parse(inputJson).RootElement.Clone(),
    };

    private void Assistant(params ClaudeContentBlock[] content)
        => _processor.OnAssistantMessage(new ClaudeAssistantMessage { Content = [.. content] });

    private void SubagentAssistant(string parentToolUseId, params ClaudeContentBlock[] content)
        => _processor.OnAssistantMessage(new ClaudeAssistantMessage
        {
            ParentToolUseId = parentToolUseId,
            Content = [.. content],
        });

    private void Thinking(string text, int blockIndex = 0)
        => _processor.OnStreamDelta(new ClaudeStreamDelta
        {
            DeltaType = "thinking_delta",
            Text = text,
            ContentBlockIndex = blockIndex,
        });

    private void Result() => _processor.OnResult(new ClaudeResultMessage());

    /// <summary>Text posted as a standalone, always-visible assistant bubble.</summary>
    private List<string> Answers() => _ops.OfType<PostAnswerOp>().Select(o => o.Text).ToList();

    /// <summary>Text folded into the collapsed activity bubble.</summary>
    private List<string> Commentary() => _ops.OfType<CommentaryOp>().Select(o => o.Text).ToList();

    private List<string> ToolNames() => _ops.OfType<AddToolOp>().Select(o => o.Name).ToList();

    /// <summary>Every piece of assistant prose the user can reach, visible or folded.</summary>
    private string AllText() => string.Join("\n", Answers().Concat(Commentary()));

    // ---------------------------------------------------------------- the reported bug

    /// <summary>
    /// The headline regression. A single assistant message carrying both text and a tool call —
    /// content: [text, tool_use], the ordinary API shape for "here's what I'm doing" plus the
    /// call — had its text blocks never read at all: the tool branch returned before reaching
    /// them. Not folded, not posted, not logged. Gone.
    /// </summary>
    [Fact]
    public void TextSharingAMessageWithAToolCallIsNotLost()
    {
        Assistant(Text("I'll check the config file first."), Tool("Read"));
        Result();

        Assert.Contains("I'll check the config file first.", AllText());
    }

    /// <summary>
    /// The other half of the report: "sometimes it's two sequential messages and Agent Dock is
    /// only showing the last message". Text followed by more text, with no tool call between,
    /// is one answer split across messages — not commentary followed by an answer.
    /// </summary>
    [Fact]
    public void ConsecutiveTextBlocksBecomeOneAnswerRatherThanCommentaryPlusAnswer()
    {
        Assistant(Text("Part one of the answer."));
        Assistant(Text("Part two of the answer."));
        Result();

        var answer = Assert.Single(Answers());
        Assert.Contains("Part one of the answer.", answer);
        Assert.Contains("Part two of the answer.", answer);

        // The first part must not have been demoted into the collapsed bubble.
        Assert.Empty(Commentary());
    }

    /// <summary>
    /// The question case, which is where the loss was most costly: the text explaining the
    /// options has to be readable while the question is on screen, not folded into a collapsed
    /// grey block behind it.
    /// </summary>
    [Fact]
    public void TextBeforeAQuestionIsPostedVisiblyNotFolded()
    {
        Assistant(Text("Both approaches work. A is faster, B is simpler. Which do you want?"));
        Assistant(Tool("AskUserQuestion", """{"questions":[]}"""));

        Assert.Contains(
            "Both approaches work. A is faster, B is simpler. Which do you want?",
            Assert.Single(Answers()));
        Assert.Empty(Commentary());
    }

    /// <summary>Same, when the text and the question share one message.</summary>
    [Fact]
    public void TextSharingAMessageWithAQuestionIsPostedVisibly()
    {
        Assistant(
            Text("I found two candidates. Which should I use?"),
            Tool("AskUserQuestion", """{"questions":[]}"""));

        Assert.Contains("I found two candidates. Which should I use?", Assert.Single(Answers()));
        Assert.Empty(Commentary());
    }

    [Fact]
    public void TextBeforeAPlanApprovalIsPostedVisibly()
    {
        Assistant(Text("Here is the plan:\n\n1. Do the thing\n2. Do the other thing"));
        Assistant(Tool("ExitPlanMode"));

        Assert.Contains("Here is the plan:", Assert.Single(Answers()));
    }

    /// <summary>
    /// A mid-turn answer bubble is posted between two activity bubbles, so the work that
    /// resumes after the user answers doesn't land in a bubble sitting above that text. Without
    /// the seal the transcript reads out of order.
    /// </summary>
    [Fact]
    public void AQuestionSealsTheActivityBubbleSoLaterWorkStartsAFreshOne()
    {
        Thinking("Considering the options");
        Assistant(Text("Which one?"), Tool("AskUserQuestion", """{"questions":[]}"""));

        var sealIndex = _ops.FindIndex(o => o is SealActivityOp);
        var answerIndex = _ops.FindIndex(o => o is PostAnswerOp);
        var executionIndex = _ops.FindIndex(o => o is EnsureExecutionOp);

        Assert.True(sealIndex >= 0, "the activity bubble should be sealed before the answer");
        Assert.True(sealIndex < answerIndex, "seal must precede the answer bubble");
        Assert.True(answerIndex < executionIndex, "the answer must precede the next activity bubble");
    }

    /// <summary>
    /// With no preamble there is nothing to insert between bubbles, so the turn keeps its single
    /// activity bubble. Sealing unconditionally would split every question turn in two.
    /// </summary>
    [Fact]
    public void AQuestionWithNoPreambleDoesNotSplitTheActivityBubble()
    {
        Thinking("Considering");
        Assistant(Tool("AskUserQuestion", """{"questions":[]}"""));

        Assert.DoesNotContain(_ops, o => o is SealActivityOp);
        Assert.Empty(Answers());
    }

    /// <summary>
    /// Text before an ordinary tool call is narration for that call, even when a question comes
    /// later in the turn — it must not be promoted just because a question eventually follows.
    /// </summary>
    [Fact]
    public void NarrationBeforeAToolCallStaysFoldedEvenWhenAQuestionFollows()
    {
        Assistant(Text("Let me check the database first."));
        Assistant(Tool("Bash"));
        Assistant(Tool("AskUserQuestion", """{"questions":[]}"""));

        Assert.Equal(["Let me check the database first."], Commentary());
        Assert.Empty(Answers());
    }

    // ---------------------------------------------------------------- unchanged behaviour

    /// <summary>
    /// The tidy-transcript feature this whole machine exists for. Text before an ordinary tool
    /// call is narration, and belongs in the collapsed bubble — not as a visible bubble that
    /// inflates the transcript. Breaking this is the classic over-correction.
    /// </summary>
    [Fact]
    public void TextBeforeAnOrdinaryToolCallIsStillCommentary()
    {
        Assistant(Text("Let me look at that file."));
        Assistant(Tool("Read"));
        Assistant(Text("The answer is 42."));
        Result();

        Assert.Equal(["Let me look at that file."], Commentary());
        Assert.Equal(["The answer is 42."], Answers());
    }

    [Fact]
    public void TheLastTextOfATurnIsTheAnswer()
    {
        Assistant(Text("All done."));
        Result();

        Assert.Equal(["All done."], Answers());
        Assert.Empty(Commentary());
    }

    [Fact]
    public void ALongAgenticTurnFoldsEveryNarrationAndPostsOneAnswer()
    {
        Assistant(Text("First I'll read it."), Tool("Read"));
        Assistant(Text("Now I'll patch it."), Tool("Edit"));
        Assistant(Text("And verify."), Tool("Bash"));
        Assistant(Text("Fixed: the config was missing a key."));
        Result();

        Assert.Equal(
            ["First I'll read it.", "Now I'll patch it.", "And verify."],
            Commentary());
        Assert.Equal(["Fixed: the config was missing a key."], Answers());
        Assert.Equal(["Read", "Edit", "Bash"], ToolNames());
    }

    /// <summary>
    /// Commentary has to be added while the grey block is still open, or it lands outside the
    /// bubble. This ordering is exactly what "everything dumped into the root" looked like.
    /// </summary>
    [Fact]
    public void CommentaryIsEmittedBeforeTheThinkingWindowCloses()
    {
        Assistant(Text("Narration."), Tool("Read"));

        var commentaryIndex = _ops.FindIndex(o => o is CommentaryOp);
        var finalizeIndex = _ops.FindIndex(o => o is FinalizeThinkingOp);

        Assert.True(commentaryIndex >= 0);
        Assert.True(commentaryIndex < finalizeIndex,
            "commentary must be folded in before the grey block is closed");
    }

    [Fact]
    public void AnOrdinaryToolCallDoesNotSealTheActivityBubble()
    {
        Assistant(Text("Narration."), Tool("Read"));

        Assert.DoesNotContain(_ops, o => o is SealActivityOp);
        Assert.Empty(Answers());
    }

    [Fact]
    public void AToolCallWithNoTextEmitsNoProse()
    {
        Assistant(Tool("Read"));
        Result();

        Assert.Empty(Answers());
        Assert.Empty(Commentary());
        Assert.Equal(["Read"], ToolNames());
    }

    [Fact]
    public void AnEmptyTurnEmitsNoProse()
    {
        Result();

        Assert.Empty(Answers());
        Assert.Empty(Commentary());
    }

    // ---------------------------------------------------------------- thinking

    [Fact]
    public void ThinkingStreamsLive()
    {
        Thinking("Weighing it up");

        Assert.Equal(["Weighing it up"], _ops.OfType<AppendThinkingOp>().Select(o => o.Text));
    }

    /// <summary>
    /// Redacted thinking arrives as empty deltas. Opening a window for those produced the empty
    /// "Thinking" bubble on every turn that was fixed in v0.9.0 — keep it fixed.
    /// </summary>
    [Fact]
    public void RedactedThinkingOpensNoWindow()
    {
        Thinking("");
        Thinking("   ");

        Assert.Empty(_ops.OfType<AppendThinkingOp>());
    }

    [Fact]
    public void ASecondThinkingBlockMergesIntoTheSameWindow()
    {
        Thinking("First thought", blockIndex: 0);
        Thinking("Second thought", blockIndex: 1);

        var appended = _ops.OfType<AppendThinkingOp>().Select(o => o.Text).ToList();
        Assert.Equal(2, appended.Count);
        Assert.StartsWith("\n\n", appended[1]);
    }

    // ---------------------------------------------------------------- subagents

    /// <summary>
    /// A subagent's internal tool calls must not surface as if the main agent ran them, but its
    /// final report must not be thrown away either.
    /// </summary>
    [Fact]
    public void SubagentInternalToolsAreHiddenButItsReportIsSurfaced()
    {
        _processor.OnTaskEvent(new ClaudeTaskEvent
        {
            Subtype = "task_started",
            TaskId = "task-1",
            TaskType = "local_agent",
            SubagentType = "Explore",
            ToolUseId = "toolu_parent",
            Description = "find the config loader",
        });

        SubagentAssistant("toolu_parent", Text("Looking around"), Tool("Grep"));
        SubagentAssistant("toolu_parent", Text("It lives in Services/AppSettingsStore.cs"));

        _processor.OnTaskEvent(new ClaudeTaskEvent
        {
            Subtype = "task_updated",
            TaskId = "task-1",
            Status = "completed",
        });

        // The subagent's own Grep never appears as a top-level tool row.
        Assert.Empty(ToolNames());

        var report = Assert.Single(_ops.OfType<AddSubagentReportOp>());
        Assert.Equal("Explore", report.Label);
        Assert.Contains("Services/AppSettingsStore.cs", report.Text);

        // And nothing from the subagent leaked into the main transcript's prose.
        Assert.Empty(Answers());
        Assert.Empty(Commentary());
    }

    /// <summary>The spawning Agent/Task tool row is suppressed — the richer subagent entry
    /// from the task_started event stands in for it, and showing both duplicates it.</summary>
    [Fact]
    public void TheSubagentSpawningToolRowIsSuppressed()
    {
        Assistant(Text("Delegating this."), Tool("Agent"));

        Assert.Empty(ToolNames());
        Assert.Equal(["Delegating this."], Commentary());
    }

    // ---------------------------------------------------------------- turn isolation

    [Fact]
    public void PendingTextDoesNotLeakIntoTheNextTurn()
    {
        Assistant(Text("Turn one answer."));
        Result();
        _ops.Clear();

        Assistant(Text("Turn two answer."));
        Result();

        Assert.Equal(["Turn two answer."], Answers());
    }

    [Fact]
    public void ResetDropsBufferedText()
    {
        Assistant(Text("Buffered but abandoned."));
        _processor.Reset();
        _ops.Clear();

        Result();

        Assert.Empty(Answers());
    }
}
