using System.Text.Json;
using System.Windows.Threading;
using AgentDock.Controls;
using AgentDock.Models;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services.Remote;

/// <summary>
/// Replicates one server-side project tab to the connected client.
///
/// It owns the per-project sequence number and the delta pump. Coalescing happens here rather
/// than in the connection because only this class knows which ops may be merged: consecutive
/// thinking deltas collapse into one, while every structural op (tool call, turn complete,
/// finalize) is preserved in order. A heavy turn otherwise emits hundreds of frames a second.
///
/// Everything it reads lives on the UI thread, so all state gathering is marshalled there; the
/// connection's writer task is the only thing that touches the socket.
/// </summary>
public sealed class RemoteProjectPublisher : IDisposable
{
    private readonly ILogService _log;
    private readonly IProjectSettingsStore _projectSettings;
    private readonly RemoteFileAccess _files;
    private readonly Dispatcher _dispatcher;

    private readonly List<ChatOp> _pending = [];
    private readonly DispatcherTimer _pump;
    private readonly object _pendingLock = new();

    private long _seq;
    private bool _disposed;

    public string Id { get; }
    public string ProjectPath { get; }
    public AiChatControl Chat { get; }
    public GitStatusControl? Git { get; }

    /// <summary>Frames ready to go to the client. Raised on the UI thread.</summary>
    public event Action<RemoteFrame>? Publish;

    public RemoteProjectPublisher(
        string id,
        string projectPath,
        string displayName,
        AiChatControl chat,
        GitStatusControl? git,
        ILogService log,
        IProjectSettingsStore projectSettings,
        IPerfDiagnostics perf)
    {
        Id = id;
        ProjectPath = projectPath;
        DisplayName = displayName;
        Chat = chat;
        Git = git;
        _log = log;
        _projectSettings = projectSettings;
        _files = new RemoteFileAccess(projectPath, log, perf);
        _dispatcher = chat.Dispatcher;

        _pump = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RemoteProtocol.DeltaCoalesceWindow,
        };
        _pump.Tick += (_, _) => FlushPending();

        Chat.OpsProduced += OnOpsProduced;
        Chat.TranscriptAppended += OnTranscriptAppended;
        Chat.SessionStateChanged += OnSessionStateChanged;
        Chat.SessionStatsChanged += _ => PublishSessionState();
        Chat.SessionStatusChanged += PublishSessionState;
        Chat.SessionBackgroundWorkChanged += PublishSessionState;
        Chat.PermissionRequestRaised += OnPermissionRequested;
        Chat.PermissionCleared += OnPermissionCleared;

