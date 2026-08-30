using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentDock.Models;
using AgentDock.Services;
using AgentDock.Windows;
using MdXaml;

using AgentDock.Services.Abstractions;

namespace AgentDock.Controls;

public partial class AiChatControl : UserControl
{
    private ClaudeSession? _session;
    // Runs the turn state machine on the session's background read-loop thread and
    // emits classified ops we merge into Messages on the UI thread. See ApplyOps.
    private ChatTurnProcessor? _processor;
    private string _projectPath = "";

    // The virtualized chat list. Past messages are immutable VM classes;
    // only the live tail (streaming text, building execution, inactivity
    // warning) carries INPC. Past-message containers in the
    // VirtualizingStackPanel are realized only when scrolled into view.
    public ObservableCollection<ChatMessageVm> Messages { get; } = [];

    // Images queued in the input area, shown as chips and attached to the next
    // sent message. Bound by AttachmentsBar (RelativeSource to this UserControl).
    public ObservableCollection<PendingImageAttachment> PendingAttachments { get; } = [];

    // Live-tail references — set when the corresponding VM is in the
    // collection, nulled on finalize / removal.
    //
    // The one unified activity bubble for the current turn (gray thinking +
    // green execution interleaved). Created on the first thinking/tool/commentary
    // op, finalized (collapsed, header → duration summary) at turn end.
    private ActivityMessage? _activityVm;
    private WaitingMessage? _waitingVm;
    private InactivityWarning? _inactivityVm;
    private DispatcherTimer? _inactivityElapsedTimer;

    // Animated "still working" header for the active activity bubble: a whimsical
    // verb with cycling dots (e.g. "Pondering…"), refreshed by a timer while the
    // turn runs. One timer per control (so each tab animates independently).
    private DispatcherTimer? _activityHeaderTimer;
    private int _activityTick;
    private string _activityVerb = "";
    private static readonly Random s_rng = new();
    private static readonly string[] ActivityVerbs =
    [
        "Thinking", "Pondering", "Conjuring", "Finagling", "Noodling", "Cogitating",
        "Ruminating", "Tinkering", "Wrangling", "Percolating", "Synthesizing",
        "Deliberating", "Scheming", "Computing", "Mulling", "Brewing", "Whirring",
        "Crunching", "Untangling", "Spelunking", "Marinating", "Puzzling",
    ];

    // Animated working indicator in the status bar (not a message).
    private DispatcherTimer? _workingTimer;
    private int _spinnerIndex;
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // In-flight background work the session reports as running, split by kind. Shown as a
    // suffix on the "Working..." status so the user can tell what kind of work is still in
    // flight (e.g. a subagent churning) even after the main agent's text has returned.
    private int _activeSubagents;
    private int _activeBackgroundTasks;
    private int _activeWorkflows;

    // Pending AskUserQuestion — tracks question text for response.
    private string? _pendingQuestionText;

    // --- Send queue (the outbox) ---
    // A message typed while the session is busy is appended here instead of being
    // dropped, then dispatched in strict FIFO order as each turn returns. A message
    // "scheduled" via the clock is the same thing with a NotBeforeUtc time gate. The
    // pump (PumpQueue) runs whenever the session goes idle and on a per-second timer
    // (only while the queue is non-empty) that also drives the live countdown labels.
    // In-memory only — the queue is cleared on Stop and not persisted across restarts.
    private readonly SendQueue _queue = new();
    private DispatcherTimer? _queueTimer;

    /// <summary>The queued messages, exposed for the queue panel's ItemsControl binding.</summary>
    public ObservableCollection<QueuedMessage> QueuedMessages => _queue.Items;

    /// <summary>
    /// UTC time the next scheduled (time-gated) message will fire, or null when nothing
    /// is scheduled. Surfaced so the tab strip can show a scheduled-message indicator +
    /// tooltip. Plain queued messages drain the moment the session is idle, so a
    /// scheduled item is the only kind that lingers at idle.
    /// </summary>
    public DateTime? ScheduledFireTimeUtc => _queue.NextScheduledFireUtc;

    /// <summary>Raised when the send queue changes (message queued/scheduled, sent, or cancelled).</summary>
    public event Action? ScheduleChanged;

    // Last user message timestamp, for the inactivity warning's elapsed text.
    private DateTime _lastMessageSentTime;

    // Inner ScrollViewer of the ItemsControl, cached after template is applied.
    private ScrollViewer? _scrollViewer;

    // Sticky-bottom auto-scroll state. True while the chat should follow new
    // content to the bottom; flipped to false when the user scrolls up to read
    // history, and back to true when they return to the bottom (or send). It is
    // maintained entirely from ScrollChanged event args (see OnScrollChanged) so
    // the streaming hot path never reads ScrollableHeight/VerticalOffset, which
    // would force a synchronous layout pass on every delta.
    private bool _stickToBottom = true;
    private const double StickyBottomThreshold = 16;

    /// <summary>
    /// Raised when session state changes (for toolbar icon updates).
    /// </summary>
    public event Action<ClaudeSessionState>? SessionStateChanged;

    /// <summary>
    /// Raised when the session's background-work status changes (subagents / background
    /// tasks starting or draining) independent of the main turn state, so the tab icon
    /// can pulse while the session is Idle-but-still-busy.
    /// </summary>
    public event Action? SessionBackgroundWorkChanged;

    /// <summary>
    /// Raised when cumulative session stats change (cost, tokens).
    /// </summary>
    public event Action<SessionStats>? SessionStatsChanged;

    /// <summary>
    /// Raised whenever <see cref="StatusLabel"/> changes, including on every frame of
    /// the working spinner. The chat pane has no status strip of its own — the host
    /// renders this in the dock panel's title bar next to cost and tokens.
    /// </summary>
    public event Action? SessionStatusChanged;

    /// <summary>
    /// Human-readable session status ("Idle", "⠹ Working...", "⚠ Error"), or empty
    /// when no session is running.
    /// </summary>
    public string StatusLabel { get; private set; } = "";

    // Sets StatusLabel and notifies the host, skipping no-op writes so an unchanged
    // status doesn't churn the title bar.
    private void SetStatus(string status)
    {
        if (StatusLabel == status) return;
        StatusLabel = status;
        SessionStatusChanged?.Invoke();
    }

    /// <summary>
    /// Raised when the session init arrives and the model is known.
    /// </summary>
    public event Action<string>? SessionModelChanged;

    /// <summary>
    /// Raised when the user clicks a file-path reference rendered inside an
    /// assistant markdown bubble. Payload is the absolute path.
    /// </summary>
    public event Action<string>? FileReferenceClicked;

    /// <summary>
    /// Cumulative stats for the current session.
    /// </summary>
    public SessionStats Stats { get; } = new();

    /// <summary>
    /// Current model reported by the Claude Code session (e.g. "claude-sonnet-4-5"), or null if unknown.
    /// </summary>
    public string? Model => _session?.Model;

    /// <summary>Shortcut for backwards compat.</summary>
    public double SessionCostUsd => Stats.TotalCostUsd;

    /// <summary>
    /// Whether the current session is running in dangerous mode.
    /// </summary>
    public bool IsDangerousMode => _session?.IsDangerousMode ?? false;

    /// <summary>
    /// Current session state (for theme change icon refresh).
    /// </summary>
    public ClaudeSessionState CurrentState => _session?.State ?? ClaudeSessionState.NotStarted;

    /// <summary>
    /// True while the session has subagents / background tasks still running, even
    /// though the main turn may have returned to Idle.
    /// </summary>
    public bool HasBackgroundWork => _session?.HasBackgroundWork ?? false;

    private readonly ILogService _log;
    private readonly IPerfDiagnostics _perf;
    private readonly IThemeService _theme;
    private readonly IProjectSettingsStore _projectSettings;
    private readonly IAccountManager _accounts;
    private readonly IClaudeSessionFactory _sessionFactory;
    private readonly IClaudeEnvironment _claudeEnvironment;
    private readonly IMarkdownRenderer _markdown;
    private readonly IImageAttachmentService _images;

