using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using AgentDock.Models;
using AgentDock.Services.Remote;

namespace AgentDock.Controls;

/// <summary>
/// The chat panel's remote-session surface, kept in its own file so the already-large control
/// does not absorb another concern.
///
/// Two directions:
/// <list type="bullet">
/// <item><b>Publishing</b> (this machine is the server) — the op stream, permission prompts and
/// session state are exposed as events so a <see cref="RemoteProjectPublisher"/> can replicate
/// them, and the agent input is locked because the human is at the other machine.</item>
/// <item><b>Consuming</b> (this machine is the client) — ops and state that arrived over the
/// wire are applied through the same code path the local session uses, so a remote transcript
/// renders identically to a local one.</item>
/// </list>
/// </summary>
public partial class AiChatControl
{
    // ------------------------------------------------------------------ publishing (server)

    /// <summary>
    /// Fires after ops have been applied locally, in the same order. A remote client applying
    /// this stream ends in the same state as the server's own panel.
    /// </summary>
    public event Action<IReadOnlyList<ChatOp>>? OpsProduced;

    /// <summary>The agent is waiting on a human. In server mode only a client can answer.</summary>
    public event Action<ClaudePermissionRequest>? PermissionRequestRaised;

    /// <summary>The pending prompt is no longer live, so a client can drop a dead panel.</summary>
    public event Action? PermissionCleared;

    /// <summary>Raised when anything a remote client mirrors in the title/status changes.</summary>
    public event Action? RemoteStateChanged;

    private bool _agentInputLocked;

    /// <summary>
    /// True while this panel is being hosted for a remote client and the composer therefore
    /// belongs to that client.
    /// </summary>
    public bool IsAgentInputLocked => _agentInputLocked;

    /// <summary>
    /// Locks or unlocks the paths that can speak to the agent.
    ///
    /// <paramref name="composerLocked"/> is set for the whole time this machine is hosting, from
    /// Start Server rather than from the first client connecting: the point of server mode is that
    /// the keyboard is somewhere else, and a composer that still accepts text is a composer whose
    /// contents nobody will ever send. It is greyed out *and* explained — see
    /// <c>AgentLockNotice</c> — because a dead input with no reason given reads as a bug.
    ///
    /// <paramref name="promptsLocked"/> is set only once a client has actually attached. A blocked
    /// turn has to be answerable from somewhere: with nobody connected, that somewhere is here.
    ///
    /// The start panel stays live in both cases — bringing a new session up is not driving one, and
    /// it is the only way to add a tab to the hosted set. Stop Server likewise stays available,
    /// which is how a user who walks back to this machine reclaims it.
    /// </summary>
    public void SetAgentInputLocked(bool composerLocked, bool promptsLocked)
    {
        _agentInputLocked = composerLocked;

        InputPanel.IsEnabled = !composerLocked;
        PermissionPanel.IsEnabled = !promptsLocked;
        QuestionPanel.IsEnabled = !promptsLocked;

        AgentLockNotice.Visibility = composerLocked ? Visibility.Visible : Visibility.Collapsed;
        AgentLockNoticeText.Text = promptsLocked
            ? "Server mode — this session is being driven from the connected machine. Input is disabled here."
            : "Server mode — input is disabled here. Connect from another machine to drive this session.";

        RemoteStateChanged?.Invoke();
    }

    /// <summary>The prompt currently awaiting an answer, for a joining client's snapshot.</summary>
    public ClaudePermissionRequest? PendingPermissionRequest => _session?.PendingPermission;

    /// <summary>Session state as replicated to a client.</summary>
    public SessionStateSnapshot DescribeSessionState() => new(
        CurrentState,
        Model,
        AccountLabel,
        IsDangerousMode,
        HasBackgroundWork,
        StatusLabel,
        Stats.TotalCostUsd,
        Stats.InputTokens,
        Stats.OutputTokens,
        Stats.CacheReadInputTokens,
        Stats.CacheCreationInputTokens);

    /// <summary>
    /// Projects the live transcript onto its wire form. Transient bubbles (the waiting
    /// placeholder, the inactivity warning) are skipped: they hold live callbacks and mean
    /// nothing replayed.
    /// </summary>
    public List<SerializedChatMessage> SerializeTranscript()
    {
        var result = new List<SerializedChatMessage>(Messages.Count);

        foreach (var message in Messages)
        {
            var id = message.Id.ToString();

            switch (message)
            {
                case UserMessage user:
                    result.Add(new SerializedUserMessage(id, user.Text, EncodeImages(user.Images)));
                    break;

                case AssistantMessage assistant:
                    result.Add(new SerializedAssistantMessage(
                        id, assistant.Text, assistant.IsMarkdownView, assistant.HasMarkdownToggle));
                    break;

                case SystemMessage system:
                    result.Add(new SerializedSystemMessage(id, system.Text, system.IsWarning));
                    break;

                case ActivityMessage activity:
                    result.Add(new SerializedActivityMessage(
                        id, activity.Header, activity.IsExpanded, SerializeEntries(activity)));
                    break;
            }
        }

        return result;
    }

