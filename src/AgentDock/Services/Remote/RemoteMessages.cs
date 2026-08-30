using System.Text.Json.Serialization;
using AgentDock.Models;

namespace AgentDock.Services.Remote;

/// <summary>
/// Every frame on the wire. Polymorphic on a short type discriminator so the reader can
/// switch without a hand-rolled envelope.
///
/// Naming convention: <c>*Msg</c> is server to client, <c>*Cmd</c> is client to server, and the
/// handshake / heartbeat frames travel both ways.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$k")]
// --- handshake + liveness (both directions) ---
[JsonDerivedType(typeof(ServerHelloMsg), "hello.server")]
[JsonDerivedType(typeof(ClientHelloCmd), "hello.client")]
[JsonDerivedType(typeof(AuthChallengeMsg), "auth.challenge")]
[JsonDerivedType(typeof(AuthProofCmd), "auth.proof")]
[JsonDerivedType(typeof(AuthResultMsg), "auth.result")]
[JsonDerivedType(typeof(PingFrame), "ping")]
[JsonDerivedType(typeof(PongFrame), "pong")]
[JsonDerivedType(typeof(DisconnectMsg), "bye")]
// --- state replication (server to client) ---
[JsonDerivedType(typeof(HostSnapshotMsg), "host.snapshot")]
[JsonDerivedType(typeof(ProjectSnapshotMsg), "project.snapshot")]
[JsonDerivedType(typeof(ProjectDeltaMsg), "project.delta")]
[JsonDerivedType(typeof(ProjectAddedMsg), "project.added")]
[JsonDerivedType(typeof(ProjectRemovedMsg), "project.removed")]
[JsonDerivedType(typeof(SessionStateMsg), "session.state")]
[JsonDerivedType(typeof(PermissionRequestMsg), "session.permission")]
[JsonDerivedType(typeof(PermissionResolvedMsg), "session.permissionResolved")]
[JsonDerivedType(typeof(GitStatusMsg), "git.status")]
[JsonDerivedType(typeof(ProjectSettingsMsg), "project.settings")]
[JsonDerivedType(typeof(DirectoryListingMsg), "files.listing")]
[JsonDerivedType(typeof(FileContentMsg), "files.content")]
[JsonDerivedType(typeof(FileInvalidatedMsg), "files.invalidated")]
[JsonDerivedType(typeof(TranscriptAppendMsg), "project.append")]
// --- commands (client to server) ---
[JsonDerivedType(typeof(SendMessageCmd), "cmd.send")]
[JsonDerivedType(typeof(AllowPermissionCmd), "cmd.allow")]
[JsonDerivedType(typeof(DenyPermissionCmd), "cmd.deny")]
[JsonDerivedType(typeof(AnswerQuestionCmd), "cmd.answer")]
[JsonDerivedType(typeof(ListDirectoryCmd), "cmd.listDir")]
[JsonDerivedType(typeof(ReadFileCmd), "cmd.readFile")]
[JsonDerivedType(typeof(ReadDiffCmd), "cmd.readDiff")]
[JsonDerivedType(typeof(WatchDirectoryCmd), "cmd.watchDir")]
[JsonDerivedType(typeof(UpdateTodoItemsCmd), "cmd.todo")]
[JsonDerivedType(typeof(LocalCommandCmd), "cmd.local")]
[JsonDerivedType(typeof(AckCmd), "cmd.ack")]
[JsonDerivedType(typeof(ResyncCmd), "cmd.resync")]
public abstract record RemoteFrame;

// ---------------------------------------------------------------- handshake

/// <summary>
/// First frame the server sends. The client checks <see cref="ProtocolVersion"/> before
/// anything else, and shows <see cref="MachineName"/> + <see cref="CertFingerprint"/> so the
/// user can confirm they reached the machine they meant (trust on first use).
/// </summary>
public sealed record ServerHelloMsg(
    int ProtocolVersion,
    string MachineName,
    string AppVersion,
    string CertFingerprint) : RemoteFrame;

public sealed record ClientHelloCmd(
    int ProtocolVersion,
    string ClientName,
    string AppVersion) : RemoteFrame;

/// <summary>
/// Server nonce. The client answers with an HMAC over it keyed by the pairing code, so the
/// code itself never crosses the wire even inside TLS.
/// </summary>
public sealed record AuthChallengeMsg(string Nonce) : RemoteFrame;