    public AiChatControl(
        ILogService log,
        IPerfDiagnostics perf,
        IThemeService theme,
        IProjectSettingsStore projectSettings,
        IAccountManager accounts,
        IClaudeSessionFactory sessionFactory,
        IClaudeEnvironment claudeEnvironment,
        IMarkdownRenderer markdown,
        IImageAttachmentService images)
    {
        _log = log;
        _perf = perf;
        _theme = theme;
        _projectSettings = projectSettings;
        _accounts = accounts;
        _sessionFactory = sessionFactory;
        _claudeEnvironment = claudeEnvironment;
        _markdown = markdown;
        _images = images;

        _log.Info("AiChatControl: constructor");
        InitializeComponent();
        MessageList.ItemsSource = Messages;
        Messages.CollectionChanged += OnMessagesChanged;
        PendingAttachments.CollectionChanged += (_, _) => UpdateAttachmentsBar();
        _queue.Changed += OnQueueChanged;
        // Intercept Ctrl+V so a pasted screenshot / image file becomes an
        // attachment instead of pasting its path (or nothing) as text.
        DataObject.AddPastingHandler(InputBox, InputBox_Pasting);
        _log.Info("AiChatControl: InitializeComponent complete");
    }

    public void Initialize(string projectPath)
    {
        _log.Info($"AiChatControl: Initialize for '{projectPath}'");
        _projectPath = projectPath;
        // Reopen on whichever login this project last started a session with.
        _preferredAccountId = _projectSettings.Load(projectPath).ClaudeAccountId;
        // Refresh the login picker whenever the Start panel appears (initial show
        // and after a session ends) and when its dropdown opens, so accounts added
        // via the Accounts dialog show up without restarting the app.
        StartPanel.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) PopulateAccountPicker();
        };
        AccountCombo.DropDownOpened += (_, _) => PopulateAccountPicker();
        PopulateAccountPicker();
    }

    public void FocusInput()
    {
        if (InputBox.IsEnabled)
            Dispatcher.BeginInvoke(() => InputBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // Clicks on the prompt border (outside the textbox itself, e.g. between the > and
    // the text) should land focus in the textbox so the chrome behaves like one widget.
    private void InputBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == InputBorder && InputBox.IsEnabled)
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
            e.Handled = true;
        }
    }

    public void Shutdown()
    {
        StopQueueTimer();
        _queue.Clear();
        _session?.Dispose();
        _session = null;
        _processor = null;
    }

    // --- Start Session ---

    private void StartNormal_Click(object sender, RoutedEventArgs e) => StartSession(false);
    private void StartDangerous_Click(object sender, RoutedEventArgs e) => StartSession(true);

    /// <summary>A selectable login on the Start panel — one per configured account.</summary>
    private sealed class AccountChoice
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        /// <summary>Short friendly label (the account name) shown in the chat title.</summary>
        public string Label { get; init; } = "";
    }

    /// <summary>
    /// The login this session was started with, for display in the chat panel title:
    /// the account name, or null when no accounts are configured (single-account users
    /// see no change). Fixed for the session's life.
    /// </summary>
    public string? AccountLabel { get; private set; }

    /// <summary>
    /// Id of the login this session is running as, or null for the machine default.
    /// Kept alongside <see cref="AccountLabel"/> so a failed turn can check whether that
    /// account is still signed in and say so. Fixed for the session's life.
    /// </summary>
    private string? _sessionAccountId;

    /// <summary>
    /// The account id remembered for this project (persisted in .agentdock/settings.json).
    /// Seeds the picker on load and is rewritten whenever the user picks a different login.
    /// </summary>
    private string? _preferredAccountId;

    /// <summary>Suppresses the persist-on-change handler while the picker is repopulated.</summary>
    private bool _populatingAccounts;

    /// <summary>
    /// Rebuilds the Start-panel login picker from the configured accounts, preserving
    /// the current selection by id. The picker stays hidden until at least one account
    /// exists (users who don't use multi-account never see it). There's no "Default"
    /// entry: once accounts are configured, a session always runs as one of them.
    /// </summary>
    private void PopulateAccountPicker()
    {
        var accounts = _accounts.Load();
        if (accounts.Count == 0)
        {
            AccountPickerPanel.Visibility = Visibility.Collapsed;
            AccountCombo.ItemsSource = null;
            return;
        }

        var previousId = (AccountCombo.SelectedItem as AccountChoice)?.Id ?? _preferredAccountId;

        var choices = new List<AccountChoice>();
        foreach (var a in accounts)
        {
            // Login state first, email as the label — a signed-out account still has an
            // email on disk, and showing only that hides the fact it can't run.
            var email = _accounts.ReadEmail(a.Id);
            var suffix = _accounts.IsLoggedIn(a.Id)
                ? email != null ? $" — {email}" : " — signed in"
                : email != null ? $" — {email} · signed out" : " — not signed in";
            choices.Add(new AccountChoice { Id = a.Id, Label = a.Name, DisplayName = a.Name + suffix });
        }

        var selected = choices.FirstOrDefault(c => c.Id == previousId) ?? choices[0];

        // Repopulating fires SelectionChanged; that's not a user choice, so don't persist it.
        _populatingAccounts = true;
        try
        {
            AccountCombo.ItemsSource = choices;
            AccountCombo.SelectedItem = selected;
        }
        finally
        {
            _populatingAccounts = false;
        }

        _preferredAccountId = selected.Id;
        AccountPickerPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Remembers the chosen login against the project so the next session — this run or
    /// after a restart — starts on the same account. Written straight to the project's
    /// settings file, so there's nothing for the user to save.
    /// </summary>
    private void AccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingAccounts || AccountCombo.SelectedItem is not AccountChoice choice)
            return;
        if (choice.Id == _preferredAccountId)
            return;

        _preferredAccountId = choice.Id;
        if (!string.IsNullOrEmpty(_projectPath))
        {
            _log.Info($"AiChatControl: remembering account id={choice.Id} for '{_projectPath}'");
            _projectSettings.Update(_projectPath, s => s.ClaudeAccountId = choice.Id);
        }
    }

    private void StartSession(bool dangerous)
    {
        _log.Info($"AiChatControl: StartSession(dangerous={dangerous})");
        if (!_claudeEnvironment.IsAvailable())
        {
            StartError.Text = "Claude CLI not found in PATH. Install Claude Code first.";
            StartError.Visibility = Visibility.Visible;
            return;
        }

        // Resolve the chosen login. No picker (no accounts configured) = the machine default.
        string? accountConfigDir = null;
        AccountLabel = null;
        _sessionAccountId = null;
        if (AccountPickerPanel.Visibility == Visibility.Visible &&
            AccountCombo.SelectedItem is AccountChoice choice)
        {
            if (!_accounts.IsLoggedIn(choice.Id))
            {
                StartError.Text = "That account isn't signed in yet. Open Claude Accounts to log in first.";
                StartError.Visibility = Visibility.Visible;
                return;
            }
            accountConfigDir = _accounts.ConfigDirFor(choice.Id);
            _log.Info($"AiChatControl: StartSession using account id={choice.Id}");
            AccountLabel = choice.Label;
            _sessionAccountId = choice.Id;
        }

        StartError.Visibility = Visibility.Collapsed;

        _session = _sessionFactory.Create(_projectPath, accountConfigDir);
        WireSessionEvents();

        StartPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        SetStatus("Initializing...");

        _session.Start(dangerous);
    }

    private void WireSessionEvents()
    {
        if (_session == null) return;

        // UI-only session reactions (status bar, panels, system messages) are
        // posted via Post(), which dispatches at DispatcherPriority.Background —
        // below DispatcherPriority.Input — so WPF processes prompt-box keystrokes
        // ahead of the session-event flood (the default Normal priority outranks
        // Input and was starving typing). All share the one priority, preserving
        // FIFO order.
        _session.StateChanged += state => Post(() => OnStateChanged(state));
        _session.Initialized += init => Post(() => OnInitialized(init));
        _session.PermissionRequested += req => Post(() => OnPermissionRequested(req));
        _session.ErrorOutput += text => Post(() => OnErrorOutput(text));
        _session.ProcessExited += code => Post(() => OnProcessExited(code));
        _session.InactivityTimeout += () => Post(OnInactivityTimeout);
        _session.BackgroundWorkChanged += () => Post(() => SessionBackgroundWorkChanged?.Invoke());

        // Transcript classification (thinking / commentary / execution / answer)
        // runs on the session's background read-loop thread via the processor — no
        // UI work there. It emits already-classified ops; we merge them into the
        // message list on the UI thread (ApplyOps). This is the "post-process off
        // the UI thread, then merge deltas into the observable" pattern: the only
        // thing on the UI thread is the collection mutation itself.
        _processor = new ChatTurnProcessor();
        _processor.Ops += ops => Post(() => ApplyOps(ops));
        _processor.Attach(_session);
    }

    // Applies a batch of classified ops to the message list on the UI thread.
    // Dropped if the session has gone away (e.g. Stop ran while ops were queued),
    // so late ops can't resurrect content into a cleared transcript.
    private void ApplyOps(IReadOnlyList<ChatOp> ops)
    {
        if (_session == null) return;
        foreach (var op in ops)
            ApplyOp(op);

        // Replicate after applying locally, in the same order, so a connected client
        // converges on exactly the state this panel is in.
        OpsProduced?.Invoke(ops);
    }

    private void ApplyOp(ChatOp op)
    {
        switch (op)
        {
            case RemoveInactivityOp: RemoveInactivityWarning(); break;
            case AppendThinkingOp a: ApplyAppendThinking(a.Text); break;
            case CommentaryOp c: ApplyCommentary(c.Text); break;
            // Thinking and execution now share one bubble: "finalize thinking" just
            // closes the current gray block so a following tool / fresh thinking
            // starts a new block. The bubble itself is finalized at TurnComplete.
            case FinalizeThinkingOp: _activityVm?.CloseThinking(); break;
            case EnsureExecutionOp: EnsureActivityVm(); _activityVm!.CloseThinking(); break;
            case AddToolOp t: EnsureActivityVm(); _activityVm!.AddTool(t.Name, t.FormattedInput); break;
            case AddSubagentOp s: EnsureActivityVm(); _activityVm!.AddSubagent(s.Label, s.Description); break;
            case AddSubagentReportOp r: EnsureActivityVm(); _activityVm!.AddSubagentReport(r.Label, r.Model, r.Text); break;
            case ActivityCountsOp ac:
                _activeSubagents = ac.Subagents;
                _activeBackgroundTasks = ac.BackgroundTasks;
                _activeWorkflows = ac.Workflows;
                RefreshWorkingStatus();
                break;
            case FinalizeExecutionOp: break; // no-op — see TurnComplete
            case PostAnswerOp p: ApplyPostAnswer(p.Text); break;
            case TurnCompleteOp tc: ApplyTurnComplete(tc.Result); break;
        }
    }

    // Ensures the activity bubble exists, then streams a thinking delta into its
    // open gray block. The processor has already applied block separators and the
    // redacted-thinking skip.
    private void ApplyAppendThinking(string text)
    {
        EnsureActivityVm();
        _activityVm!.AppendThinking(text);
    }

    // A buffered text block that turned out to be commentary — append it to the
    // activity bubble's open gray block (created if needed), coalesced with any
    // existing thinking content.
    private void ApplyCommentary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        EnsureActivityVm();
        _activityVm!.AddCommentary(text);
    }

    // The buffered text was the final answer — post it as a standalone, always-shown
    // assistant bubble (never inside the thinking group). The rendered FlowDocument
    // is built lazily on realize in a visible tab (see AttachAssistantDocument).
    private void ApplyPostAnswer(string text)
    {
        RemoveWaitingBubble();

        // Not just multi-line: a single-line reply can still carry inline markdown
        // (**bold**, *italic*, `code`, links, bare URLs) that must be rendered — and
        // that needs the source/rendered toggle — rather than shown with literal stars.
        var isMarkdown = MarkdownRenderer.LooksLikeMarkdown(text);
        Func<FlowDocument>? markdownBuilder = null;
        if (isMarkdown)
        {
            var style = (System.Windows.Style)FindResource("ChatMarkdownStyle");
            var projectPath = _projectPath;
            Action<string> onLink = p => FileReferenceClicked?.Invoke(p);
            FlowDocument? cached = null;
            markdownBuilder = () => cached ??= BuildAnswerDocument(text, style, projectPath, onLink);
        }

        Messages.Add(new AssistantMessage(
            id: Guid.NewGuid(),
            text: text,
            isMarkdownView: isMarkdown,
            hasMarkdownToggle: isMarkdown,
            markdownBuilder: markdownBuilder));
    }

    // Builds the rendered answer document, degrading to plain text if markdown rendering
    // throws. Without this, a render bug re-throws on every tab realize (the builder is
    // memoized with ??=, so a throw never caches and runs again next time). We log the
    // failure with the source text — that's the only copy that reproduces it — so the
    // underlying render bug stays visible and fixable rather than being silently swallowed.
    private FlowDocument BuildAnswerDocument(
        string text, System.Windows.Style style, string projectPath, Action<string> onLink)
    {
        try
        {
            return _markdown.BuildDocument(
                text, markdownStyle: style, projectPath: projectPath, onFileLinkClicked: onLink);
        }
        catch (Exception ex)
        {
            _log.Error($"Markdown render failed; showing plain text. Source markdown:\n{text}", ex);
            return _markdown.BuildPlainTextFallback(text, style);
        }
    }

    // Posts a session-driven UI update at Background priority so it yields to
    // prompt-box input. See the priority rationale in WireSessionEvents.
    private void Post(Action action)
        => Dispatcher.BeginInvoke(action, DispatcherPriority.Background);

    // --- State Handling ---

    private void OnStateChanged(ClaudeSessionState state)
    {
        StopWorkingAnimation();

        // Track how many sessions across all tabs are Working at once, so the
        // PerfDiagnostics stall/health log can correlate UI stalls with
        // background-tab session contention.
        var nowWorking = state == ClaudeSessionState.Working;
        if (nowWorking != _countedAsWorking)
        {
            _countedAsWorking = nowWorking;
            _perf.WorkingSessionDelta(nowWorking ? 1 : -1);
        }

        if (state == ClaudeSessionState.Working)
        {
            StartWorkingAnimation();
        }
        else
        {
            // The turn is over (or paused for a permission prompt). Clear the running
            // counts except while waiting on a permission, where the subagent that
            // triggered the prompt is still alive and resumes when granted.
            if (state != ClaudeSessionState.WaitingForPermission)
            {
                _activeSubagents = 0;
                _activeBackgroundTasks = 0;
                _activeWorkflows = 0;
            }

            SetStatus(DecorateStatus(state switch
            {
                ClaudeSessionState.Initializing => "Initializing...",
                ClaudeSessionState.Idle => "Idle",
                ClaudeSessionState.WaitingForPermission => "Waiting for permission...",
                ClaudeSessionState.Exited => "Session ended",
                ClaudeSessionState.Error => "Error",
                _ => ""
            }));
        }

        // Keep the input box usable while a turn is running so the user can draft
        // follow-ups; the Send button stays enabled too, but now enqueues rather
        // than dispatching when the session is busy (see SendCurrentMessage).
        UpdateSendButtonEnabled();

        if (state == ClaudeSessionState.Idle)
        {
            FocusInput();
            // The turn returned — dispatch the next queued/scheduled message if one is
            // ready. The per-second queue timer is a backstop; this makes it immediate.
            PumpQueue();
        }

        SessionStateChanged?.Invoke(state);
    }

    private void StartWorkingAnimation()
    {
        _spinnerIndex = 0;
        SetStatus(WorkingStatusText());
        _workingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _workingTimer.Tick += (_, _) =>
        {
            _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
            SetStatus(WorkingStatusText());
        };
        _workingTimer.Start();
    }

    // Prefixes a warning glyph while the session runs with permissions bypassed. This
    // used to be a separate icon in the (now removed) status strip.
    private string DecorateStatus(string status)
    {
        if (string.IsNullOrEmpty(status)) return status;
        var state = _session?.State ?? ClaudeSessionState.NotStarted;
        var showDanger = _session?.IsDangerousMode == true
            && state != ClaudeSessionState.NotStarted
            && state != ClaudeSessionState.Exited;
        return showDanger ? $"⚠ {status}" : status;
    }

    // "⠹ Working..." plus a "· N subagents · M background tasks running" suffix when the
    // session reports in-flight work, split by kind so real subagents aren't conflated with
    // background shells. Built from the current spinner frame and the live counts so both
    // the 80 ms animation tick and a count change render it.
    private string WorkingStatusText()
    {
        var text = DecorateStatus($"{SpinnerFrames[_spinnerIndex]} Working...");

        var parts = new List<string>(3);
        if (_activeSubagents > 0)
            parts.Add($"{_activeSubagents} subagent{(_activeSubagents == 1 ? "" : "s")}");
        if (_activeBackgroundTasks > 0)
            parts.Add($"{_activeBackgroundTasks} background task{(_activeBackgroundTasks == 1 ? "" : "s")}");
        if (_activeWorkflows > 0)
            parts.Add($"{_activeWorkflows} workflow{(_activeWorkflows == 1 ? "" : "s")}");

        if (parts.Count > 0)
            text += $"  ·  {string.Join(" · ", parts)} running";

        return text;
    }

    // Refresh the status line in place after the running count changes (the animation
    // timer also picks it up on its next tick; this makes the update immediate).
    private void RefreshWorkingStatus()
    {
        if (_session?.State == ClaudeSessionState.Working)
            SetStatus(WorkingStatusText());
    }

    private void StopWorkingAnimation()
    {
        _workingTimer?.Stop();
        _workingTimer = null;
    }

    private void OnInitialized(ClaudeSystemInit init)
    {
        AddSystemMessage("Session ready — type a message to begin");
        if (_session?.IsDangerousMode == true)
            AddSystemMessage("WARNING: Dangerous mode — all permissions auto-approved", isWarning: true);

        if (!string.IsNullOrEmpty(init.Model) && !init.Model.StartsWith("("))
            SessionModelChanged?.Invoke(init.Model);
    }

    // --- Sending Messages ---

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrentMessage();
    private async void EndSession_Click(object sender, RoutedEventArgs e)
    {
        StopWorkingAnimation();
        EndSessionButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        SetStatus("Stopping...");

        RemoveWaitingBubble();
        RemoveInactivityWarning();
        FinalizeActivity(null);
        _queue.Clear();

        var session = _session;
        _session = null;
        _processor = null; // queued ops are dropped by ApplyOps once _session is null
        if (session != null)
        {
            await session.StopAsync();
            session.Dispose();
        }

        AccountLabel = null;
        _sessionAccountId = null;
        ChatPanel.Visibility = Visibility.Collapsed;
        StartPanel.Visibility = Visibility.Visible;
        EndSessionButton.IsEnabled = true;
        SetStatus("");
        ClearMessages();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            SendCurrentMessage();
        }
    }

    // --- Slash Command Autocomplete ---

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text;
        UpdateComposerButtons();

        if (!text.StartsWith('/') || text.Any(char.IsWhiteSpace))
        {
            SlashCommandPopup.IsOpen = false;
            return;
        }

        var matches = ClaudeSlashCommands.Filter(text);
        if (matches.Count == 0)
        {
            SlashCommandPopup.IsOpen = false;
            return;
        }

        SlashCommandList.ItemsSource = matches;
        SlashCommandList.SelectedIndex = 0;
        SlashCommandPopup.IsOpen = true;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Intercept Ctrl+V for images before the TextBox's Paste command gets a
        // chance. A plain TextBox disables its Paste command whenever the clipboard
        // holds no text-compatible format, so an image-only clipboard (e.g. a
        // Snipping Tool screenshot) would otherwise do nothing — and the
        // DataObject.Pasting handler below never fires. Reading the clipboard here
        // sidesteps that gate. Falls through to the normal text paste when there's
        // no image.
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (TryAttachClipboardImages())
            {
                e.Handled = true;
                return;
            }
        }

        if (!SlashCommandPopup.IsOpen)
            return;

        switch (e.Key)
        {
            case Key.Down:
                if (SlashCommandList.Items.Count > 0)
                {
                    SlashCommandList.SelectedIndex = Math.Min(
                        SlashCommandList.SelectedIndex + 1,
                        SlashCommandList.Items.Count - 1);
                    SlashCommandList.ScrollIntoView(SlashCommandList.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (SlashCommandList.Items.Count > 0)
                {
                    SlashCommandList.SelectedIndex = Math.Max(SlashCommandList.SelectedIndex - 1, 0);
                    SlashCommandList.ScrollIntoView(SlashCommandList.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Tab:
                if (SlashCommandList.SelectedItem is ClaudeSlashCommand cmd)
                {
                    CompleteSlashCommand(cmd);
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                SlashCommandPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SlashCommandList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is ListBoxItem && SlashCommandList.SelectedItem is ClaudeSlashCommand cmd)
        {
            CompleteSlashCommand(cmd);
            e.Handled = true;
        }
    }

    private void CompleteSlashCommand(ClaudeSlashCommand cmd)
    {
        InputBox.Text = cmd.Command + " ";
        InputBox.CaretIndex = InputBox.Text.Length;
        SlashCommandPopup.IsOpen = false;
        InputBox.Focus();
    }

    // --- Prompt Menu (> button) ---

    private void PromptMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = null,
            PlacementTarget = PromptMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top
        };

        var hasSession = _session != null;
        var isIdle = hasSession && _session!.State == ClaudeSessionState.Idle;

        AddMenuItem(menu, "Clear Chat", "/clear", isIdle, ExecuteLocalCommand);
        AddMenuItem(menu, "Compact History", "/compact", isIdle, ExecuteLocalCommand);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "End Session", "/stop", hasSession, ExecuteLocalCommand);
        AddMenuItem(menu, "Open Logs Folder", "/logs", true, ExecuteLocalCommand);

        menu.IsOpen = true;
    }

    private static void AddMenuItem(ContextMenu menu, string header, string command, bool isEnabled, Action<string> handler)
    {
        var item = new MenuItem
        {
            Header = $"{header}  {command}",
            IsEnabled = isEnabled,
            Tag = command
        };
        item.Click += (_, _) => handler(command);
        menu.Items.Add(item);
    }

    // --- Slash Command Handling ---

    private void SendCurrentMessage()
    {
        var text = InputBox.Text.Trim();
        var hasImages = PendingAttachments.Count > 0;
        if (string.IsNullOrEmpty(text) && !hasImages)
            return;

        // Local commands are handled regardless of session state and are never queued;
        // consume the input. Images force a real send — a slash command can't carry them.
        if (!hasImages && text.StartsWith('/') && TryHandleLocalCommand(text))
        {
            InputBox.Text = "";
            return;
        }

        // A live session is required to send or queue — except on a remote head, where the
        // session lives on the server and the wire is the send path.
        if (_session == null && !IsRemote)
            return;

        // Snapshot the queued images for both the payload and the bubble, then clear
        // the input area — the message now belongs to the send path (immediate or queued).
        var attachments = PendingAttachments.Select(a => a.ToAttachment()).ToList();
        var thumbnails = hasImages
            ? PendingAttachments.Select(a => a.Thumbnail).ToList()
            : null;
        PendingAttachments.Clear();
        InputBox.Text = "";

        // Dispatch immediately only when the session is idle AND nothing is already
        // queued ahead of this — otherwise append so strict FIFO order is preserved
        // (a new message can't jump the queue, and a busy session no longer drops it).
        // On a remote head the server owns the queue, so everything goes straight out and the
        // server decides whether to run it now or queue it. Keeping a second queue here would
        // give the two machines different ideas about the order.
        var idle = IsRemote
            ? _remoteState?.State == ClaudeSessionState.Idle
            : _session!.State == ClaudeSessionState.Idle;

        if (IsRemote || (idle && _queue.IsEmpty))
            Dispatch(text, attachments, thumbnails);
        else
            _queue.Enqueue(new QueuedMessage
            {
                Text = text,
                Attachments = attachments,
                Thumbnails = thumbnails,
            });

        FocusInput();
    }

    // The actual send: finalize the previous turn's activity bubble, echo the user
    // message, show the waiting placeholder, and hand the text + images to the session.
    // Shared by the immediate-send path and the queue pump so both behave identically.
    private void Dispatch(
        string text,
        IReadOnlyList<ImageAttachment> attachments,
        IReadOnlyList<ImageSource>? thumbnails,
        Guid? bubbleId = null)
    {
        // Remote mode: the agent lives on the server, so the text goes on the wire.
        if (TrySendRemote(text, attachments, thumbnails)) return;

        // Defensive turn-boundary finalize — should already have happened on the
        // previous result, but covers edge cases (manual /compact mid-stream etc.).
        FinalizeActivity(null);
        _processor?.Reset();

        _lastMessageSentTime = DateTime.UtcNow;
        AddUserMessage(text, thumbnails, bubbleId);
        ShowWaitingBubble();
        _session!.SendMessage(text, attachments);
    }

    // --- Send Queue (the outbox) ---

    // The Send button is enabled whenever the session can take work — idle (dispatch
    // now) or working (append to the queue). It's disabled only when there's no live
    // session or it has exited.
    private void UpdateSendButtonEnabled()
    {
        // In remote mode there is no local session to ask; the authoritative state is the
        // replica the server pushed.
        var state = IsRemote ? _remoteState?.State : _session?.State;
        SendButton.IsEnabled = state is ClaudeSessionState.Idle or ClaudeSessionState.Working;
    }

    // The dropdown half of the split Send button. Send is the default action, so the
    // menu only exists to reach the alternative (schedule for later); "Send now" is
    // repeated there so the menu reads as a complete choice.
    private void SendMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = SendMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top
        };

        var sendNow = new MenuItem
        {
            Icon = MenuGlyph(SendGlyph),
            Header = "Send now",
            IsEnabled = SendButton.IsEnabled
        };
        sendNow.Click += (_, _) => SendCurrentMessage();
        menu.Items.Add(sendNow);

        var schedule = new MenuItem
        {
            Icon = MenuGlyph(ScheduleGlyph),
            Header = "Schedule for later…",
            IsEnabled = _session != null
        };
        schedule.Click += (_, _) => ShowScheduleDialog();
        menu.Items.Add(schedule);

        menu.IsOpen = true;
    }

    // Segoe MDL2 Assets code points for the send-menu icons.
    private const string SendGlyph = "\uE724";
    private const string ScheduleGlyph = "\uE823";

    private static TextBlock MenuGlyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 12
    };

    // Opens the compose dialog to add a time-scheduled message to the queue. Cancelling
    // / viewing pending items is done inline in the queue panel, so there's no separate
    // manage dialog.
    private void ShowScheduleDialog()
    {
        if (_session == null) return;

        var owner = Window.GetWindow(this)!;
        // Seed the dialog with whatever's drafted — including nothing. The message is
        // editable there, so scheduling can start from an empty input box.
        var result = ScheduleMessageDialog.ShowCompose(owner, InputBox.Text.Trim());
        if (!result.HasValue) return;

        // The draft (and any attachments it was composed alongside) now belongs to the
        // scheduled message — consume them so they aren't also sent from the input box.
        var attachments = PendingAttachments.Select(a => a.ToAttachment()).ToList();
        var thumbnails = PendingAttachments.Count > 0
            ? PendingAttachments.Select(a => a.Thumbnail).ToList()
            : null;
        PendingAttachments.Clear();
        InputBox.Text = "";

        _queue.Enqueue(new QueuedMessage
        {
            Text = result.Value.message,
            Attachments = attachments,
            Thumbnails = thumbnails,
            NotBeforeUtc = DateTime.UtcNow + result.Value.delay,
        });
        FocusInput();
    }

    // Remove (✕) button on a queue-panel row.
    private void RemoveQueued_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
            _queue.Remove(id);
    }

    // Runs whenever the queue's contents change: toggle the panel, (re)start or stop
    // the per-second timer, refresh the countdown labels + clock tint, and notify the
    // tab strip.
    private void OnQueueChanged()
    {
        QueueBar.Visibility = _queue.IsEmpty ? Visibility.Collapsed : Visibility.Visible;

        if (_queue.IsEmpty)
            StopQueueTimer();
        else
            EnsureQueueTimer();

        RefreshQueueStatuses();
        UpdateScheduleButtonVisual();
        ScheduleChanged?.Invoke();
    }

    private void EnsureQueueTimer()
    {
        if (_queueTimer != null) return;
        _queueTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _queueTimer.Tick += (_, _) =>
        {
            RefreshQueueStatuses();
            PumpQueue();
        };
        _queueTimer.Start();
    }

    private void StopQueueTimer()
    {
        _queueTimer?.Stop();
        _queueTimer = null;
    }

    private void RefreshQueueStatuses()
    {
        var now = DateTime.UtcNow;
        foreach (var message in _queue.Items)
            message.RefreshStatus(now);
    }

    // Dispatches the head of the queue if it's ready and the session is idle. Called on
    // return-to-idle (OnStateChanged) and on each queue-timer tick. Sends exactly one
    // message — SendMessage flips the session to Working synchronously, so a re-entrant
    // pump before the next idle can't double-send.
    private void PumpQueue()
    {
        if (_session == null || _session.State != ClaudeSessionState.Idle) return;

        var head = _queue.PeekReady(DateTime.UtcNow);
        if (head == null) return;

        _queue.DequeueHead();
        Dispatch(head.Text, head.Attachments, head.Thumbnails, head.BubbleId);
    }

    // The clock button folded into the Send split button's dropdown, so a pending
    // scheduled message is signalled by tinting the dropdown arrow instead.
    private void UpdateScheduleButtonVisual()
    {
        var hasScheduled = _queue.NextScheduledFireUtc != null;
        SendMenuIcon.Foreground = hasScheduled
            ? _theme.GetBrush("ChatStatusWarningForeground")
            : _theme.GetBrush("ChatButtonForeground");
        SendMenuButton.ToolTip = hasScheduled
            ? "A scheduled message is waiting — click for send options"
            : "More send options";
    }

    /// <summary>
    /// Returns true if the command was handled locally (should not be sent to Claude).
    /// </summary>
    private bool TryHandleLocalCommand(string text)
    {
        var command = text.Split(' ', 2)[0].ToLowerInvariant();
        switch (command)
        {
            case "/clear":
                ExecuteLocalCommand("/clear");
                return true;
            case "/stop":
                ExecuteLocalCommand("/stop");
                return true;
            case "/logs":
                ExecuteLocalCommand("/logs");
                return true;
            default:
                return false;
        }
    }

    private void ExecuteLocalCommand(string command)
    {
        if (TryLocalCommandRemote(command)) return;

        switch (command)
        {
            case "/clear":
                if (_session != null && _session.State == ClaudeSessionState.Idle)
                {
                    ClearMessages();
                    AddSystemMessage("Chat cleared.");
                }
                break;

            case "/compact":
                if (_session != null && _session.State == ClaudeSessionState.Idle)
                {
                    FinalizeActivity(null);
                    _processor?.Reset();
                    _lastMessageSentTime = DateTime.UtcNow;
                    AddUserMessage("/compact");
                    ShowWaitingBubble();
                    _session.SendMessage("/compact");
                }
                break;

            case "/stop":
                EndSession_Click(this, new RoutedEventArgs());
                break;

            case "/logs":
                var logPath = _log.LogFilePath;
                if (logPath != null)
                {
                    var folder = System.IO.Path.GetDirectoryName(logPath);
                    if (folder != null && System.IO.Directory.Exists(folder))
                        System.Diagnostics.Process.Start("explorer.exe", folder);
                }
                break;
        }
    }

    // --- Image Attachments ---

    // The command-driven paste path (e.g. the right-click "Paste" menu item, or a
    // Ctrl+V that carried text alongside an image). Ctrl+V for an image-only
    // clipboard is handled earlier in InputBox_PreviewKeyDown — the Paste command
    // never executes without a text format, so this handler wouldn't fire for it.
    private void InputBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (TryAttachClipboardImages())
            e.CancelCommand();
    }

    // Reads the system clipboard for image file(s) or a raw/screenshot bitmap and,
    // if found, queues them as attachments. Returns true when at least one image was
    // attached, so the caller can suppress the default text paste.
    private bool TryAttachClipboardImages()
    {
        try
        {
            // Image files copied from Explorer come through as a file-drop list.
            if (Clipboard.ContainsFileDropList())
            {
                var images = Clipboard.GetFileDropList().Cast<string>()
                    .Where(_images.IsSupportedImageFile).ToList();
                if (images.Count > 0)
                {
                    foreach (var path in images)
                        AddAttachment(_images.FromFile(path));
                    return true;
                }
            }

            // A screenshot / copied image arrives as raw bitmap data (DIB/CF_BITMAP)
            // and/or a PNG stream — some sources provide only the latter, which
            // ContainsImage() doesn't report, so check both.
            if (Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"))
            {
                var attachment = _images.FromClipboard();
                if (attachment != null)
                {
                    AddAttachment(attachment);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"AiChatControl: clipboard image paste failed — {ex.Message}");
        }
        return false;
    }

    private void InputPanel_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasDroppableImages(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void InputPanel_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;

        var images = files.Where(_images.IsSupportedImageFile).ToList();
        if (images.Count == 0)
            return;

        foreach (var path in images)
            AddAttachment(_images.FromFile(path));
        e.Handled = true;
        FocusInput();
    }

    private bool HasDroppableImages(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop)
           && e.Data.GetData(DataFormats.FileDrop) is string[] files
           && files.Any(_images.IsSupportedImageFile);

    private void AddAttachment(PendingImageAttachment? attachment)
    {
        if (attachment != null)
            PendingAttachments.Add(attachment);
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingImageAttachment a })
            PendingAttachments.Remove(a);
    }

    // Click a chip's thumbnail to open the larger lightbox, from which the image
    // can be removed (Gmail-style).
    private void PreviewAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PendingImageAttachment a })
            return;

        var owner = Window.GetWindow(this)!;
        var preview = _images.CreatePreview(a);
        if (ImagePreviewDialog.Show(owner, preview, a.DisplayName))
            PendingAttachments.Remove(a);
    }

    // The floating clear (✕) button: discard the drafted text and any queued attachments.
    private void ClearInput_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Text = "";
        PendingAttachments.Clear();
        FocusInput();
    }

    // Entering/leaving the composer toggles the floating clear button.
    private void InputArea_MouseChanged(object sender, MouseEventArgs e) => UpdateComposerButtons();

    // Shows the attachments bar only when something is queued, and keeps the
    // clear button in sync.
    private void UpdateAttachmentsBar()
    {
        AttachmentsBar.Visibility = PendingAttachments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateComposerButtons();
    }

    // The clear button floats over the composer instead of taking a slot in the button
    // column, so it can appear and vanish without reflowing anything: shown only while
    // the pointer is over the composer and there's something to discard.
    private void UpdateComposerButtons()
    {
        var hasContent = !string.IsNullOrEmpty(InputBox.Text) || PendingAttachments.Count > 0;
        ClearInputButton.Visibility = hasContent && InputArea.IsMouseOver
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // --- Message Collection Helpers ---

    private void AddUserMessage(string text, IReadOnlyList<ImageSource>? images = null, Guid? id = null)
    {
        // Sending always snaps to the newest message, even if the user had
        // scrolled up to read history.
        _stickToBottom = true;
        var message = new UserMessage(id ?? Guid.NewGuid(), text, images);
        Messages.Add(message);

        // User and system bubbles are appended directly rather than through a ChatOp,
        // so they need their own replication hook or a client would never see them.
        RaiseTranscriptAppended(message);
    }

    private void AddSystemMessage(string text, bool isWarning = false)
    {
        var message = new SystemMessage(Guid.NewGuid(), text, isWarning);
        Messages.Add(message);
        RaiseTranscriptAppended(message);
    }

    private void ShowWaitingBubble()
    {
        if (_waitingVm != null) return; // already shown
        _waitingVm = new WaitingMessage(Guid.NewGuid());
        Messages.Add(_waitingVm);
    }

    private void RemoveWaitingBubble()
    {
        if (_waitingVm == null) return;
        Messages.Remove(_waitingVm);
        _waitingVm = null;
    }

    /// <summary>
    /// Empties the collection and resets every live-tail reference. Used by
    /// /clear and Stop. Without resetting the refs we'd hold pointers to
    /// VMs that are no longer in the collection.
    /// </summary>
    private void ClearMessages()
    {
        _inactivityElapsedTimer?.Stop();
        _inactivityElapsedTimer = null;
        // Stop the activity header animation and flush any in-flight streaming
        // thinking so nothing keeps ticking after the transcript is cleared.
        StopActivityAnimation();
        _activityVm?.Freeze();
        Messages.Clear();
        PendingAttachments.Clear();
        _activityVm = null;
        _waitingVm = null;
        _inactivityVm = null;
        _processor?.Reset();
    }

    // --- Content Block / Stream Events ---

    // Applies the TurnComplete op. Finalizes the activity bubble here (rather than
    // on an earlier op) so its header can show the turn's duration. The whole result
    // op-batch is applied in one synchronous pass, so collapsing the bubble and
    // posting the answer (PostAnswerOp, just before this) render together — no flicker.
    private void ApplyTurnComplete(ClaudeResultMessage result)
    {
        FinalizeActivity(result.DurationMs.HasValue ? result.DurationMs.Value / 1000.0 : null);

        if (result.IsError)
            ReportTurnError(result);

        var (costDelta, tokenDelta) = Stats.Update(result);

        var parts = new List<string>();
        if (costDelta > 0)
            parts.Add($"${costDelta:F4}");
        if (result.DurationMs.HasValue)
            parts.Add($"{result.DurationMs / 1000.0:F1}s");
        if (tokenDelta > 0)
            parts.Add($"{SessionStats.FormatTokens(tokenDelta)} tokens");
        if (parts.Count > 0)
        {
            var costText = string.Join(" | ", parts);
            costText += $"  (session: ${Stats.TotalCostUsd:F4}, {SessionStats.FormatTokens(Stats.TotalTokens)} tokens)";
            AddSystemMessage(costText);
        }

        SessionStatsChanged?.Invoke(Stats);
    }

    /// <summary>
    /// Reports a failed turn in the transcript. Prefers the structured <c>errors</c>
    /// list, falling back to the result text.
    /// </summary>
    /// <remarks>
    /// The fallback matters: an auth failure arrives as a synthetic result with
    /// <c>is_error: true</c>, an empty <c>errors</c> list, zero tokens and zero cost, and
    /// the reason only in <c>result</c>. Gating the message on a non-empty <c>errors</c>
    /// list meant those turns finished in total silence — the session looked like it had
    /// answered with nothing, which is how two revoked accounts went unnoticed.
    ///
    /// A zero-token error against a named account is also the signature of that account
    /// being signed out, so confirm it and point at the fix rather than leaving the user
    /// to interpret a raw CLI message.
    /// </remarks>
    private void ReportTurnError(ClaudeResultMessage result)
    {
        var detail = result.Errors?.Count > 0
            ? string.Join("; ", result.Errors)
            : string.IsNullOrWhiteSpace(result.Result)
                ? "the turn failed without reporting a reason"
                : result.Result.Trim();

        AddSystemMessage($"Error: {detail}", isWarning: true);

        var noWorkDone = result.InputTokens == 0 && result.OutputTokens == 0;
        if (noWorkDone && _sessionAccountId != null && !_accounts.IsLoggedIn(_sessionAccountId))
        {
            // No session restart needed: turns are one-shot with --resume, so SessionId
            // outlives the failed process. Signing back in and re-sending picks the
            // conversation up where it stopped.
            AddSystemMessage(
                $"The \"{AccountLabel}\" account is signed out, so this turn couldn't run. " +
                "Open Claude Accounts, select it, and click Log In — a terminal window will " +
                "open for the browser sign-in. Then just send your message again; this " +
                "session resumes where it left off.",
                isWarning: true);
        }
    }

    // --- Activity bubble lifecycle ---

    // Ensures the one activity bubble for this turn exists, dropping the "Thinking..."
    // placeholder and starting the animated header.
    private void EnsureActivityVm()
    {
        if (_activityVm != null) return;
        RemoveWaitingBubble();
        _activityVm = new ActivityMessage(Guid.NewGuid());
        StartActivityAnimation();
        Messages.Add(_activityVm);
    }

    // Finalizes the active bubble: flush in-flight thinking, stop the animation, and
    // either drop it (no content) or collapse it with a "Worked for Ns" header.
    // <paramref name="durationSeconds"/> is null when finalizing outside a normal
    // turn end (stop / clear / defensive), in which case a neutral label is used.
    private void FinalizeActivity(double? durationSeconds)
    {
        if (_activityVm == null) return;
        StopActivityAnimation();
        _activityVm.Freeze();

        if (!_activityVm.HasEntries)
        {
            Messages.Remove(_activityVm);
        }
        else
        {
            _activityVm.Header = durationSeconds.HasValue
                ? $"Worked for {durationSeconds.Value:F1}s"
                : "Activity";
            _activityVm.IsExpanded = false;
        }
        _activityVm = null;
    }

    // --- Activity header animation (whimsical verb + cycling dots) ---

    private void StartActivityAnimation()
    {
        _activityTick = 0;
        _activityVerb = ActivityVerbs[s_rng.Next(ActivityVerbs.Length)];
        UpdateActivityHeader();
        _activityHeaderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _activityHeaderTimer.Tick += (_, _) =>
        {
            _activityTick++;
            // Switch to a fresh verb every ~2.4s so it reads as ongoing work.
            if (_activityTick % 8 == 0)
                _activityVerb = ActivityVerbs[s_rng.Next(ActivityVerbs.Length)];
            UpdateActivityHeader();
        };
        _activityHeaderTimer.Start();
    }

    private void StopActivityAnimation()
    {
        _activityHeaderTimer?.Stop();
        _activityHeaderTimer = null;
    }

    private void UpdateActivityHeader()
    {
        if (_activityVm == null) return;
        var dots = (_activityTick % 3) + 1;
        _activityVm.Header = _activityVerb + new string('.', dots);
    }

    // --- Permission Handling ---

    private void OnPermissionRequested(ClaudePermissionRequest req)
    {
        // A prompt is only replicated when this panel owns the session; in remote mode
        // the prompt arrived from the server and re-publishing it would loop.
        if (!IsRemote) PermissionRequestRaised?.Invoke(req);

        if (req.ToolName == "AskUserQuestion" && req.Input.ValueKind == JsonValueKind.Object)
        {
            ShowQuestionPanel(req);
            return;
        }

        PermissionToolName.Text = $"Tool: {req.ToolName}";

        var detail = req.Input.ValueKind == JsonValueKind.Object
            ? FormatToolInput(req.Input)
            : req.Input.ToString();
        PermissionDetail.Text = detail;

        InputPanel.Visibility = Visibility.Collapsed;
        PermissionPanel.Visibility = Visibility.Visible;
    }

    private void ShowQuestionPanel(ClaudePermissionRequest req)
    {
        QuestionOptionsPanel.Children.Clear();
        _pendingQuestionText = null;

        try
        {
            if (!req.Input.TryGetProperty("questions", out var questions) || questions.GetArrayLength() == 0)
            {
                OnPermissionRequested(new ClaudePermissionRequest
                {
                    RequestId = req.RequestId,
                    ToolName = "AskUserQuestion (parse failed)",
                    Input = req.Input
                });
                return;
            }

            var firstQuestion = questions[0];
            var questionText = firstQuestion.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
            _pendingQuestionText = questionText;

            QuestionText.Text = questionText;

            if (firstQuestion.TryGetProperty("options", out var options))
            {
                foreach (var option in options.EnumerateArray())
                {
                    var label = option.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    var desc = option.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

                    var btn = new Button
                    {
                        Template = (ControlTemplate)FindResource("ChatButtonChrome"),
                        Cursor = Cursors.Hand,
                        Background = _theme.GetBrush("ChatOptionButtonBackground"),
                        BorderBrush = _theme.GetBrush("ChatOptionButtonBorderBrush"),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 0, 0, 4),
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };

                    var panel = new StackPanel();
                    panel.Children.Add(new TextBlock
                    {
                        Text = label,
                        FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                        FontSize = 12,
                        Foreground = _theme.GetBrush("ChatTextForeground")
                    });
                    if (!string.IsNullOrEmpty(desc))
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = desc,
                            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                            FontSize = 10,
                            Foreground = _theme.GetBrush("ChatMutedForeground"),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                    btn.Content = panel;

                    var capturedLabel = label;
                    btn.Click += (_, _) => SubmitQuestionAnswer(capturedLabel);

                    QuestionOptionsPanel.Children.Add(btn);
                }
            }

            QuestionCustomInput.Text = "";
            InputPanel.Visibility = Visibility.Collapsed;
            QuestionPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _log.Error("Failed to parse AskUserQuestion input", ex);
            PermissionToolName.Text = $"Tool: {req.ToolName}";
            PermissionDetail.Text = FormatToolInput(req.Input);
            InputPanel.Visibility = Visibility.Collapsed;
            PermissionPanel.Visibility = Visibility.Visible;
        }
    }

    private void SubmitQuestionAnswer(string answer)
    {
        if (TryAnswerQuestionRemote(_pendingQuestionText ?? "", answer)) return;

        if (_session == null || _pendingQuestionText == null)
            return;

        _session.AnswerQuestion(_pendingQuestionText, answer);
        _pendingQuestionText = null;

        QuestionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;

        // Echo the chosen/typed answer into the chat so the user sees what they
        // submitted, mirroring the normal SendMessage flow.
        _lastMessageSentTime = DateTime.UtcNow;
        AddUserMessage(answer);
        ShowWaitingBubble();
    }

    private void QuestionCustomSend_Click(object sender, RoutedEventArgs e)
    {
        var text = QuestionCustomInput.Text.Trim();
        if (!string.IsNullOrEmpty(text))
            SubmitQuestionAnswer(text);
    }

    private void QuestionCustomInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Match the main composer: Enter sends, Shift+Enter inserts a newline so
        // multi-line answers (e.g. pasted or dictated text) can be typed here.
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            var text = QuestionCustomInput.Text.Trim();
            if (!string.IsNullOrEmpty(text))
                SubmitQuestionAnswer(text);
        }
    }

    private void PermissionAllow_Click(object sender, RoutedEventArgs e)
    {
        if (TryAnswerRemote(allow: true, denyReason: null)) return;

        _session?.AllowPermission();
        PermissionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
        PermissionCleared?.Invoke();
    }

    private void PermissionDeny_Click(object sender, RoutedEventArgs e)
    {
        if (TryAnswerRemote(allow: false, denyReason: null)) return;

        _session?.DenyPermission();
        PermissionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
        PermissionCleared?.Invoke();
    }

    // --- Inactivity Warning ---

    private void OnInactivityTimeout()
    {
        if (_session == null || _inactivityVm != null)
            return;

        _inactivityVm = new InactivityWarning(Guid.NewGuid())
        {
            ElapsedText = FormatElapsed(DateTime.UtcNow - _lastMessageSentTime),
            OnWait = () =>
            {
                RemoveInactivityWarning();
                _session?.ExtendInactivityTimer();
            },
            OnKill = () =>
            {
                RemoveInactivityWarning();
                EndSession_Click(this, new RoutedEventArgs());
            }
        };
        Messages.Add(_inactivityVm);

        _inactivityElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _inactivityElapsedTimer.Tick += (_, _) =>
        {
            if (_inactivityVm != null)
                _inactivityVm.ElapsedText = FormatElapsed(DateTime.UtcNow - _lastMessageSentTime);
        };
        _inactivityElapsedTimer.Start();

        ScrollToBottom();
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
            return $"Waiting for {(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        return $"Waiting for {(int)elapsed.TotalSeconds}s";
    }

    private void RemoveInactivityWarning()
    {
        _inactivityElapsedTimer?.Stop();
        _inactivityElapsedTimer = null;
        if (_inactivityVm != null)
        {
            Messages.Remove(_inactivityVm);
            _inactivityVm = null;
        }
    }

    // --- Error / Exit ---

    private void OnErrorOutput(string text)
    {
        if (text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            AddSystemMessage(text, isWarning: true);
        }
    }

    private void OnProcessExited(int exitCode)
    {
        if (exitCode != 0)
            AddSystemMessage($"Claude process exited with code {exitCode}", isWarning: true);
        else
            AddSystemMessage("Session ended");
    }

    // --- DataTemplate Click Handlers ---

    // For finalized AssistantMessage: toggling the source/rendered view replaces
    // the immutable VM with a new instance carrying the flipped flag. The
    // ItemsControl re-evaluates the ContentControl's ContentTemplate selector
    // and swaps just that one container's child template.
    private void MarkdownToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AssistantMessage msg)
        {
            var idx = Messages.IndexOf(msg);
            if (idx >= 0)
            {
                // Pass the same memoizing builder — toggling the view doesn't
                // re-parse markdown (the closure caches the built document).
                Messages[idx] = new AssistantMessage(
                    msg.Id, msg.Text, !msg.IsMarkdownView, msg.HasMarkdownToggle, msg.MarkdownBuilder);
            }
        }
    }

    // ActivityMessage stays a single mutable VM (building → frozen), so toggling
    // expand is just a property flip — no instance swap needed.
    private void ActivityHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ActivityMessage msg)
            msg.IsExpanded = !msg.IsExpanded;
        e.Handled = true;
    }

    private void InactivityWait_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is InactivityWarning msg)
            msg.OnWait?.Invoke();
    }

    private void InactivityKill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is InactivityWarning msg)
            msg.OnKill?.Invoke();
    }

    // Container recycling: DataContext changes when the container is reused
    // for a different message. Attach the cached FlowDocument — no re-parse,
    // no re-wire of file links.
    private void AssistantMarkdown_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AttachAssistantDocument((MarkdownScrollViewer)sender, e.NewValue as AssistantMessage);

    // Initial realization — DataContextChanged may fire before the viewer is
    // in the visual tree, so attach again here defensively.
    private void AssistantMarkdown_Loaded(object sender, RoutedEventArgs e)
        => AttachAssistantDocument((MarkdownScrollViewer)sender, ((MarkdownScrollViewer)sender).DataContext as AssistantMessage);

    private void AttachAssistantDocument(MarkdownScrollViewer viewer, AssistantMessage? msg)
    {
        // Idempotent — the helper hooks each viewer once, however often containers recycle.
        _markdown.EnableMarkdownLinkCopy(viewer);

        // GetMarkdown builds the document on first realize (UI thread) and caches
        // it; subsequent realizations of the same message reuse the instance.
        var doc = msg?.GetMarkdown();
        if (doc == null)
        {
            viewer.Document = null;
            return;
        }
        if (ReferenceEquals(viewer.Document, doc))
            return;

        // FlowDocument has at most one logical parent. If our cached document
        // is currently attached to another (recycled) viewer, detach it first.
        if (doc.Parent is System.Windows.Controls.FlowDocumentScrollViewer prev
            && !ReferenceEquals(prev, viewer))
        {
            prev.Document = null;
        }

        viewer.Document = doc;
    }

    // --- Scroll + Wheel ---

    private ScrollViewer? GetScrollViewer()
    {
        if (_scrollViewer != null) return _scrollViewer;
        _scrollViewer = FindVisualChild<ScrollViewer>(MessageList);
        if (_scrollViewer != null)
            _scrollViewer.ScrollChanged += OnScrollChanged;
        return _scrollViewer;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindVisualChild<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    // Whether this control's session is currently counted in the global
    // "working sessions" tally (see OnStateChanged / PerfDiagnostics).
    private bool _countedAsWorking;

    private void ScrollToBottom()
    {
        var sv = GetScrollViewer();
        if (sv != null)
            sv.ScrollToEnd();
    }

    /// <summary>
    /// Single source of truth for sticky-bottom follow. Runs whenever the chat
    /// scroll state changes. All decisions use the event's precomputed metrics —
    /// reading the ScrollViewer's live ScrollableHeight/VerticalOffset here (or
    /// on the streaming hot path) would force a layout pass and was a measured
    /// contributor to prompt-box lag.
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        // ScrollChanged bubbles, so an activity bubble's inner scroller raises events
        // that reach this outer handler too. Ignore those — the inner scroller manages
        // its own sticky-bottom (see AutoScroll); reacting here would corrupt the
        // outer follow state.
        if (!ReferenceEquals(e.OriginalSource, sv)) return;

        if (e.ExtentHeightChange != 0)
        {
            // Content grew or shrank (a streaming delta, a new bubble). If we
            // were following the bottom, re-pin to the new bottom. Don't touch
            // the flag — growth alone must not unstick us.
            if (_stickToBottom) sv.ScrollToEnd();
        }
        else if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
        {
            // The viewport position changed without the content changing — i.e.
            // the user scrolled, or the pane was resized. Follow only while the
            // bottom is (near) in view.
            _stickToBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - StickyBottomThreshold;
        }
    }

    /// <summary>
    /// Sticky-bottom auto-scroll: when a message is added and the user is at
    /// (or near) the bottom of the chat, scroll to follow. If they've scrolled
    /// up to read history, leave them where they are.
    /// </summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        if (!_stickToBottom) return;

        var sv = GetScrollViewer();
        if (sv == null)
        {
            // Template not applied yet — schedule scroll after first render.
            Dispatcher.BeginInvoke(ScrollToBottom, DispatcherPriority.Loaded);
            return;
        }

        // Defer until after the new container is realized & measured; the
        // resulting extent change also re-pins via OnScrollChanged.
        Dispatcher.BeginInvoke(() => sv.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void MessageList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var outer = GetScrollViewer();
        if (outer == null) return;

        // If the pointer is over an activity bubble's inner scroller that can still
        // scroll in the wheel direction, scroll it instead of the whole pane. (Preview
        // tunnels top-down, so without this the outer handler would always win and the
        // inner scroller would only be usable via its scrollbar.) When the inner scroller
        // hits its edge, the wheel falls through to the outer pane.
        var inner = FindInnerScrollViewer(e.OriginalSource as DependencyObject, outer);
        if (inner != null && InnerCanScroll(inner, e.Delta))
        {
            inner.ScrollToVerticalOffset(inner.VerticalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // Walks up from the wheel's target to find a ScrollViewer nested inside the outer
    // pane (i.e. an activity bubble's inner scroller), or null if the target isn't
    // inside one.
    private static ScrollViewer? FindInnerScrollViewer(DependencyObject? origin, ScrollViewer outer)
    {
        var node = origin;
        while (node != null && !ReferenceEquals(node, outer))
        {
            if (node is ScrollViewer sv)
                return sv;
            node = GetParentObject(node);
        }
        return null;
    }

    // Walks one step up the tree. The wheel target can be a text ContentElement
    // (Run, Span, Paragraph) inside a bubble's FlowDocument — those aren't Visuals,
    // so VisualTreeHelper.GetParent throws on them. Climb the logical tree for those
    // until we re-enter the visual tree at the document's host control.
    private static DependencyObject? GetParentObject(DependencyObject node)
    {
        if (node is FrameworkContentElement fce)
            return fce.Parent;
        if (node is ContentElement ce)
            return LogicalTreeHelper.GetParent(ce);
        return VisualTreeHelper.GetParent(node);
    }

    // True if the inner scroller has room to move in the wheel direction (delta > 0 is
    // a scroll up). Reads live offsets, but only on wheel ticks — not the streaming path.
    private static bool InnerCanScroll(ScrollViewer sv, int delta)
        => delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight - 0.5;

    // --- Helpers ---

    private static string FormatToolInput(JsonElement input)
    {
        try
        {
            return JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return input.ToString();
        }
    }
}