    private static List<SerializedActivityEntry> SerializeEntries(ActivityMessage activity)
    {
        var entries = new List<SerializedActivityEntry>(activity.Entries.Count);

        foreach (var entry in activity.Entries)
        {
            switch (entry)
            {
                case ActivityThinkingEntry thinking:
                    // Flush first: an in-flight buffer would otherwise lose its trailing delta
                    // in the snapshot, and a joining client would see a truncated thought.
                    thinking.Flush();
                    entries.Add(new SerializedThinkingEntry(thinking.Text));
                    break;

                case ToolEntry tool:
                    entries.Add(new SerializedToolEntry(tool.Name, tool.FormattedInput));
                    break;

                case SubagentEntry subagent:
                    entries.Add(new SerializedSubagentEntry(subagent.Label, subagent.Description));
                    break;

                case SubagentReportEntry report:
                    entries.Add(new SerializedSubagentReportEntry(report.Label, report.Model, report.Text));
                    break;
            }
        }

        return entries;
    }

    private List<byte[]>? EncodeImages(IReadOnlyList<ImageSource>? images)
    {
        if (images is not { Count: > 0 }) return null;

        var encoded = new List<byte[]>(images.Count);
        foreach (var image in images)
        {
            if (image is not System.Windows.Media.Imaging.BitmapSource bitmap) continue;

            try
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = new System.IO.MemoryStream();
                encoder.Save(stream);
                encoded.Add(stream.ToArray());
            }
            catch (Exception ex)
            {
                _log.Warn($"AiChatControl: could not encode attachment thumbnail for replication — {ex.Message}");
            }
        }

