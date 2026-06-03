namespace AgentDock.Models;

/// <summary>
/// A unit of already-classified work produced by <c>ChatTurnProcessor</c> on the
/// session's background read-loop thread and applied to the chat message list on
/// the UI thread. The processor decides <i>what</i> the transcript should do
/// (this is the "post-processing" — classifying streamed content into thinking /
/// commentary / execution / answer); the UI only performs the resulting
/// observable mutation. Ops carry plain data only — never WPF objects — so they
/// are safe to construct off the UI thread.
/// </summary>
public abstract record ChatOp;

/// <summary>Drop the transient "Thinking…" placeholder.</summary>
public sealed record RemoveWaitingOp : ChatOp;

/// <summary>Drop the inactivity warning bubble (output resumed).</summary>
public sealed record RemoveInactivityOp : ChatOp;

/// <summary>Append text to the live thinking window (created on first append).
/// The processor has already applied any block separators and the redacted-thinking
/// skip, so the UI just appends.</summary>
public sealed record AppendThinkingOp(string Text) : ChatOp;

/// <summary>Append a delta to the live response (answer/commentary) bubble,
/// created on first append.</summary>
public sealed record AppendResponseOp(string Text) : ChatOp;

/// <summary>Reconcile the live response with the authoritative full text from an
/// <c>assistant</c> message line. The UI applies it only when it's at least as
/// long as what the deltas accumulated (guards against a later short block
/// clobbering earlier text).</summary>
public sealed record ReconcileResponseOp(string FullText) : ChatOp;

/// <summary>The live response text was intermediate commentary (tools followed) —
/// fold it into the thinking window.</summary>
public sealed record FoldResponseIntoThinkingOp : ChatOp;

/// <summary>Finalize the live thinking window into a collapsed, immutable bubble.</summary>
public sealed record FinalizeThinkingOp : ChatOp;

/// <summary>Finalize the live response into the immutable answer bubble.</summary>
public sealed record FinalizeResponseOp : ChatOp;

/// <summary>Ensure the turn's execution bubble exists.</summary>
public sealed record EnsureExecutionOp : ChatOp;

/// <summary>Add a tool-call entry to the current execution bubble.</summary>
public sealed record AddToolOp(string Name, string FormattedInput) : ChatOp;

/// <summary>Finalize the execution bubble into its immutable form.</summary>
public sealed record FinalizeExecutionOp : ChatOp;

/// <summary>The turn finished: surface any errors and the cost/token summary,
/// and update cumulative stats.</summary>
public sealed record TurnCompleteOp(ClaudeResultMessage Result) : ChatOp;
