using System.Windows;
using System.Windows.Threading;
using AgentDock.Controls;
using AgentDock.Models;
using AgentDock.Services;

namespace AgentDock;

/// <summary>
/// Autopilot: inside a nominated time window, nudge the selected sessions to carry on by
/// themselves.
///
/// The problem it exists for is narrow and specific. A long task finishes a phase, the agent says
/// it is ready for the next one, and then it waits — sometimes for hours, sometimes because a
/// subagent came back and the main turn never noticed. None of that needs a human; it just needs
/// someone to say "go on then". Overnight there is nobody to say it.
///
/// What is dangerous here is not the sending, it is the sending at the wrong moment. So the rules
/// about <i>when</i> live in <see cref="AutopilotPolicy"/> and <see cref="AutopilotTracker"/>,
/// where they can be tested without a window; this file is the plumbing that feeds them
/// observations and carries out the decision.
/// </summary>
public partial class MainWindow
{
    private const string AutopilotSettingsKey = "AutopilotSettings";

    private bool _autopilotArmed;
    private AutopilotSettings _autopilotSettings = new();
    private DispatcherTimer? _autopilotTimer;

    /// <summary>Per-project bookkeeping, keyed by folder path so it survives a project being closed
    /// and reopened — the same project to the user, and its "nothing left to do" verdict with it.</summary>
    private readonly Dictionary<string, AutopilotTracker> _autopilotTrackers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the schedule window was open at the previous tick, so opening and closing can be
    /// detected as transitions. Trackers are reset when a window opens: a session parked last night
    /// deserves a fresh hearing tonight, because the repository has moved on since it concluded
    /// there was nothing to do.
    /// </summary>
    private bool _autopilotWindowWasOpen;

    // --- arming ---

    private AutopilotSettings LoadAutopilotSettings() =>
        AutopilotSettings.FromJson(_appSettings.GetString(AutopilotSettingsKey));

    private void SaveAutopilotSettings(AutopilotSettings settings)
    {
        _appSettings.SetString(AutopilotSettingsKey, settings.ToJson());
        _autopilotSettings = settings;
    }

    /// <summary>
    /// Arms the schedule. Note this does not start nudging — if the window is hours away the
    /// window behaves exactly like Standard until it opens, which is the distinction between the
    /// two modes.
    /// </summary>
    private void ArmAutopilot(AutopilotSettings settings)
    {
        _autopilotSettings = settings;
        _autopilotArmed = true;

        if (!settings.Armed)
        {
            settings.Armed = true;
            _appSettings.SetString(AutopilotSettingsKey, settings.ToJson());
        }
        _autopilotWindowWasOpen = false;
        _autopilotTrackers.Clear();

        _autopilotTimer ??= CreateAutopilotTimer();
        _autopilotTimer.Start();

        _log.Info($"Autopilot: armed for {settings.DescribeWindow()} " +
                  $"({settings.ProjectPaths.Count} project(s), repeatDaily={settings.RepeatDaily})");

        // Evaluate immediately rather than waiting a tick, so arming inside the window starts the
        // dwell clock now instead of up to ten seconds late.
        AutopilotTick();
    }

    private void DisarmAutopilot()
    {
        if (!_autopilotArmed) return;

        _autopilotArmed = false;
        _autopilotTimer?.Stop();
        _autopilotTrackers.Clear();
        _autopilotWindowWasOpen = false;

        // Persisted, so that turning autopilot off stays off across a restart. The rest of the
        // configuration is kept — the next time the dialog opens it should come back with the
        // window and session selection already filled in, not blank.
        if (_autopilotSettings.Armed)
        {
            _autopilotSettings.Armed = false;
            _appSettings.SetString(AutopilotSettingsKey, _autopilotSettings.ToJson());
        }

        _log.Info("Autopilot: disarmed");
    }

    private DispatcherTimer CreateAutopilotTimer()
    {
        // Ten seconds. The dwell and the nudge interval are measured in minutes, so the tick only
        // has to be fine enough not to add a visible lag to them — polling faster would just wake
        // the UI thread more often to reach the same conclusion.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => AutopilotTick();
        Closed += (_, _) => timer.Stop();
        return timer;
    }