        return encoded.Count > 0 ? encoded : null;
    }

    /// <summary>The send queue and any scheduled messages, as replicated to a client.</summary>
    public List<QueuedMessageSnapshot> SerializeQueue()
        => QueuedMessages.Select(q => new QueuedMessageSnapshot(
                q.Id.ToString(),
                q.Text,
                q.NotBeforeUtc?.Ticks,
                q.StatusText))
            .ToList();

    // ------------------------------------------------------------------ remote input (server)

    /// <summary>
    /// Applies a message that arrived from the connected client. Goes through the same dispatch
    /// path as a locally typed message, so the transcript, the queue and the op stream cannot
    /// diverge between the two machines.
    /// </summary>
    public void ApplyRemoteSend(string text, IReadOnlyList<ImageAttachment>? images, Guid bubbleId)
    {
        if (_session == null) return;

        if (_session.State is ClaudeSessionState.Working or ClaudeSessionState.WaitingForPermission)
        {
            // Busy: fall into the existing queue rather than interleaving turns. The client sees
            // the queued item because the queue is part of the replicated state.
            _queue.Enqueue(new QueuedMessage
            {
                Text = text,
                Attachments = images ?? [],
                // Carried so the eventual dispatch reuses the id the client already rendered.
                BubbleId = bubbleId,
            });
            return;
        }

        Dispatch(text, images ?? [], thumbnails: null, bubbleId);
    }

    /// <summary>
    /// Answers the pending prompt on behalf of the client. The request id is echoed back and
    /// checked: a client answering a prompt the server has already resolved must be discarded,
    /// not applied to whatever prompt happens to be current.
    /// </summary>
    public bool ApplyRemoteAllow(string requestId)
    {
        if (!IsPendingRequest(requestId)) return false;

        _session?.AllowPermission();
        HidePermissionPanels();
        return true;
    }

    public bool ApplyRemoteDeny(string requestId, string? reason)
    {
        if (!IsPendingRequest(requestId)) return false;

        _session?.DenyPermission(reason);
        HidePermissionPanels();
        return true;
    }

    public bool ApplyRemoteAnswer(string requestId, string questionText, string answer)
    {
        if (!IsPendingRequest(requestId)) return false;

        _session?.AnswerQuestion(questionText, answer);
        _pendingQuestionText = null;
        HidePermissionPanels();
        return true;
    }

    private bool IsPendingRequest(string requestId)
    {
        var pending = _session?.PendingPermission;
        if (pending == null)
        {
            _log.Warn($"AiChatControl: remote answer for '{requestId}' ignored — nothing pending");
            return false;
        }

        if (pending.RequestId != requestId)
        {
            _log.Warn($"AiChatControl: remote answer for stale request '{requestId}' ignored " +
                      $"(current is '{pending.RequestId}')");
            return false;
        }

        return true;
    }

    private void HidePermissionPanels()
    {
        PermissionPanel.Visibility = Visibility.Collapsed;
        QuestionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
        PermissionCleared?.Invoke();
    }

    /// <summary>
    /// Runs a panel-local command asked for by the client. <c>/clear</c> is a display action on
    /// shared state, so clearing here also clears the client via the op stream ordering.
    /// </summary>
    public void ApplyRemoteLocalCommand(string command) => ExecuteLocalCommand(command);

    // ------------------------------------------------------------------ consuming (client)

    /// <summary>
    /// Puts this panel into remote mode: it renders a transcript owned by another machine and
    /// sends composed messages over the wire instead of to a local subprocess.
    /// </summary>
    public void EnterRemoteMode(IRemoteChatChannel channel, string projectId, string? displayRoot)
    {
        _remoteChannel = channel;
        _remoteProjectId = projectId;
        _remoteDisplayRoot = displayRoot;

        // Reveal the chat body, exactly as starting a local session does. Without this the
        // panel stays on the start screen and the composer is never realized — a remote head
        // with nothing to type into.
        StartPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        InputPanel.Visibility = Visibility.Visible;
    }

    private IRemoteChatChannel? _remoteChannel;
    private string? _remoteProjectId;

    /// <summary>True when this panel is a remote head rather than owning a local session.</summary>
    public bool IsRemote => _remoteChannel != null;

    /// <summary>
    /// Reconciles the transcript against a server snapshot.
    ///
    /// Deliberately not a clear-and-rebuild: on a reconnect the transcript has usually only
    /// grown, so the matching prefix is kept and just the tail is appended. Clearing would
    /// flash the whole panel and throw away scroll position for no reason. Stale-but-shown
    /// beats empty-then-repopulated.
    /// </summary>
    public void ApplyRemoteSnapshot(ProjectSnapshotMsg snapshot)
    {
        var incoming = snapshot.Transcript;

        // How much of what we already show is identical, in order, to the snapshot?
        var shared = 0;
        while (shared < Messages.Count && shared < incoming.Count
               && Guid.TryParse(incoming[shared].Id, out var incomingId)
               && Messages[shared].Id == incomingId)
        {
            shared++;
        }

        // Drop only the divergent tail, then append what is new.
        while (Messages.Count > shared)
            Messages.RemoveAt(Messages.Count - 1);

        for (var i = shared; i < incoming.Count; i++)
            Messages.Add(RehydrateMessage(incoming[i]));


        ApplyRemoteSessionState(snapshot.Session);

        if (snapshot.PendingPermission != null)
            ApplyRemotePermissionRequest(snapshot.PendingPermission);
        else
            HidePermissionPanels();
    }

    private ChatMessageVm RehydrateMessage(SerializedChatMessage message)
    {
        var id = Guid.TryParse(message.Id, out var parsed) ? parsed : Guid.NewGuid();

        switch (message)
        {
            case SerializedUserMessage user:
                return new UserMessage(id, user.Text, DecodeImages(user.Images));

            case SerializedAssistantMessage assistant:
            {
                // The markdown document is built lazily on this machine. Rendering cost belongs
                // to whoever renders, so the closure is rebuilt client-side rather than shipped.
                var style = (Style)FindResource("ChatMarkdownStyle");
                var text = assistant.Text;
                FlowDocumentCache? cache = null;
                return new AssistantMessage(
                    id,
                    text,
                    assistant.IsMarkdownView,
                    assistant.HasMarkdownToggle,
                    () => (cache ??= new FlowDocumentCache(
                            BuildAnswerDocument(text, style, _remoteDisplayRoot ?? "", OnRemoteFileLink)))
                        .Document);
            }

            case SerializedSystemMessage system:
                return new SystemMessage(id, system.Text, system.IsWarning);

            case SerializedActivityMessage activity:
            {
                var vm = new ActivityMessage(id) { Header = activity.Header, IsExpanded = activity.IsExpanded };
                foreach (var entry in activity.Entries)
                {
                    switch (entry)
                    {
                        case SerializedThinkingEntry thinking:
                            vm.AppendThinking(thinking.Text);
                            vm.CloseThinking();
                            break;
                        case SerializedToolEntry tool:
                            vm.AddTool(tool.Name, tool.FormattedInput);
                            break;
                        case SerializedSubagentEntry subagent:
                            vm.AddSubagent(subagent.Label, subagent.Description);
                            break;
                        case SerializedSubagentReportEntry report:
                            vm.AddSubagentReport(report.Label, report.Model, report.Text);
                            break;
                    }
                }
                vm.Freeze();
                return vm;
            }

            default:
                return new SystemMessage(id, "(unrecognised message)", isWarning: true);
        }
    }

    /// <summary>Memoizes the built document so toggling source/rendered does not re-parse.</summary>
    private sealed class FlowDocumentCache(System.Windows.Documents.FlowDocument document)
    {
        public System.Windows.Documents.FlowDocument Document { get; } = document;
    }

    private string? _remoteDisplayRoot;

    /// <summary>The server-side project root, used only to linkify paths in rendered markdown.</summary>
    public void SetRemoteDisplayRoot(string? root) => _remoteDisplayRoot = root;

    private void OnRemoteFileLink(string path) => FileReferenceClicked?.Invoke(path);

    private static IReadOnlyList<ImageSource>? DecodeImages(List<byte[]>? images)
    {
        if (images is not { Count: > 0 }) return null;

        var decoded = new List<ImageSource>(images.Count);
        foreach (var bytes in images)
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = new System.IO.MemoryStream(bytes);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                decoded.Add(bitmap);
            }
            catch
            {
                // A thumbnail that will not decode is not worth failing a transcript over.
            }
        }

        return decoded.Count > 0 ? decoded : null;
    }

    /// <summary>Applies a delta batch that arrived from the server.</summary>
    public void ApplyRemoteOps(IReadOnlyList<ChatOp> ops)
    {
        foreach (var op in ops)
            ApplyOp(op);
    }

    /// <summary>Mirrors server session state onto the local chrome (title, status, indicators).</summary>
    public void ApplyRemoteSessionState(SessionStateSnapshot state)
    {
        _remoteState = state;

        Stats.TotalCostUsd = state.TotalCostUsd;
        Stats.InputTokens = state.InputTokens;
        Stats.OutputTokens = state.OutputTokens;
        Stats.CacheReadInputTokens = state.CacheReadTokens;
        Stats.CacheCreationInputTokens = state.CacheCreationTokens;

        SetStatus(state.StatusLabel);
        SessionStateChanged?.Invoke(state.State);
        SessionStatsChanged?.Invoke(Stats);
        SessionBackgroundWorkChanged?.Invoke();

        if (state.Model != null)
            SessionModelChanged?.Invoke(state.Model);

        UpdateSendButtonEnabled();
    }

    private SessionStateSnapshot? _remoteState;

    /// <summary>Shows a prompt that arrived from the server, so the client can answer it.</summary>
    public void ApplyRemotePermissionRequest(PermissionRequestMsg request)
    {
        _remotePermissionRequestId = request.RequestId;

        try
        {
            using var document = JsonDocument.Parse(request.InputJson);
            OnPermissionRequested(new ClaudePermissionRequest
            {
                RequestId = request.RequestId,
                ToolName = request.ToolName,
                ToolUseId = "",
                Input = document.RootElement.Clone(),
            });
        }
        catch (Exception ex)
        {
            _log.Error($"AiChatControl: could not parse remote permission input for {request.ToolName}", ex);
            PermissionToolName.Text = $"Tool: {request.ToolName}";
            PermissionDetail.Text = request.InputJson;
            InputPanel.Visibility = Visibility.Collapsed;
            PermissionPanel.Visibility = Visibility.Visible;
        }
    }

    private string? _remotePermissionRequestId;

    /// <summary>The server resolved the prompt (or it died) — drop the panel.</summary>
    public void ApplyRemotePermissionResolved(string requestId)
    {
        if (_remotePermissionRequestId != null && _remotePermissionRequestId != requestId) return;

        _remotePermissionRequestId = null;
        PermissionPanel.Visibility = Visibility.Collapsed;
        QuestionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Marks the panel read-only because the link dropped. The transcript stays on screen and is
    /// marked stale rather than cleared — stale-but-shown beats empty-then-repopulated, and a
    /// reconnect then patches instead of flashing the whole panel.
    /// </summary>
    public void SetRemoteConnectionLost(string message)
    {
        InputPanel.IsEnabled = false;
        PermissionPanel.IsEnabled = false;
        QuestionPanel.IsEnabled = false;
        SetStatus(message);
    }

    public void SetRemoteConnectionRestored()
    {
        InputPanel.IsEnabled = true;
        PermissionPanel.IsEnabled = true;
        QuestionPanel.IsEnabled = true;
    }
}

