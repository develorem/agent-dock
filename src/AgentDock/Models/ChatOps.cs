namespace AgentDock.Models;

/// <summary>
/// A unit of already-classified work produced by <c>ChatTurnProcessor</c> on the
/// session's background read-loop thread and applied to the chat message list on
/// the UI thread.
///
/// The processor buffers each text block and waits until it knows what follows
/// before deciding where it goes — so content is placed correctly the first time
/// and never rendered then moved. Intermediate commentary (text followed by a tool
/// call or more text) is dropped into the thinking group; the final answer (text
/// followed by end-of-turn) is posted as a standalone, always-shown bubble.
///
/// Ops carry plain data only — never WPF objects — so they are safe to construct
/// off the UI thread.
/// </summary>
public abstract record ChatOp;

/// <summary>Drop the inactivity warning bubble (output resumed).</summary>
public sealed record RemoveInactivityOp : ChatOp;

/// <summary>Append live thinking text to the thinking window (created on first
/// append). The processor has already applied block separators and the
/// redacted-thinking skip.</summary>
public sealed record AppendThinkingOp(string Text) : ChatOp;

/// <summary>A buffered text block turned out to be intermediate commentary (a tool
/// call or another text block followed it). Fold it into the thinking window.</summary>
public sealed record CommentaryOp(string Text) : ChatOp;

/// <summary>Finalize the live thinking window into a collapsed, immutable bubble.</summary>
public sealed record FinalizeThinkingOp : ChatOp;

/// <summary>Ensure the turn's execution bubble exists.</summary>
public sealed record EnsureExecutionOp : ChatOp;

/// <summary>Add a tool-call entry to the current execution bubble.</summary>
public sealed record AddToolOp(string Name, string FormattedInput) : ChatOp;

/// <summary>A subagent (or background task) was spawned — add a distinct entry to the
/// activity bubble. <paramref name="Label"/> is the subagent type (e.g. "Explore"),
/// "Background task", or "Workflow"; <paramref name="Description"/> is the short task
/// description.</summary>
public sealed record AddSubagentOp(string Label, string Description) : ChatOp;

/// <summary>A subagent (task_type <c>local_agent</c>) finished and produced a final
/// report. Rendered as a distinct entry so the subagent's actual output is captured in
/// the transcript (previously discarded). <paramref name="Model"/> is the model the
/// subagent ran on, shown as a tag; null if unknown.</summary>
public sealed record AddSubagentReportOp(string Label, string? Model, string Text) : ChatOp;

/// <summary>The counts of in-flight background work changed, split by kind so the
/// working status line can say e.g. "2 subagents · 1 background task" rather than
/// lumping background shells in with real subagents. Drives the live suffix on the
/// working status line.</summary>
public sealed record ActivityCountsOp(int Subagents, int BackgroundTasks, int Workflows) : ChatOp;

/// <summary>Finalize the execution bubble into its immutable form.</summary>
public sealed record FinalizeExecutionOp : ChatOp;

/// <summary>The buffered text block was the final answer (end-of-turn followed it).
/// Post it as a standalone, always-shown assistant bubble.</summary>
public sealed record PostAnswerOp(string Text) : ChatOp;

/// <summary>The turn finished: surface any errors and the cost/token summary,
/// and update cumulative stats.</summary>
public sealed record TurnCompleteOp(ClaudeResultMessage Result) : ChatOp;