    /// <summary>
    /// Restores a daily schedule that was left armed.
    ///
    /// Only a repeating one comes back. A one-shot window was a decision about a particular night,
    /// and reinstating it on some later launch would be arming a mode that writes into live
    /// sessions on the strength of a choice the user may not remember making. A repeating schedule
    /// is the opposite — the user asked for it to keep happening, and forgetting it on every
    /// restart is the behaviour that would surprise them.
    /// </summary>
    private void InitializeAutopilot()
    {
        _autopilotSettings = LoadAutopilotSettings();

        if (!_autopilotSettings.Armed || !_autopilotSettings.RepeatDaily) return;

        // Deferred to Loaded: arming evaluates immediately, and that walks the project list and the
        // usage cache, neither of which is populated during the constructor.
        Loaded += (_, _) =>
        {
            if (CurrentMode != AppMode.Standard) return;
            ArmAutopilot(_autopilotSettings);
            RefreshModeUi();
        };
    }

    // --- the loop ---

    /// <summary>True when the clock is currently inside the armed window.</summary>
    private bool IsAutopilotWindowOpen() =>
        _autopilotArmed
        && AutopilotPolicy.IsWithinWindow(
            _autopilotSettings.StartMinutes,
            _autopilotSettings.EndMinutes,
            TimeOnly.FromDateTime(DateTime.Now));

    private void AutopilotTick()
    {
        if (!_autopilotArmed) return;

        // Mode precedence, re-checked every tick rather than assumed: hosting or connecting can
        // begin without going through the mode menu, and autopilot writing into sessions that are
        // being driven from another machine is the one combination that must never happen.
        if (CurrentMode != AppMode.Autopilot) return;

        var open = IsAutopilotWindowOpen();

        if (open != _autopilotWindowWasOpen)
        {
            OnAutopilotWindowTransition(open);
            _autopilotWindowWasOpen = open;

            // Repaint on both edges of the transition. A repeating schedule stays armed when its
            // window closes, so without this the badge would sit on "ACTIVE" until something else
            // happened to refresh it — claiming autopilot is managing sessions at nine in the
            // morning, hours after it stopped.
            if (_autopilotArmed) RefreshModeBadge(AppMode.Autopilot);
        }

        if (!open) return;

        var managed = ManagedProjects().ToList();
        if (managed.Count == 0) return;

        EnsureFreshUsage();

        foreach (var project in managed)
        {
            if (!_projectChatControls.TryGetValue(project, out var chat)) continue;

            var tracker = TrackerFor(project.FolderPath);
            var budget = BudgetForProject(project);

            if (!tracker.Evaluate(SnapshotOf(chat), DateTime.UtcNow, budget)) continue;

            SendNudge(project, chat, budget);
        }

        RefreshModeBadge(AppMode.Autopilot);
    }

    private void OnAutopilotWindowTransition(bool opened)
    {
        if (opened)
        {
            // A park is a verdict about a moment, not about a session. Overnight the repository
            // gains commits, merges and new failures, so last night's "nothing left to do" says
            // nothing about tonight and every tracker starts clean.
            foreach (var tracker in _autopilotTrackers.Values)
                tracker.ResetForNewWindow();

            _log.Info($"Autopilot: window opened ({_autopilotSettings.DescribeWindow()})");
            return;
        }

        var nudges = _autopilotTrackers.Values.Sum(t => t.NudgeCount);
        _log.Info($"Autopilot: window closed after {nudges} nudge(s)");

        // A one-shot schedule has now done its job. Leaving it armed would mean the same window
        // silently reopening tomorrow, which is precisely what the user declined when they left
        // the daily toggle off.
        if (!_autopilotSettings.RepeatDaily)
        {
            DisarmAutopilot();
            RefreshModeUi();
        }
    }

    /// <summary>
    /// The projects autopilot manages: those the user picked that are still open and have a live
    /// session. A selected project whose session has exited is skipped rather than restarted —
    /// autopilot continues work, it does not start it.
    /// </summary>
    private IEnumerable<ProjectInfo> ManagedProjects() =>
        _projects.Where(p =>
            _autopilotSettings.ProjectPaths.Contains(p.FolderPath, StringComparer.OrdinalIgnoreCase)
            && IsProjectActive(p));

    private DateTime? _autopilotUsageRequestedAt;