/// <summary>
/// What a remote chat panel needs from its connection. Narrow on purpose: the panel composes
/// messages and answers prompts, and knows nothing about framing, reconnects or tokens.
/// </summary>
public interface IRemoteChatChannel
{
    /// <summary>
    /// <paramref name="messageId"/> is the id of the bubble the client already rendered. The server
    /// stores it on its own bubble and echoes it back, which is how the echo is de-duplicated.
    /// </summary>
    void SendMessage(string projectId, string messageId, string text, IReadOnlyList<ImageAttachment>? images);
    void AllowPermission(string projectId, string requestId);
    void DenyPermission(string projectId, string requestId, string? reason);
    void AnswerQuestion(string projectId, string requestId, string questionText, string answer);
    void SendLocalCommand(string projectId, string command);
}

/// <summary>
/// Replication plumbing for bubbles that bypass the op stream, and the client-side routing that
/// turns local composer actions into wire commands.
/// </summary>
public partial class AiChatControl
{
    /// <summary>A user or system bubble was appended directly. Server side only.</summary>
    public event Action<SerializedChatMessage>? TranscriptAppended;

    private void RaiseTranscriptAppended(ChatMessageVm message)
    {
        // In remote mode the bubble came from the server in the first place; re-publishing it
        // would echo back round the loop.
        if (IsRemote || TranscriptAppended == null) return;

        SerializedChatMessage? serialized = message switch
        {
            UserMessage user => new SerializedUserMessage(
                user.Id.ToString(), user.Text, EncodeImages(user.Images)),
            SystemMessage system => new SerializedSystemMessage(
                system.Id.ToString(), system.Text, system.IsWarning),
            _ => null,
        };

        if (serialized != null)
            TranscriptAppended.Invoke(serialized);
    }