        if (Git != null)
        {
            Git.StatusReplicated += PublishGitStatus;
            Git.FileSystemChanged += OnFileSystemChanged;
        }
    }

    public string DisplayName { get; }

    public string RelativeRootPath => "";

    // ------------------------------------------------------------------ descriptors + snapshots

    public RemoteProjectDescriptor Describe()
    {
        var settings = _projectSettings.Load(ProjectPath);
        byte[]? iconBytes = null;

        // A file-based icon lives inside the server's project folder, so an icon *path* is
        // meaningless to the client. Ship the bytes instead.
        if (settings.Icon is { } icon && !BuiltInIcons.IsBuiltIn(icon))
        {
            try
            {
                var iconPath = System.IO.Path.IsPathRooted(icon)
                    ? icon
                    : System.IO.Path.Combine(ProjectPath, icon);

                if (System.IO.File.Exists(iconPath))
                    iconBytes = System.IO.File.ReadAllBytes(iconPath);
            }
            catch (Exception ex)
            {
                _log.Warn($"RemotePublisher[{DisplayName}]: could not read project icon — {ex.Message}");
            }
        }

        return new RemoteProjectDescriptor(
            Id,
            DisplayName,
            ProjectPath,
            settings.Icon,
            settings.IconColor,
            iconBytes,
            Git?.IsGitRepository ?? false);
    }

    public ProjectSnapshotMsg Snapshot()
    {
        // Flush first so the snapshot and the sequence number agree: a snapshot taken with
        // deltas still buffered would be replayed *behind* ops the client already applied.
        FlushPending();

        var pending = Chat.PendingPermissionRequest;

        return new ProjectSnapshotMsg(
            Id,
            Volatile.Read(ref _seq),
            Chat.SerializeTranscript(),
            Chat.DescribeSessionState(),
            Git?.DescribeStatus(),
            DescribeSettings(),
            Chat.SerializeQueue(),
            pending == null ? null : ToPermissionMsg(pending));
    }

    private ProjectSettingsSnapshot DescribeSettings()
    {
        var settings = _projectSettings.Load(ProjectPath);
        return new ProjectSettingsSnapshot(
            settings.Name,
            settings.Description,
            settings.DescriptionFontSize,
            settings.TodoItems?.Select(t => new RemoteTodoItem(t.Text, t.IsCompleted)).ToList() ?? []);
    }

    private PermissionRequestMsg ToPermissionMsg(ClaudePermissionRequest request) => new(
        Id,
        request.RequestId,
        request.ToolName,
        request.Input.ValueKind == JsonValueKind.Undefined ? "{}" : request.Input.GetRawText());

    // ------------------------------------------------------------------ live replication

    private void OnOpsProduced(IReadOnlyList<ChatOp> ops)
    {
        lock (_pendingLock)
        {
            foreach (var op in ops)
                AppendCoalesced(op);
        }

        if (!_pump.IsEnabled) _pump.Start();
    }

    /// <summary>
    /// Merges a thinking delta into the previous one when possible. Only text appends are ever
    /// merged; anything structural is appended as-is so ordering and completeness survive.
    /// </summary>
    private void AppendCoalesced(ChatOp op)
    {
        if (op is AppendThinkingOp append && _pending.Count > 0 &&
            _pending[^1] is AppendThinkingOp previous)
        {
            _pending[^1] = new AppendThinkingOp(previous.Text + append.Text);
            return;
        }

        _pending.Add(op);
    }

    private void FlushPending()
    {
        List<ChatOp> batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
            {
                _pump.Stop();
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
        }

        var seq = Interlocked.Increment(ref _seq);
        Publish?.Invoke(new ProjectDeltaMsg(Id, seq, batch));
    }

    private void OnTranscriptAppended(SerializedChatMessage message)
    {
        // Ordering matters: a user bubble must land after the ops that preceded it.
        FlushPending();
        var seq = Interlocked.Increment(ref _seq);
        Publish?.Invoke(new TranscriptAppendMsg(Id, seq, message));
    }

    private void OnSessionStateChanged(ClaudeSessionState state) => PublishSessionState();

    private void PublishSessionState()
    {
        if (_disposed) return;
        Publish?.Invoke(new SessionStateMsg(Id, Chat.DescribeSessionState()));
    }

    private void OnPermissionRequested(ClaudePermissionRequest request)
    {
        FlushPending();
        Publish?.Invoke(ToPermissionMsg(request));
    }

    private void OnPermissionCleared()
    {
        var pending = Chat.PendingPermissionRequest;
        Publish?.Invoke(new PermissionResolvedMsg(Id, pending?.RequestId ?? ""));
    }

    private void PublishGitStatus()
    {
        if (_disposed || Git == null) return;
        Publish?.Invoke(new GitStatusMsg(Id, Git.DescribeStatus()));
    }

    /// <summary>
    /// The watcher that drives git status also tells us content went stale. Riding it means the
    /// client's file cache is invalidated by the agent's own edits without any polling.
    /// </summary>
    private void OnFileSystemChanged()
    {
        if (_disposed || Git == null) return;

        var changed = Git.DescribeStatus().Entries
            .Select(e => e.FilePath)
            .Distinct()
            .ToList();

        if (changed.Count > 0)
            Publish?.Invoke(new FileInvalidatedMsg(Id, changed));

        Publish?.Invoke(new ProjectSettingsMsg(Id, DescribeSettings()));
    }

    // ------------------------------------------------------------------ command handling

    /// <summary>
    /// Applies a client command. Called on the UI thread because everything it touches is UI
    /// state. Returns a frame to send back, or null when the command has no direct reply.
    /// </summary>
    public RemoteFrame? HandleCommand(RemoteFrame command)
    {
        switch (command)
        {
            case SendMessageCmd send:
            {
                var attachments = ToAttachments(send.Images);
                var bubbleId = Guid.TryParse(send.MessageId, out var parsed) ? parsed : Guid.NewGuid();
                Chat.ApplyRemoteSend(send.Text, attachments, bubbleId);
                return null;
            }

            case AllowPermissionCmd allow:
                Chat.ApplyRemoteAllow(allow.RequestId);
                return null;

            case DenyPermissionCmd deny:
                Chat.ApplyRemoteDeny(deny.RequestId, deny.Reason);
                return null;

            case AnswerQuestionCmd answer:
                Chat.ApplyRemoteAnswer(answer.RequestId, answer.QuestionText, answer.Answer);
                return null;

            case LocalCommandCmd local:
                Chat.ApplyRemoteLocalCommand(local.Command);
                return null;

            case ListDirectoryCmd list:
                return _files.ListDirectory(Id, list.RequestId, list.RelativePath);

            case ReadFileCmd read:
                return _files.ReadFile(Id, read.RequestId, read.RelativePath, read.KnownVersion);

            case ReadDiffCmd diff:
                return _files.ReadDiff(Id, diff.RequestId, diff.RelativePath, diff.Staged);

            case UpdateTodoItemsCmd todo:
                // Todo items live in the *project's* .agentdock/settings.json, which is on this
                // machine — so a remote edit has to round-trip here or it would be written nowhere.
                _projectSettings.Update(ProjectPath, s => s.TodoItems =
                    todo.Items.Select(i => new TodoItem { Text = i.Text, IsCompleted = i.IsCompleted }).ToList());
                return new ProjectSettingsMsg(Id, DescribeSettings());

            case ResyncCmd:
                return Snapshot();

            default:
                _log.Warn($"RemotePublisher[{DisplayName}]: unhandled command {command.GetType().Name}");
                return null;
        }
    }

    private List<ImageAttachment>? ToAttachments(List<RemoteImageAttachment>? images)
    {
        if (images is not { Count: > 0 }) return null;

        var result = new List<ImageAttachment>(images.Count);
        foreach (var image in images)
        {
            if (image.Data.Length > RemoteProtocol.MaxAttachmentBytes)
            {
                _log.Warn($"RemotePublisher[{DisplayName}]: rejected oversized attachment " +
                          $"'{image.FileName}' ({image.Data.Length} bytes)");
                continue;
            }

            result.Add(new ImageAttachment(image.MediaType, Convert.ToBase64String(image.Data)));
        }

        return result.Count > 0 ? result : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pump.Stop();

        Chat.OpsProduced -= OnOpsProduced;
        Chat.TranscriptAppended -= OnTranscriptAppended;
        Chat.SessionStateChanged -= OnSessionStateChanged;
        Chat.SessionStatusChanged -= PublishSessionState;
        Chat.SessionBackgroundWorkChanged -= PublishSessionState;
        Chat.PermissionRequestRaised -= OnPermissionRequested;
        Chat.PermissionCleared -= OnPermissionCleared;

        if (Git != null)
        {
            Git.StatusReplicated -= PublishGitStatus;
            Git.FileSystemChanged -= OnFileSystemChanged;
        }
    }
}