    /// <summary>
    /// Keeps a usable usage reading in hand while autopilot is running.
    ///
    /// Without this the feature deadlocks itself overnight, and quietly. The usage indicator only
    /// re-fetches when a session has actually spent tokens (<c>_usageDirty</c>) — a sensible rule
    /// for a display, because polling a rate-limited endpoint to redraw an unchanged number is
    /// waste. But autopilot's whole situation is a machine where nothing is spending anything: the
    /// sessions are idle, so nothing marks usage dirty, so the reading ages past
    /// <see cref="AutopilotPolicy.MaxUsageAge"/>, so every nudge is held for want of a number, so
    /// nothing ever spends anything. The one thing that would have broken the cycle — a session
    /// doing work — is the thing being blocked.
    ///
    /// So the refresh is demand-driven from here instead: asked for only when a managed session is
    /// actually waiting on it, at most once a minute, and refreshed at half the staleness limit so
    /// the answer is normally in hand before it is needed rather than a tick late.
    /// </summary>
    private void EnsureFreshUsage()
    {
        // Measured against the newest figure actually obtained, not the last attempt: a run of
        // failed fetches must keep asking, and asking is the only thing that can end the run. At
        // half the staleness limit so the answer is normally in hand before a session needs it.
        var newest = _usageAccounts
            .Select(a => a.LastSuccessAt)
            .Where(t => t != null)
            .DefaultIfEmpty(null)
            .Max();

        if (newest is { } obtained && DateTime.Now - obtained <= AutopilotPolicy.MaxUsageAge / 2)
            return;

        // The endpoint rate-limits by source, so the request rate is capped here regardless of how
        // many sessions are waiting on it. RefreshUsageAsync honours any Retry-After on top.
        if (_autopilotUsageRequestedAt is { } asked
            && DateTime.Now - asked < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _autopilotUsageRequestedAt = DateTime.Now;
        _ = RefreshUsageAsync();
    }

    private AutopilotTracker TrackerFor(string projectPath)
    {
        if (_autopilotTrackers.TryGetValue(projectPath, out var existing)) return existing;

        var tracker = new AutopilotTracker();
        _autopilotTrackers[projectPath] = tracker;
        return tracker;
    }

    /// <summary>
    /// Folds a chat panel down to the three facts the tracker reasons about.
    ///
    /// The <c>IsAtRest</c> conjunction is the safety-critical line in this feature. Every clause
    /// rules out a different way of nudging at the wrong moment: a turn still running, subagents
    /// still draining while the main turn reports Idle, a permission prompt or a question waiting
    /// on a human decision autopilot may not make, and a queue that already has the user's own
    /// instructions in it.
    /// </summary>
    private static AutopilotSnapshot SnapshotOf(AiChatControl chat) =>
        new(
            IsAtRest:
                chat.CurrentState == ClaudeSessionState.Idle
                && !chat.HasBackgroundWork
                && !chat.HasPendingPrompt
                && chat.IsSendQueueEmpty,
            ToolUseCount: chat.ToolUseCount,
            HumanMessageCount: chat.HumanMessageCount);

    /// <summary>
    /// The budget for one project, read from the usage figures already cached for its account.
    ///
    /// Deliberately does not fetch: the usage endpoint rate-limits by source, and a tick that
    /// polled it per project per ten seconds would trip a 429 and end up with *less* information
    /// than it started with. The cached reading is refreshed by the ordinary 90-second indicator,
    /// and anything older than <see cref="AutopilotPolicy.MaxUsageAge"/> is treated as no reading
    /// at all.
    /// </summary>
    private AutopilotBudget BudgetForProject(ProjectInfo project) =>
        AutopilotPolicy.ClassifyBudget(UsageForProject(project));

    /// <summary>
    /// The usage reading that governs this project, or null when there isn't a usable one.
    ///
    /// Null covers three situations that have to behave identically, because autopilot cannot tell
    /// them apart and none of them is a reason to proceed: no login matched, no reading ever
    /// obtained for it, and a reading too old to trust. All three mean the same thing — autopilot
    /// does not know how much room it has — and all three hold the nudge.
    ///
    /// Age is measured from when the figure was last *obtained*, not from when one was last asked
    /// for. Those diverge exactly when it matters: a lapsed token or a sustained 429 keeps the
    /// attempt timestamp fresh forever while the number behind it goes stale, which is precisely
    /// the case <see cref="AutopilotPolicy.MaxUsageAge"/> exists to catch.
    /// </summary>
    private UsageSummary? UsageForProject(ProjectInfo project)
    {
        var accountId = _projectSettings.Load(project.FolderPath).ClaudeAccountId;

        // Exact match on the login the session actually runs as, including the machine default
        // (both ids null). Quota is per-login, so falling back to some *other* account's figure
        // would authorise a nudge against budget that belongs to somebody else — worse than having
        // no figure at all. The single-account fallback is allowed only where there is no ambiguity
        // about which login is meant.
        var account =
            _usageAccounts.FirstOrDefault(a => a.Id == accountId)
            ?? (accountId == null && _usageAccounts.Count == 1 ? _usageAccounts[0] : null);

        if (account?.LastSuccess is not { } summary || account.LastSuccessAt is not { } obtained)
        {
            LogAutopilotBudgetBlock(project, accountId, "no usage reading for this account");
            return null;
        }

        if (DateTime.Now - obtained > AutopilotPolicy.MaxUsageAge)
        {
            LogAutopilotBudgetBlock(project, accountId,
                $"usage reading is {(int)(DateTime.Now - obtained).TotalMinutes} minutes old");
            return null;
        }

        // Recovered: allow the next problem on this project to be reported afresh.
        _autopilotBudgetWarned.Remove(project.FolderPath);
        return summary;
    }

    /// <summary>Folder paths already warned about, so the ten-second tick reports a stuck project
    /// once rather than six times a minute all night.</summary>
    private readonly HashSet<string> _autopilotBudgetWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Says why a project is being held back on budget. Silence here would be the worst failure
    /// this feature has: a machine that looks armed and running, and simply never does anything,
    /// with nothing anywhere saying why.
    /// </summary>
    private void LogAutopilotBudgetBlock(ProjectInfo project, string? accountId, string reason)
    {
        if (!_autopilotBudgetWarned.Add(project.FolderPath)) return;

        _log.Warn($"Autopilot: holding '{project.DisplayName}' (account={accountId ?? "default"}) — {reason}");
    }

    private void SendNudge(ProjectInfo project, AiChatControl chat, AutopilotBudget budget)
    {
        var text = AutopilotPolicy.ComposeNudge(budget, _autopilotSettings.PromptOverride);

        // ComposeNudge returns null only for Exhausted, which Evaluate has already refused to nudge
        // on. Belt and braces: a silent no-op is the right failure here, not an empty send.
        if (text == null) return;

        var remaining = AutopilotPolicy.RemainingPercent(UsageForProject(project));

        var budgetWord = budget switch
        {
            AutopilotBudget.Large => "large task",
            AutopilotBudget.Moderate => "moderate task",
            _ => "small task",
        };

        var notice = remaining is { } pct
            ? $"⟳ Autopilot — continuing unattended ({pct:0}% quota left, {budgetWord})"
            : $"⟳ Autopilot — continuing unattended ({budgetWord})";

        chat.SendAutopilotNudge(text, notice);

        _log.Info($"Autopilot: nudged '{project.DisplayName}' (budget={budget})");
    }

    // --- UI wiring ---

    /// <summary>
    /// Hooks a chat panel as it is built. Typing in the composer pushes the pending nudge back to
    /// the start of its dwell, so a user who wandered back to the machine at 1am gets to finish
    /// their sentence rather than racing the timer.
    /// </summary>
    private void WireAutopilot(ProjectInfo project, AiChatControl chat)
    {
        chat.ComposerActivity += () =>
        {
            if (!_autopilotArmed) return;
            if (!_autopilotTrackers.TryGetValue(project.FolderPath, out var tracker)) return;
            tracker.CancelPendingNudge();
        };
    }

    /// <summary>Badge tooltip: what autopilot is doing, and what it is waiting for.</summary>
    private string DescribeAutopilotState()
    {
        if (!IsAutopilotWindowOpen())
        {
            var repeat = _autopilotSettings.RepeatDaily ? " every day" : "";
            return $"Autopilot is armed for {_autopilotSettings.DescribeWindow()}{repeat}. " +
                   "Until then this window behaves normally. Click to return to Standard mode.";
        }

        var managed = ManagedProjects().ToList();
        if (managed.Count == 0)
            return "Autopilot is running, but none of the selected projects have a live session. " +
                   "Click to return to Standard mode.";

        var lines = managed.Select(p =>
        {
            var tracker = TrackerFor(p.FolderPath);
            var state = tracker.IsParked
                ? "parked — said it had nothing left"
                : tracker.LastHold switch
                {
                    AutopilotHold.Busy => "working",
                    AutopilotHold.Settling => "settling",
                    AutopilotHold.TooSoon => "just nudged",
                    AutopilotHold.Budget => "waiting on quota",
                    _ => "ready",
                };
            return $"    {p.DisplayName} — {state} ({tracker.NudgeCount} nudge(s))";
        });

        return $"Autopilot is running ({_autopilotSettings.DescribeWindow()}).\n" +
               string.Join('\n', lines) +
               "\n\nClick to return to Standard mode.";
    }

    /// <summary>
    /// The badge's click action, which depends on what the badge is showing. Server and Remote keep
    /// their long-standing one-click exit; Autopilot disarms the same way. Standard has nothing to
    /// leave, so the badge is a label there and says where the menu is instead.
    /// </summary>
    private void ModeBadge_Click(object sender, RoutedEventArgs e)
    {
        switch (CurrentMode)
        {
            case AppMode.Server:
            case AppMode.Remote:
                ExitRemoteMode_Click(sender, e);
                break;

            case AppMode.Autopilot:
                DisarmAutopilot();
                RefreshModeUi();
                break;
        }
    }
}
