using System.Windows;
using AgentDock.Models;
using AgentDock.Services.Remote;
using AgentDock.Windows;

namespace AgentDock;

/// <summary>
/// The window's current <see cref="AppMode"/>, and the one menu that changes it.
///
/// Modes are mutually exclusive, and before this file that exclusion was implicit — expressed as
/// pairwise guards scattered across the remote code ("refuse to host while connected", "refuse to
/// connect while hosting"), each one restating the rule in its own words. Naming the states turns
/// that into a single question asked in a single place, which is what makes adding Autopilot a new
/// switch arm rather than a new round of guards.
///
/// The awkward member is <see cref="AppMode.Remote"/>: it is the only mode that ends without the
/// user asking it to, when the link drops or the host takes back control. So <see cref="CurrentMode"/>
/// is computed from the live services rather than stored — a cached copy would be wrong from the
/// moment the network decided otherwise, and it would be wrong silently.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The mode in force right now, derived rather than stored. Ordered by precedence: hosting and
    /// being a remote head are both hard states that dictate what the window can do, so either one
    /// outranks a schedule that may not even be inside its window yet.
    /// </summary>
    public AppMode CurrentMode
    {
        get
        {
            if (_remoteServer.IsRunning) return AppMode.Server;
            if (_remoteClient.State != RemoteClientState.Disconnected || IsRemoteSession) return AppMode.Remote;
            return _autopilotArmed ? AppMode.Autopilot : AppMode.Standard;
        }
    }

    /// <summary>
    /// Leaves whichever mode is active and returns to Standard. Used by the Standard menu item and
    /// as the first step of switching between the other three, so there is exactly one description
    /// of how each mode is torn down.
    /// </summary>
    private void ReturnToStandard()
    {
        if (_remoteServer.IsRunning)
        {
            _remoteServer.Stop();
            RefreshRemoteUi();
        }
        else if (_remoteClient.State != RemoteClientState.Disconnected || IsRemoteSession)
        {
            DisconnectRemote_Click(this, new RoutedEventArgs());
        }

        if (_autopilotArmed)
            DisarmAutopilot();

        RefreshModeUi();
    }

    private void SetModeStandard_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentMode == AppMode.Standard)
        {
            RefreshModeUi();
            return;
        }

        ReturnToStandard();
    }

    /// <summary>
    /// Entering a non-Standard mode always leaves the current one first. Selecting a mode is a
    /// statement about where the user wants to end up, so making them exit the old one by hand
    /// before the menu will listen would be asking them to do the obvious step themselves.
    ///
    /// Returns false when the user cancelled out of leaving — only possible for Remote, whose
    /// disconnect can prompt.
    /// </summary>
    private bool LeaveCurrentModeFor(AppMode target)
    {
        if (CurrentMode == AppMode.Standard) return true;
        if (CurrentMode == target) return true;

        ReturnToStandard();
        return CurrentMode == AppMode.Standard;
    }

    private void SetModeServer_Click(object sender, RoutedEventArgs e)
    {
        // Already hosting: re-selecting the mode is a no-op rather than a second Start, which the
        // server would reject. Only Autopilot reopens its dialog on re-selection, because its
        // settings are the kind you come back to adjust — a running server's port is not.
        if (CurrentMode == AppMode.Server) { RefreshModeUi(); return; }

        SwitchMode(AppMode.Server, () => StartServer_Click(sender, e));
    }

    private void SetModeRemote_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentMode == AppMode.Remote) { RefreshModeUi(); return; }

        SwitchMode(AppMode.Remote, () => ConnectRemote_Click(sender, e));
    }

    /// <summary>
    /// Leaves the current mode, runs <paramref name="enter"/>, and puts the old mode back if
    /// entering the new one did not take.
    ///
    /// The restore is the point. Server and Remote both begin by showing a dialog, and the user
    /// cancelling that dialog is an ordinary outcome, not an error — but leaving the old mode is
    /// the step that has to happen first, and for Autopilot it is destructive and persistent.
    /// Without this, opening Set Mode ▸ Server Mode and pressing Cancel would silently delete a
    /// nightly schedule, and it would still be deleted after a restart.
    /// </summary>
    private void SwitchMode(AppMode target, Action enter)
    {
        var previous = CurrentMode;
        var previousAutopilot = previous == AppMode.Autopilot ? _autopilotSettings : null;

        if (!LeaveCurrentModeFor(target))
        {
            RefreshModeUi();
            return;
        }

        enter();

        if (CurrentMode == AppMode.Standard && previousAutopilot != null)
        {
            ArmAutopilot(previousAutopilot);
            _log.Info($"Autopilot: kept armed — entering {target} mode was cancelled");
        }

        RefreshModeUi();
    }

    /// <summary>
    /// Opens the Autopilot dialog and arms the result. Unlike the other two, this is also the way
    /// to *reconfigure* an armed autopilot: selecting it again reopens the dialog with the current
    /// window and selection filled in, so changing tonight's finish time does not mean turning the
    /// whole mode off and setting it up again.
    /// </summary>
    private void SetModeAutopilot_Click(object sender, RoutedEventArgs e)
    {
        if (!LeaveCurrentModeFor(AppMode.Autopilot))
        {
            RefreshModeUi();
            return;
        }

        var candidates = _projects
            .Select(p => new AutopilotDialog.ProjectChoice(
                p.FolderPath,
                p.DisplayName,
                IsProjectActive(p)))
            .ToList();

        if (candidates.Count == 0)
        {
            ThemedMessageBox.Show(this,
                "Autopilot manages open projects, and none are open yet. Add a project folder and " +
                "start its session first.",
                "Autopilot");
            RefreshModeUi();
            return;
        }

        var result = AutopilotDialog.Show(this, LoadAutopilotSettings(), candidates);
        if (result == null)
        {
            RefreshModeUi();
            return;
        }

        SaveAutopilotSettings(result);
        ArmAutopilot(result);
        RefreshModeUi();
    }

    /// <summary>
    /// Ticks the mode menu and repaints the badge. Called after anything that could have changed
    /// the mode — including the things that change it without being asked, which is why the remote
    /// service's own state events route here too.
    /// </summary>
    private void RefreshModeUi()
    {
        var mode = CurrentMode;

        if (ModeStandardItem != null) ModeStandardItem.IsChecked = mode == AppMode.Standard;
        if (ModeServerItem != null) ModeServerItem.IsChecked = mode == AppMode.Server;
        if (ModeAutopilotItem != null) ModeAutopilotItem.IsChecked = mode == AppMode.Autopilot;
        if (ModeRemoteItem != null) ModeRemoteItem.IsChecked = mode == AppMode.Remote;

        RefreshModeBadge(mode);
        RefreshAllAutopilotToolbars();
    }

    /// <summary>
    /// The title-bar badge, which is always on screen now rather than appearing only for the remote
    /// modes.
    ///
    /// Standard used to show nothing, on the reasoning that "normal" needs no label. That reasoning
    /// fails the moment a mode can be armed and then sit quietly: a window in Autopilot at 4pm,
    /// waiting for an 11pm start, looks exactly like a window that is not. Showing the mode
    /// unconditionally means the badge answers the question instead of only ever raising it.
    /// </summary>
    private void RefreshModeBadge(AppMode mode)
    {
        if (ModeBadge == null) return;

        // The two remote modes keep their existing badge text and tooltips, which carry the
        // endpoint and the client name — RefreshRemoteBadges has already written them.
        if (mode is AppMode.Server or AppMode.Remote) return;

        ModeBadge.Visibility = Visibility.Visible;

        if (mode == AppMode.Autopilot)
        {
            var running = IsAutopilotWindowOpen();
            ModeBadgeText.Text = running
                ? "AUTOPILOT · ACTIVE"
                : $"AUTOPILOT · {_autopilotSettings.DescribeWindow()}";
            ModeBadge.ToolTip = DescribeAutopilotState();
            return;
        }

        // The only mode with nothing to leave, so here the badge is a label rather than a button
        // and the tooltip points at the menu instead of promising an action the click won't take.
        ModeBadgeText.Text = "STANDARD";
        ModeBadge.ToolTip = "Ordinary local use — nothing scheduled, nothing published. " +
                            "Settings ▸ Set Mode to change.";
    }
}