/// <summary>
/// Either a proof derived from the pairing code, or a previously issued session token for a
/// silent reconnect after a network blip.
/// </summary>
public sealed record AuthProofCmd(string? CodeProof, string? SessionToken) : RemoteFrame;

public sealed record AuthResultMsg(
    bool Success,
    string? SessionToken,
    DisconnectReason FailureReason,
    string? Message,
    int? RetryAfterSeconds) : RemoteFrame;

public sealed record PingFrame(long TicksUtc) : RemoteFrame;

public sealed record PongFrame(long TicksUtc) : RemoteFrame;

public sealed record DisconnectMsg(DisconnectReason Reason, string? Message) : RemoteFrame;

// ---------------------------------------------------------------- host + project state

/// <summary>The tab set: one entry per live agent session on the server.</summary>
public sealed record HostSnapshotMsg(
    string HostName,
    List<RemoteProjectDescriptor> Projects) : RemoteFrame;

/// <summary>
/// Identity and chrome for one remote project tab. <see cref="Id"/> is a server-assigned
/// opaque handle, so a server file path is never used as an identifier on the wire.
/// </summary>
public sealed record RemoteProjectDescriptor(
    string Id,
    string DisplayName,
    string DisplayRoot,
    string? IconName,
    string? IconColor,
    byte[]? IconImage,
    bool IsGitRepository);

public sealed record ProjectAddedMsg(RemoteProjectDescriptor Project) : RemoteFrame;

/// <summary>A project tab went away server-side. The client marks it; it does not vanish.</summary>
public sealed record ProjectRemovedMsg(string ProjectId, string Reason) : RemoteFrame;

/// <summary>
/// Complete current state of one project tab, at sequence <c>Seq</c>. Sent on join and
/// whenever the replay buffer cannot bridge a reconnect gap.
/// </summary>
public sealed record ProjectSnapshotMsg(
    string ProjectId,
    long Seq,
    List<SerializedChatMessage> Transcript,
    SessionStateSnapshot Session,
    GitStatusSnapshot? Git,
    ProjectSettingsSnapshot Settings,
    List<QueuedMessageSnapshot> Queue,
    PermissionRequestMsg? PendingPermission) : RemoteFrame;

/// <summary>
/// A batch of already-classified chat ops. Carries a monotonic per-project sequence number:
/// without it, reconnects silently duplicate or drop transcript content.
/// </summary>
public sealed record ProjectDeltaMsg(
    string ProjectId,
    long Seq,
    List<ChatOp> Ops) : RemoteFrame;

public sealed record SessionStateSnapshot(
    ClaudeSessionState State,
    string? Model,
    string? AccountLabel,
    bool IsDangerousMode,
    bool HasBackgroundWork,
    string StatusLabel,
    double TotalCostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens);

public sealed record SessionStateMsg(string ProjectId, SessionStateSnapshot State) : RemoteFrame;

public sealed record QueuedMessageSnapshot(
    string Id,
    string Text,
    long? FireAtTicksUtc,
    string StatusText);

// ---------------------------------------------------------------- permissions

/// <summary>
/// A prompt awaiting a human. In server mode nothing can answer these locally, so this frame
/// and its three replies are load-bearing: if they break, every session needing approval stalls.
/// </summary>
public sealed record PermissionRequestMsg(
    string ProjectId,
    string RequestId,
    string ToolName,
    string InputJson) : RemoteFrame;

/// <summary>
/// The request is no longer live (answered, superseded, or the turn died). Lets the client
/// drop a stale prompt instead of leaving a dead panel on screen.
/// </summary>
public sealed record PermissionResolvedMsg(string ProjectId, string RequestId) : RemoteFrame;

public sealed record AllowPermissionCmd(string ProjectId, string RequestId) : RemoteFrame;

public sealed record DenyPermissionCmd(string ProjectId, string RequestId, string? Reason) : RemoteFrame;

public sealed record AnswerQuestionCmd(
    string ProjectId,
    string RequestId,
    string QuestionText,
    string Answer) : RemoteFrame;

// ---------------------------------------------------------------- git

public sealed record GitFileEntrySnapshot(string FilePath, GitFileStatus Status, bool IsStaged);

public sealed record GitStatusSnapshot(
    bool IsRepository,
    string? Branch,
    string? RemoteWebUrl,
    List<GitFileEntrySnapshot> Entries);

public sealed record GitStatusMsg(string ProjectId, GitStatusSnapshot Status) : RemoteFrame;

// ---------------------------------------------------------------- project settings

