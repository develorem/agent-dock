using System.Windows;
using System.Windows.Controls;
using AgentDock.Controls;
using AgentDock.Models;
using AgentDock.Services.Remote;
using AgentDock.Windows;

namespace AgentDock;

/// <summary>
/// Remote session wiring for the main window: hosting this machine's sessions, and being the
/// remote head for another machine's.
///
/// The two roles are mutually exclusive in practice — connecting replaces the workspace, and
/// hosting locks agent input — but they share the status strip and the menu, so they live together.
/// </summary>
public partial class MainWindow : IRemoteServerProjects
{
    /// <summary>Server-assigned project handle, for projects that live on another machine.</summary>
    private readonly Dictionary<ProjectInfo, string> _remoteProjectIds = [];

    /// <summary>The group holding the remote machine's tabs, named after that machine.</summary>
    private string? _remoteGroupId;

    private string? GetRemoteProjectId(ProjectInfo project)
        => _remoteProjectIds.TryGetValue(project, out var id) ? id : null;

    private ProjectInfo? FindRemoteProject(string remoteId)
        => _remoteProjectIds.FirstOrDefault(kv => kv.Value == remoteId).Key;

    /// <summary>True while this window is a remote head for another machine.</summary>
    private bool IsRemoteSession => _remoteGroupId != null;

    // ================================================================== server role

    /// <inheritdoc />
    public event Action? ActiveProjectsChanged;

    /// <inheritdoc />
    public IReadOnlyList<RemoteProjectPublisher> CreatePublishers()
    {
        var publishers = new List<RemoteProjectPublisher>();

        // "Active" means the same thing it means for the dynamic Active Projects group — a live
        // agent session. Reusing that definition rather than inventing a second one.
        foreach (var project in GetActiveProjects())
        {
            if (!_projectChatControls.TryGetValue(project, out var chat)) continue;

            _projectGitControls.TryGetValue(project, out var git);

            publishers.Add(new RemoteProjectPublisher(
                id: project.FolderPath,
                projectPath: project.FolderPath,
                displayName: project.DisplayName,
                chat: chat,
                git: git,
                log: _log,
                projectSettings: _projectSettings,
                perf: _perf));
        }

        return publishers;
    }

    /// <summary>True while this machine is hosting its sessions for a remote Agent Dock.</summary>
    private bool IsHostingRemote => _remoteServer.IsRunning;

    /// <summary>
    /// The lock currently in force, remembered so a tab opened *while* hosting starts out locked
    /// too. Without this, adding a project after Start Server would hand back a live composer on
    /// a machine that is supposed to be driven from elsewhere.
    /// </summary>
    private (bool Composer, bool Prompts) _agentInputLock;

    /// <inheritdoc />
    public void SetAgentInputLocked(bool composerLocked, bool promptsLocked)
    {
        _agentInputLock = (composerLocked, promptsLocked);

        foreach (var chat in _projectChatControls.Values)
            chat.SetAgentInputLocked(composerLocked, promptsLocked);
    }

    /// <summary>Applies the current lock to a freshly built chat panel. Called from AddProjectCore.</summary>
    private void ApplyAgentInputLock(AiChatControl chat)
    {
        if (!_agentInputLock.Composer && !_agentInputLock.Prompts) return;

        chat.SetAgentInputLocked(_agentInputLock.Composer, _agentInputLock.Prompts);
    }

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        if (IsRemoteSession)
        {
            ThemedMessageBox.Show(this,
                "Disconnect from the remote session before hosting this machine.",
                "Already connected");
            return;
        }

        var result = Windows.ServerModeDialog.Show(this, _appSettings, RemoteProtocol.DefaultPort);
        if (result == null) return;

        var outcome = _remoteServer.Start(result.Port, result.Scope, this, Dispatcher);
        if (!outcome.Success)
        {
            ThemedMessageBox.Show(this, outcome.Error ?? "The server could not start.", "Server Mode");
            return;
        }

        _log.Info($"MainWindow: server mode started on port {outcome.Port} " +
                  $"({_remoteServer.ConnectEndpoint})");

        // Server mode publishes exactly the projects with a live session, so that is what the
        // window should be showing: switching to the Active Projects view makes the local screen
        // agree with what the client will see, and puts the Stop Server control next to it.
        if (GetActiveProjects().Count > 0)
            SetActiveGroup(ActiveProjectsGroupId);

