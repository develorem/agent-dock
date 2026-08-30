using System.Text.Json.Serialization;

namespace AgentDock.Services.Remote;

/// <summary>
/// Wire form of one chat bubble.
///
/// The in-memory view-models in <c>Models/ChatMessages.cs</c> are deliberately not
/// serializable: <c>UserMessage.Images</c> holds WPF <c>ImageSource</c>, and
/// <c>AssistantMessage</c> holds a memoizing <c>Func&lt;FlowDocument&gt;</c>. Rather than
/// weaken those types, the transcript is projected onto this parallel DTO union and rehydrated
/// on the client — which also puts the markdown build cost on whichever machine actually
/// renders it, where it belongs.
///
/// Transient bubbles (the waiting placeholder, the inactivity warning) are not serialized: they
/// carry live callbacks and are meaningless to replay.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$m")]
[JsonDerivedType(typeof(SerializedUserMessage), "user")]
[JsonDerivedType(typeof(SerializedAssistantMessage), "assistant")]
[JsonDerivedType(typeof(SerializedSystemMessage), "system")]
[JsonDerivedType(typeof(SerializedActivityMessage), "activity")]
public abstract record SerializedChatMessage(string Id);

public sealed record SerializedUserMessage(
    string Id,
    string Text,
    List<byte[]>? Images) : SerializedChatMessage(Id);

public sealed record SerializedAssistantMessage(
    string Id,
    string Text,
    bool IsMarkdownView,
    bool HasMarkdownToggle) : SerializedChatMessage(Id);

public sealed record SerializedSystemMessage(
    string Id,
    string Text,
    bool IsWarning) : SerializedChatMessage(Id);

/// <summary>
/// The unified per-turn activity bubble: an ordered, interleaved list of thinking blocks, tool
/// executions, subagent spawns and subagent reports. Order matters and is preserved.
/// </summary>
public sealed record SerializedActivityMessage(
    string Id,
    string Header,
    bool IsExpanded,
    List<SerializedActivityEntry> Entries) : SerializedChatMessage(Id);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$e")]
[JsonDerivedType(typeof(SerializedThinkingEntry), "thinking")]
[JsonDerivedType(typeof(SerializedToolEntry), "tool")]
[JsonDerivedType(typeof(SerializedSubagentEntry), "subagent")]
[JsonDerivedType(typeof(SerializedSubagentReportEntry), "subagentReport")]
public abstract record SerializedActivityEntry;

public sealed record SerializedThinkingEntry(string Text) : SerializedActivityEntry;

public sealed record SerializedToolEntry(string Name, string FormattedInput) : SerializedActivityEntry;

public sealed record SerializedSubagentEntry(string Label, string Description) : SerializedActivityEntry;

public sealed record SerializedSubagentReportEntry(
    string Label,
    string? Model,
    string Text) : SerializedActivityEntry;