/// <summary>One checklist row, matching the project's persisted todo shape.</summary>
public sealed record RemoteTodoItem(string Text, bool IsCompleted);

public sealed record ProjectSettingsSnapshot(
    string? Name,
    string? Description,
    double? DescriptionFontSize,
    List<RemoteTodoItem> TodoItems);

public sealed record ProjectSettingsMsg(string ProjectId, ProjectSettingsSnapshot Settings) : RemoteFrame;

public sealed record UpdateTodoItemsCmd(string ProjectId, List<RemoteTodoItem> Items) : RemoteFrame;

// ---------------------------------------------------------------- files

public sealed record RemoteFileEntry(string Name, string RelativePath, bool IsDirectory);

/// <summary>Children of one directory. Fetched on expand, then pinned (see WatchDirectoryCmd).</summary>
public sealed record DirectoryListingMsg(
    string ProjectId,
    string RequestId,
    string RelativePath,
    List<RemoteFileEntry> Entries,
    string? Error) : RemoteFrame;

public sealed record ListDirectoryCmd(
    string ProjectId,
    string RequestId,
    string RelativePath) : RemoteFrame;

/// <summary>
/// Pin or unpin a directory. The server only watches materialized directories and pushes
/// invalidations for those, so the replica stays proportional to what the user is looking at
/// rather than to the size of the repository.
/// </summary>
public sealed record WatchDirectoryCmd(
    string ProjectId,
    string RelativePath,
    bool Watch) : RemoteFrame;

/// <summary>
/// File body. <c>Version</c> is an mtime+size token; the client caches under (path, version)
/// and re-fetches only when an invalidation carries a different one. In a tool where the agent
/// is continuously rewriting files, stale content is worse than slow content.
/// </summary>
public sealed record FileContentMsg(
    string ProjectId,
    string RequestId,
    string RelativePath,
    string Version,
    string? Text,
    byte[]? Bytes,
    bool IsBinary,
    bool Truncated,
    string? Error) : RemoteFrame;

public sealed record ReadFileCmd(
    string ProjectId,
    string RequestId,
    string RelativePath,
    string? KnownVersion) : RemoteFrame;

public sealed record ReadDiffCmd(
    string ProjectId,
    string RequestId,
    string RelativePath,
    bool Staged) : RemoteFrame;

/// <summary>
/// Paths whose content the client should consider stale. Rides the same watcher that drives
/// git status, so an agent edit invalidates the preview without polling.
/// </summary>
public sealed record FileInvalidatedMsg(
    string ProjectId,
    List<string> RelativePaths) : RemoteFrame;

// ---------------------------------------------------------------- agent input

public sealed record RemoteImageAttachment(string FileName, string MediaType, byte[] Data);

public sealed record SendMessageCmd(
    string MessageId,
    string ProjectId,
    string Text,
    List<RemoteImageAttachment>? Images) : RemoteFrame;

/// <summary>
/// Chat commands the panel handles itself rather than sending to the agent. <c>/clear</c>
/// clears both transcripts; <c>/compact</c> goes to the session. <c>/logs</c> is filtered out
/// client-side because it opens a folder, which is meaningless on the other machine.
/// </summary>
public sealed record LocalCommandCmd(string ProjectId, string Command) : RemoteFrame;

/// <summary>Client confirms it has applied everything up to <c>Seq</c>.</summary>
public sealed record AckCmd(string ProjectId, long Seq) : RemoteFrame;

/// <summary>
/// A bubble that was appended to the transcript outside the op stream — a user message or a
/// system notice. These are added directly to the message list rather than classified into a
/// <see cref="ChatOp"/>, so without this frame a client would simply never see them.
///
/// Carries the bubble's real id, which is how a client de-duplicates the echo of a message it
/// composed itself: it renders optimistically under an id it generated, and the server replays
/// that same id back.
/// </summary>
public sealed record TranscriptAppendMsg(
    string ProjectId,
    long Seq,
    SerializedChatMessage Message) : RemoteFrame;

/// <summary>
/// The client noticed a gap in the delta sequence (or wants to start clean) and is asking for a
/// fresh snapshot.
///
/// This is the recovery path in place of a server-side replay buffer. Re-sending the transcript
/// costs more bandwidth than replaying the missed deltas would, but it cannot be subtly wrong,
/// and the client reconciles the snapshot incrementally so the user sees no flash. Correctness
/// over cleverness on the path that only runs after something already went wrong.
/// </summary>
public sealed record ResyncCmd(string ProjectId) : RemoteFrame;