        RefreshRemoteUi();
    }

    private void StopServer_Click(object sender, RoutedEventArgs e)
    {
        _remoteServer.Stop();
        RefreshRemoteUi();
    }

    /// <summary>
    /// The one-click exit from server mode, wherever it is clicked from — the title-bar badge, the
    /// pill beside the Active tab, or the status strip. Whichever role is live is the one it ends,
    /// so the user never has to work out which of the two they are in before they can get out.
    /// </summary>
    private void ExitRemoteMode_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteServer.IsRunning)
            StopServer_Click(sender, e);
        else if (_remoteClient.State != RemoteClientState.Disconnected || IsRemoteSession)
            DisconnectRemote_Click(sender, e);
    }

    private void KickClient_Click(object sender, RoutedEventArgs e)
    {
        _remoteServer.KickClient();
        RefreshRemoteUi();
    }

    private void RegenerateCode_Click(object sender, RoutedEventArgs e)
    {
        // Regenerating invalidates the session token, so a connected client is dropped. Worth a
        // confirmation rather than a surprise.
        if (_remoteServer.HasClient)
        {
            var confirmed = ThemedMessageBox.Show(this,
                $"'{_remoteServer.ConnectedClientName}' is connected and will be disconnected. Regenerate the pairing code?",
                "Regenerate Pairing Code",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

            if (!confirmed) return;
        }

        _remoteServer.RegenerateCode();
        RefreshRemoteUi();
    }

    private void RemoteCopyCode_Click(object sender, RoutedEventArgs e)
    {
        var code = _remoteServer.PairingCodeValue;
        if (string.IsNullOrEmpty(code)) return;

        try
        {
            Clipboard.SetText(PairingCode.Format(code));
        }
        catch (Exception ex)
        {
            _log.Warn($"MainWindow: could not copy pairing code — {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the <c>address:port</c> the other machine has to type. Separate from the pairing-code
    /// button because the two go into different fields on the client, and pasting one where the
    /// other belongs is the most likely way a first pairing fails.
    /// </summary>
    private void RemoteCopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = _remoteServer.ConnectEndpoint;
        if (string.IsNullOrEmpty(endpoint)) return;

        try
        {
            Clipboard.SetText(endpoint);
        }
        catch (Exception ex)
        {
            _log.Warn($"MainWindow: could not copy server address — {ex.Message}");
        }
    }

    private void OnServerAuthFailure(string note)
        => _log.Warn($"MainWindow: {note}");

    // ================================================================== client role

    private void ConnectRemote_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteServer.IsRunning)
        {
            ThemedMessageBox.Show(this,
                "Stop server mode before connecting to another machine.",
                "Server Mode Active");
            return;
        }

        // Connecting is the peer of opening a workspace, so the current one is closed first —
        // there is then no ambiguity about which machine a panel refers to.
        // Only warn about losing real local work. If the open projects are a previous remote
        // group (typically a dead one after a kick or a server stop), there is nothing to lose
        // and asking would be noise.
        if (IsRemoteSession)
            TearDownRemoteSession();
        else if (_projects.Count > 0 && !ConfirmCloseWorkspaceForRemote())
            return;

        if (!Windows.RemoteConnectDialog.Show(this, _remoteClient, _appSettings)) return;

        _log.Info($"MainWindow: connected to '{_remoteClient.ServerName}'");
        RefreshRemoteUi();
    }

    private bool ConfirmCloseWorkspaceForRemote()
        => ThemedMessageBox.Show(this,
            "Connecting to a remote server closes the projects open here. Continue?",
            "Connect to Server",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void DisconnectRemote_Click(object sender, RoutedEventArgs e)
    {
        _remoteClient.Disconnect();
        TearDownRemoteSession();
        RefreshRemoteUi();
    }

    /// <summary>Routes a server frame to whichever panel owns it.</summary>
    private void OnRemoteFrame(RemoteFrame frame)
    {
        switch (frame)
        {
            case HostSnapshotMsg host:
                ApplyHostSnapshot(host);
                break;

            case ProjectAddedMsg added:
                ApplyProjectAdded(added.Project);
                break;

            case ProjectRemovedMsg removed:
                ApplyProjectRemoved(removed);
                break;

            case ProjectSnapshotMsg snapshot:
                WithRemoteChat(snapshot.ProjectId, chat => chat.ApplyRemoteSnapshot(snapshot));
                WithRemoteGit(snapshot.ProjectId, git =>
                {
                    if (snapshot.Git != null) git.ApplyRemoteStatus(snapshot.Git);
                });
                ApplyRemoteSettings(snapshot.ProjectId, snapshot.Settings);
                break;

            case ProjectDeltaMsg delta:
                WithRemoteChat(delta.ProjectId, chat => chat.ApplyRemoteOps(delta.Ops));
                break;

            case TranscriptAppendMsg append:
                WithRemoteChat(append.ProjectId, chat => chat.ApplyRemoteTranscriptAppend(append.Message));
                break;

            case SessionStateMsg state:
                WithRemoteChat(state.ProjectId, chat => chat.ApplyRemoteSessionState(state.State));
                break;

            case PermissionRequestMsg permission:
                WithRemoteChat(permission.ProjectId, chat => chat.ApplyRemotePermissionRequest(permission));
                break;

            case PermissionResolvedMsg resolved:
                WithRemoteChat(resolved.ProjectId, chat => chat.ApplyRemotePermissionResolved(resolved.RequestId));
                break;

            case GitStatusMsg git:
                WithRemoteGit(git.ProjectId, control => control.ApplyRemoteStatus(git.Status));
                break;

            case ProjectSettingsMsg settings:
                ApplyRemoteSettings(settings.ProjectId, settings.Settings);
                break;

            case DirectoryListingMsg listing:
                WithRemoteExplorer(listing.ProjectId, explorer => explorer.ApplyRemoteListing(listing));
                break;

            case FileContentMsg content:
                WithRemotePreview(content.ProjectId, preview => preview.ApplyRemoteContent(content));
                break;

            case FileInvalidatedMsg invalidated:
                WithRemotePreview(invalidated.ProjectId,
                    preview => preview.ApplyRemoteInvalidation(invalidated.RelativePaths));
                WithRemoteExplorer(invalidated.ProjectId,
                    explorer => explorer.ApplyRemoteInvalidation(invalidated.RelativePaths));
                break;
        }
    }

    /// <summary>
    /// Builds (or rebuilds) the remote group and its tabs. One group, named after the server,
    /// holding one tab per live session there.
    /// </summary>
    private void ApplyHostSnapshot(HostSnapshotMsg host)
    {
        _log.Info($"MainWindow: host snapshot from '{host.HostName}' with {host.Projects.Count} project(s)");

        if (_remoteGroupId == null)
        {
            _remoteGroupId = $"__remote_{Guid.NewGuid():N}";
            _groups.Add(new ProjectGroup
            {
                Id = _remoteGroupId,
                Name = host.HostName,
                Order = _groups.Count,
                Icon = "cloud",
            });
        }
        else
        {
            var group = _groups.FirstOrDefault(g => g.Id == _remoteGroupId);
            if (group != null) group.Name = host.HostName;
        }

        // Drop tabs that are no longer active on the server, then add the new ones. Existing tabs
        // are kept so a rebuild does not throw away the panel the user is reading.
        var wanted = host.Projects.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (project, id) in _remoteProjectIds.ToList())
        {
            if (wanted.Contains(id)) continue;
            _remoteProjectIds.Remove(project);
            CloseProject(project);
        }

        foreach (var descriptor in host.Projects)
            if (FindRemoteProject(descriptor.Id) == null)
                ApplyProjectAdded(descriptor);

        SetActiveGroup(_remoteGroupId);
        RefreshMetaTabBar();
        UpdateProjectStripVisibility();
        RefreshRemoteUi();
    }

    private void ApplyProjectAdded(RemoteProjectDescriptor descriptor)
    {
        if (FindRemoteProject(descriptor.Id) != null) return;

        var project = new ProjectInfo
        {
            FolderPath = descriptor.DisplayRoot,
            CustomName = descriptor.DisplayName,
            GroupId = _remoteGroupId,
        };

        // Registered before the layout is built: CreateProjectDockingLayout consults this to
        // decide whether the panels talk to the disk or to the wire.
        _remoteProjectIds[project] = descriptor.Id;

        AddProjectCore(project, layoutXml: null);
    }

    private void ApplyProjectRemoved(ProjectRemovedMsg removed)
    {
        var project = FindRemoteProject(removed.ProjectId);
        if (project == null) return;

        // The tab is not yanked away — someone may be reading it. It is marked and left in place,
        // and cleaned up on the next connect.
        if (_projectChatControls.TryGetValue(project, out var chat))
            chat.SetRemoteConnectionLost($"Closed on the server ({removed.Reason}).");
    }

    private void ApplyRemoteSettings(string projectId, ProjectSettingsSnapshot settings)
    {
        var project = FindRemoteProject(projectId);
        if (project == null) return;

        // The notifications for this project belong to this machine now — the host is silent while
        // it hosts — so its toggles come across with the settings rather than being re-read from a
        // folder that lives on the other box.
        _remoteSoundSettings[project] = new SoundToggles(
            settings.SoundOnSessionStart, settings.SoundOnAgentWaiting, settings.SoundOnSessionEnd);

        if (_projectTodoListControls.TryGetValue(project, out var todo))
            todo.ApplyRemoteItems(settings.TodoItems);

        if (_projectDescriptionControls.TryGetValue(project, out var description))
            description.ApplyRemoteSettings(settings.Description, settings.DescriptionFontSize);
    }

    private void WithRemoteChat(string projectId, Action<AiChatControl> action)
    {
        var project = FindRemoteProject(projectId);
        if (project != null && _projectChatControls.TryGetValue(project, out var chat))
            action(chat);
    }

    private void WithRemoteGit(string projectId, Action<GitStatusControl> action)
    {
        var project = FindRemoteProject(projectId);
        if (project != null && _projectGitControls.TryGetValue(project, out var git))
            action(git);
    }

    private void WithRemoteExplorer(string projectId, Action<FileExplorerControl> action)
    {
        var project = FindRemoteProject(projectId);
        if (project != null && _projectExplorerControls.TryGetValue(project, out var explorer))
            action(explorer);
    }

    private void WithRemotePreview(string projectId, Action<FilePreviewControl> action)
    {
        var project = FindRemoteProject(projectId);
        if (project != null && _projectPreviewControls.TryGetValue(project, out var preview))
            action(preview);
    }

    /// <summary>Last reason the link dropped, kept so the strip can keep explaining itself.</summary>
    private string? _lastRemoteDisconnectMessage;

    private void OnRemoteDisconnected(DisconnectReason reason, string? message)
    {
        var text = message ?? DisconnectReasonText.Describe(reason);
        _lastRemoteDisconnectMessage = text;
        _log.Info($"MainWindow: remote disconnected — {reason}: {text}");

        // Panels go read-only and say why. The transcript stays on screen: stale-but-shown beats
        // empty, and a reconnect then patches rather than flashing the whole window.
        foreach (var (project, _) in _remoteProjectIds)
            if (_projectChatControls.TryGetValue(project, out var chat))
                chat.SetRemoteConnectionLost(text);

        RefreshRemoteUi();
    }

    private void OnRemoteClientStateChanged()
    {
        if (_remoteClient.State == RemoteClientState.Connected)
        {
            _lastRemoteDisconnectMessage = null;
            foreach (var (project, _) in _remoteProjectIds)
                if (_projectChatControls.TryGetValue(project, out var chat))
                    chat.SetRemoteConnectionRestored();
        }

        RefreshRemoteUi();
    }

    private void TearDownRemoteSession()
    {
        foreach (var (project, _) in _remoteProjectIds.ToList())
            CloseProject(project);

        _remoteProjectIds.Clear();

        if (_remoteGroupId != null)
        {
            _groups.RemoveAll(g => g.Id == _remoteGroupId);
            _remoteGroupId = null;
        }

        RefreshMetaTabBar();
        UpdateProjectStripVisibility();
    }

    // ================================================================== shared chrome

    /// <summary>
    /// Keeps the status strip and the Remote menu in step with both roles. Called from every state
    /// transition rather than being recomputed in each handler, so there is one description of what
    /// the UI should look like.
    /// </summary>
    private void RefreshRemoteUi()
    {
        var hosting = _remoteServer.IsRunning;
        var connected = _remoteClient.State != RemoteClientState.Disconnected;

        StartServerMenuItem.IsEnabled = !hosting && !connected;
        StopServerMenuItem.IsEnabled = hosting;
        RegenerateCodeMenuItem.IsEnabled = hosting;
        KickClientMenuItem.IsEnabled = _remoteServer.HasClient;
        ConnectRemoteMenuItem.IsEnabled = !hosting && !connected;
        DisconnectRemoteMenuItem.IsEnabled = connected;

        if (hosting)
        {
            RemoteStrip.Visibility = Visibility.Visible;
            RemoteStripCopyButton.Visibility = Visibility.Visible;
            RemoteStripAddressButton.Visibility = Visibility.Visible;
            RemoteStripStopButton.Visibility = Visibility.Visible;
            RemoteStripDisconnectButton.Visibility = Visibility.Collapsed;
            RemoteStripKickButton.Visibility = _remoteServer.HasClient ? Visibility.Visible : Visibility.Collapsed;

            var code = _remoteServer.PairingCodeValue;
            RemoteStripCode.Text = code == null ? "" : PairingCode.Format(code);

            // The address, not the machine name: the name is what this box calls itself, and it
            // only resolves on the other machine if something on the network is publishing it.
            var endpoint = _remoteServer.ConnectEndpoint ?? $"{_remoteServer.HostName}:{_remoteServer.Port}";

            RemoteStripText.Text = _remoteServer.HasClient
                ? $"Server mode · {endpoint} · " +
                  $"{_remoteServer.ConnectedClientName} is connected and driving these sessions · code"
                : $"Server mode · {endpoint} " +
                  $"({_remoteServer.BoundAddressDescription}) · waiting for a client · code";

            RemoteStrip.ToolTip = DescribeServerAddresses();
        }
        else if (connected)
        {
            RemoteStrip.Visibility = Visibility.Visible;
            RemoteStripCopyButton.Visibility = Visibility.Collapsed;
            RemoteStripAddressButton.Visibility = Visibility.Collapsed;
            RemoteStripKickButton.Visibility = Visibility.Collapsed;
            RemoteStripStopButton.Visibility = Visibility.Collapsed;
            RemoteStripDisconnectButton.Visibility = Visibility.Visible;
            RemoteStripCode.Text = "";
            RemoteStrip.ToolTip = null;

            RemoteStripText.Text = _remoteClient.State switch
            {
                RemoteClientState.Reconnecting =>
                    $"Reconnecting to {_remoteClient.ServerName ?? _remoteClient.Endpoint}…",
                RemoteClientState.Connected =>
                    $"Remote session · driving {_remoteClient.ServerName} ({_remoteClient.Endpoint}) · " +
                    "all work runs on that machine",
                _ => $"Connecting to {_remoteClient.Endpoint}…",
            };
        }
        else if (IsRemoteSession)
        {
            // The link is gone but the remote group is still on screen. Keep the strip up with
            // the reason — collapsing it would leave a window full of another machine's tabs and
            // no explanation of why nothing responds. Disconnect tears the group down.
            RemoteStrip.Visibility = Visibility.Visible;
            RemoteStripCopyButton.Visibility = Visibility.Collapsed;
            RemoteStripAddressButton.Visibility = Visibility.Collapsed;
            RemoteStripKickButton.Visibility = Visibility.Collapsed;
            RemoteStripStopButton.Visibility = Visibility.Collapsed;
            RemoteStripDisconnectButton.Visibility = Visibility.Visible;
            RemoteStripCode.Text = "";
            RemoteStrip.ToolTip = null;
            RemoteStripText.Text = _lastRemoteDisconnectMessage ?? "Disconnected from the server.";
        }
        else
        {
            RemoteStrip.Visibility = Visibility.Collapsed;
            RemoteStrip.ToolTip = null;
        }

        RefreshRemoteBadges();
        UpdateWindowTitleForRemote();
    }

    /// <summary>
    /// Every address the client could use, for the strip's tooltip. A developer box usually has
    /// more than one and only the machine's owner can say which network the other machine is on,
    /// so the primary is shown inline and the rest are one hover away.
    /// </summary>
    private string DescribeServerAddresses()
    {
        var addresses = _remoteServer.ConnectAddresses;
        var port = _remoteServer.Port;

        if (addresses.Count == 0)
            return $"Listening on port {port}.";

        var lines = addresses.Select(a => $"    {a}:{port}");
        return addresses.Count == 1
            ? $"Connect from the other machine using:\n{string.Join('\n', lines)}"
            : "Connect from the other machine using one of:\n" + string.Join('\n', lines) +
              "\n\nThe first is the most likely to work; try another if this machine is on more " +
              "than one network.";
    }

    /// <summary>
    /// Drives the two always-on-screen remote indicators: the title-bar badge and the pill beside
    /// the Active tab. Both say the same thing and both exit the mode when clicked — the status
    /// strip alone sits at the bottom edge of a maximised window, which is exactly where a user
    /// who has been away from the machine will not look.
    /// </summary>
    private void RefreshRemoteBadges()
    {
        var hosting = _remoteServer.IsRunning;
        var connected = _remoteClient.State != RemoteClientState.Disconnected || IsRemoteSession;

        if (hosting)
        {
            var endpoint = _remoteServer.ConnectEndpoint ?? $"port {_remoteServer.Port}";

            RemoteModeBadge.Visibility = Visibility.Visible;
            RemoteModeBadgeText.Text = _remoteServer.HasClient ? "SERVER MODE · IN USE" : "SERVER MODE";
            RemoteModeBadge.ToolTip = _remoteServer.HasClient
                ? $"Hosting on {endpoint}. '{_remoteServer.ConnectedClientName}' is driving these " +
                  "sessions and input is disabled here. Click to stop hosting."
                : $"Hosting on {endpoint}, waiting for a client. Input is disabled here. " +
                  "Click to stop hosting.";

            ServerModePill.Visibility = Visibility.Visible;
            ServerModePillText.Text = _remoteServer.HasClient
                ? $"Server mode · {_remoteServer.ConnectedClientName}"
                : "Server mode";
            ServerModePillButton.Content = "Stop";
            ServerModePillButton.ToolTip = "Stop hosting and re-enable input on this machine";
        }
        else if (connected)
        {
            RemoteModeBadge.Visibility = Visibility.Visible;
            RemoteModeBadgeText.Text = "REMOTE";
            RemoteModeBadge.ToolTip =
                $"Driving {_remoteClient.ServerName ?? _remoteClient.Endpoint}. All work runs on " +
                "that machine. Click to disconnect.";

            ServerModePill.Visibility = Visibility.Visible;
            ServerModePillText.Text = $"Remote · {_remoteClient.ServerName ?? _remoteClient.Endpoint}";
            ServerModePillButton.Content = "Disconnect";
            ServerModePillButton.ToolTip = "Disconnect and close this machine's remote tabs";
        }
        else
        {
            RemoteModeBadge.Visibility = Visibility.Collapsed;
            ServerModePill.Visibility = Visibility.Collapsed;
        }

        // Hosting can force the Active Projects view on even when the workspace setting has it off.
        // Leaving it selected after hosting ends would filter the project tabs to the live sessions
        // with no tab on screen explaining why, so it hands back to a real group.
        if (!hosting && _activeGroupId == ActiveProjectsGroupId
            && (!_showActiveProjectsGroup || _groups.Count < 2))
        {
            // SetActiveGroup rather than assigning the field: it also moves off a project that the
            // fallback group doesn't contain, which is the whole point of not leaving the sentinel
            // selected. With no groups at all there is nothing to fall back to, and every tab shows.
            if (GroupBehindActiveProjectsTab() is { } fallback)
            {
                SetActiveGroup(fallback);
            }
            else
            {
                _activeGroupId = null;
                RefreshProjectTabVisibility();
            }
        }

        // The pill lives on the group strip, which is normally hidden below two groups. Hosting
        // forces it up (see UpdateGroupStripVisibility) so the pill has somewhere to sit and the
        // Active tab is on screen.
        RefreshMetaTabBar();
    }

    /// <summary>
    /// A remote window that looks identical to a local one is a way to run the wrong command on
    /// the wrong machine, so the title says which machine is being driven.
    /// </summary>
    private void UpdateWindowTitleForRemote()
    {
        if (_remoteClient.State == RemoteClientState.Disconnected) return;

        Title = $"Agent Dock — remote: {_remoteClient.ServerName ?? _remoteClient.Endpoint}";
    }

    /// <summary>Wires the remote services to the window. Called once from the constructor.</summary>
    private void InitializeRemote()
    {
        _remoteServer.StateChanged += RefreshRemoteUi;
        _remoteServer.AuthFailureReported += OnServerAuthFailure;

        _remoteClient.FrameReceived += OnRemoteFrame;
        _remoteClient.StateChanged += OnRemoteClientStateChanged;
        _remoteClient.Disconnected += OnRemoteDisconnected;

        RefreshRemoteUi();
    }
}