    /// <summary>
    /// Appends a bubble pushed by the server, unless it is the echo of one this client already
    /// rendered optimistically. De-duplication is by id: the client generates the id, the server
    /// stores it on its own bubble, and it comes back unchanged.
    /// </summary>
    public void ApplyRemoteTranscriptAppend(SerializedChatMessage message)
    {
        if (Guid.TryParse(message.Id, out var id) && Messages.Any(m => m.Id == id))
            return;

        RemoveWaitingBubble();
        Messages.Add(RehydrateMessage(message));
    }

    /// <summary>
    /// Client-side send. Renders the user's bubble immediately under a locally generated id so
    /// the panel feels responsive, shows the waiting placeholder, and hands the text to the
    /// server — which is where the agent actually runs.
    /// </summary>
    private bool TrySendRemote(
        string text,
        IReadOnlyList<ImageAttachment> attachments,
        IReadOnlyList<ImageSource>? thumbnails)
    {
        if (_remoteChannel == null || _remoteProjectId == null) return false;

        var id = Guid.NewGuid();
        _lastMessageSentTime = DateTime.UtcNow;
        AddUserMessage(text, thumbnails, id);
        ShowWaitingBubble();

        _remoteChannel.SendMessage(_remoteProjectId, id.ToString(), text, attachments);
        return true;
    }

    /// <summary>Client-side answer to a prompt the server is blocked on.</summary>
    private bool TryAnswerRemote(bool allow, string? denyReason)
    {
        if (_remoteChannel == null || _remoteProjectId == null || _remotePermissionRequestId == null)
            return false;

        if (allow)
            _remoteChannel.AllowPermission(_remoteProjectId, _remotePermissionRequestId);
        else
            _remoteChannel.DenyPermission(_remoteProjectId, _remotePermissionRequestId, denyReason);

        PermissionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
        return true;
    }

    private bool TryAnswerQuestionRemote(string questionText, string answer)
    {
        if (_remoteChannel == null || _remoteProjectId == null || _remotePermissionRequestId == null)
            return false;

        _remoteChannel.AnswerQuestion(_remoteProjectId, _remotePermissionRequestId, questionText, answer);
        QuestionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>
    /// Client-side handling of the panel's own commands. <c>/logs</c> is dropped: it opens a
    /// folder, and the folder is on the other machine.
    /// </summary>
    private bool TryLocalCommandRemote(string command)
    {
        if (_remoteChannel == null || _remoteProjectId == null) return false;

        switch (command)
        {
            case "/clear":
                // A display action on shared state — clear here and on the server.
                ClearMessages();
                AddSystemMessage("Chat cleared.");
                _remoteChannel.SendLocalCommand(_remoteProjectId, command);
                return true;

            case "/logs":
                AddSystemMessage("/logs opens a folder on the server machine — not available remotely.", isWarning: true);
                return true;

            default:
                _remoteChannel.SendLocalCommand(_remoteProjectId, command);
                return true;
        }
    }
}
