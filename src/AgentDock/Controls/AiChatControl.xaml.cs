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
using MdXaml;

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

    // Live-tail references — set when the corresponding VM is in the
    // collection, nulled on finalize / removal.
    private StreamingThinkingMessage? _streamingThinkingVm;
    private BuildingExecutionMessage? _executionVm;
    private WaitingMessage? _waitingVm;
    private InactivityWarning? _inactivityVm;
    private DispatcherTimer? _inactivityElapsedTimer;

    // Animated working indicator in the status bar (not a message).
    private DispatcherTimer? _workingTimer;
    private int _spinnerIndex;
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // Pending AskUserQuestion — tracks question text for response.
    private string? _pendingQuestionText;

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
    /// Raised when cumulative session stats change (cost, tokens).
    /// </summary>
    public event Action<SessionStats>? SessionStatsChanged;

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

    public AiChatControl()
    {
        Log.Info("AiChatControl: constructor");
        InitializeComponent();
        MessageList.ItemsSource = Messages;
        Messages.CollectionChanged += OnMessagesChanged;
        Log.Info("AiChatControl: InitializeComponent complete");

        // Older Win10 builds don't ship the WinRT dictation recognizer — hide the
        // mic button rather than show one that would always error.
        if (!DictationService.IsSupportedOnThisOS)
            MicButton.Visibility = Visibility.Collapsed;
    }

    public void Initialize(string projectPath)
    {
        Log.Info($"AiChatControl: Initialize for '{projectPath}'");
        _projectPath = projectPath;
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
        _session?.Dispose();
        _session = null;
        _processor = null;
        _dictation?.Dispose();
        _dictation = null;
    }

    // --- Dictation ---

    private DictationService? _dictation;

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dictation == null)
        {
            _dictation = new DictationService();
            _dictation.TextRecognized += text => Dispatcher.BeginInvoke(() => InsertDictatedText(text));
            _dictation.StateChanged += state => Dispatcher.BeginInvoke(() => UpdateMicVisual(state));
            _dictation.ErrorOccurred += msg => Dispatcher.BeginInvoke(() => SetMicErrorTooltip(msg));
        }
        try
        {
            await _dictation.ToggleAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Dictation toggle failed", ex);
            SetMicErrorTooltip(ex.Message);
        }
    }

    private void InsertDictatedText(string text)
    {
        var caret = InputBox.CaretIndex;
        var existing = InputBox.Text;
        var needsLeadingSpace = caret > 0 && caret <= existing.Length
            && !char.IsWhiteSpace(existing[caret - 1]);
        var insert = (needsLeadingSpace ? " " : "") + text;
        InputBox.Text = existing.Insert(caret, insert);
        InputBox.CaretIndex = caret + insert.Length;
    }

    private void UpdateMicVisual(DictationState state)
    {
        switch (state)
        {
            case DictationState.Idle:
                MicIcon.Foreground = ThemeManager.GetBrush("ChatMutedForeground");
                MicButton.ToolTip = "Start dictation";
                break;
            case DictationState.Starting:
                MicIcon.Foreground = ThemeManager.GetBrush("ChatMutedForeground");
                MicButton.ToolTip = "Starting…";
                break;
            case DictationState.Listening:
                MicIcon.Foreground = ThemeManager.GetBrush("ChatDangerForeground");
                MicButton.ToolTip = "Listening — click to stop";
                break;
            case DictationState.Stopping:
                MicButton.ToolTip = "Stopping…";
                break;
            case DictationState.Error:
                MicIcon.Foreground = ThemeManager.GetBrush("ChatMutedForeground");
                break;
        }
    }

    private void SetMicErrorTooltip(string message)
    {
        MicButton.ToolTip = $"Dictation error: {message}";
    }

    // --- Start Session ---

    private void StartNormal_Click(object sender, RoutedEventArgs e) => StartSession(false);
    private void StartDangerous_Click(object sender, RoutedEventArgs e) => StartSession(true);

    private void StartSession(bool dangerous)
    {
        Log.Info($"AiChatControl: StartSession(dangerous={dangerous})");
        if (!ClaudeSession.IsClaudeAvailable())
        {
            StartError.Text = "Claude CLI not found in PATH. Install Claude Code first.";
            StartError.Visibility = Visibility.Visible;
            return;
        }

        StartError.Visibility = Visibility.Collapsed;

        _session = new ClaudeSession(_projectPath);
        WireSessionEvents();

        StartPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        StatusText.Text = "Initializing...";

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
    }

    private void ApplyOp(ChatOp op)
    {
        switch (op)
        {
            case RemoveInactivityOp: RemoveInactivityWarning(); break;
            case AppendThinkingOp a: ApplyAppendThinking(a.Text); break;
            case CommentaryOp c: ApplyCommentary(c.Text); break;
            case FinalizeThinkingOp: FinalizeThinking(); break;
            case EnsureExecutionOp: EnsureExecutionVm(); break;
            case AddToolOp t: _executionVm?.Tools.Add(new ToolEntry(t.Name, t.FormattedInput)); break;
            case FinalizeExecutionOp: FinalizeExecution(); break;
            case PostAnswerOp p: ApplyPostAnswer(p.Text); break;
            case TurnCompleteOp tc: ApplyTurnComplete(tc.Result); break;
        }
    }

    // Ensures the live thinking window exists, then appends. The processor has
    // already applied block separators and the redacted-thinking skip.
    private void ApplyAppendThinking(string text)
    {
        if (_streamingThinkingVm == null)
        {
            RemoveWaitingBubble();
            _streamingThinkingVm = new StreamingThinkingMessage(Guid.NewGuid());
            _streamingThinkingVm.PropertyChanged += OnStreamingTextChanged;
            Messages.Add(_streamingThinkingVm);
        }
        _streamingThinkingVm.AppendText(text);
    }

    // A buffered text block that turned out to be commentary — append it to the
    // thinking window (created if needed), separated from any existing content.
    private void ApplyCommentary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        RemoveWaitingBubble();
        if (_streamingThinkingVm == null)
        {
            _streamingThinkingVm = new StreamingThinkingMessage(Guid.NewGuid());
            _streamingThinkingVm.PropertyChanged += OnStreamingTextChanged;
            Messages.Add(_streamingThinkingVm);
        }
        else if (!string.IsNullOrEmpty(_streamingThinkingVm.Text))
        {
            _streamingThinkingVm.AppendText("\n\n");
        }
        _streamingThinkingVm.AppendText(text);
    }

    // The buffered text was the final answer — post it as a standalone, always-shown
    // assistant bubble (never inside the thinking group). The rendered FlowDocument
    // is built lazily on realize in a visible tab (see AttachAssistantDocument).
    private void ApplyPostAnswer(string text)
    {
        RemoveWaitingBubble();

        var hasNl = text.Contains('\n');
        Func<FlowDocument>? markdownBuilder = null;
        if (hasNl)
        {
            var style = (System.Windows.Style)FindResource("ChatMarkdownStyle");
            var projectPath = _projectPath;
            Action<string> onLink = p => FileReferenceClicked?.Invoke(p);
            FlowDocument? cached = null;
            markdownBuilder = () => cached ??= MarkdownHelper.BuildDocument(
                text, markdownStyle: style, projectPath: projectPath, onFileLinkClicked: onLink);
        }

        Messages.Add(new AssistantMessage(
            id: Guid.NewGuid(),
            text: text,
            isMarkdownView: hasNl,
            hasMarkdownToggle: hasNl,
            markdownBuilder: markdownBuilder));
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
            PerfDiagnostics.WorkingSessionDelta(nowWorking ? 1 : -1);
        }

        if (state == ClaudeSessionState.Working)
        {
            StartWorkingAnimation();
        }
        else
        {
            StatusText.Text = state switch
            {
                ClaudeSessionState.Initializing => "Initializing...",
                ClaudeSessionState.Idle => "Idle",
                ClaudeSessionState.WaitingForPermission => "Waiting for permission...",
                ClaudeSessionState.Exited => "Session ended",
                ClaudeSessionState.Error => "Error",
                _ => ""
            };
        }

        StatusText.Foreground = state switch
        {
            ClaudeSessionState.Working => ThemeManager.GetBrush("ChatStatusWorkingForeground"),
            ClaudeSessionState.WaitingForPermission => ThemeManager.GetBrush("ChatStatusWarningForeground"),
            ClaudeSessionState.Error => ThemeManager.GetBrush("ChatStatusErrorForeground"),
            _ => ThemeManager.GetBrush("ChatMutedForeground")
        };

        var showDanger = _session?.IsDangerousMode == true
            && state != ClaudeSessionState.NotStarted
            && state != ClaudeSessionState.Exited;
        DangerIcon.Visibility = showDanger ? Visibility.Visible : Visibility.Collapsed;

        var canSend = state == ClaudeSessionState.Idle;
        InputBox.IsEnabled = canSend;
        SendButton.IsEnabled = canSend;
        MicButton.IsEnabled = canSend;
        if (!canSend && _dictation?.IsActive == true)
            _ = _dictation.StopAsync();

        if (canSend)
            FocusInput();

        SessionStateChanged?.Invoke(state);
    }

    private void StartWorkingAnimation()
    {
        _spinnerIndex = 0;
        StatusText.Text = $"{SpinnerFrames[0]} Working...";
        _workingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _workingTimer.Tick += (_, _) =>
        {
            _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
            StatusText.Text = $"{SpinnerFrames[_spinnerIndex]} Working...";
        };
        _workingTimer.Start();
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
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopWorkingAnimation();
        StopButton.IsEnabled = false;
        InputBox.IsEnabled = false;
        SendButton.IsEnabled = false;
        StatusText.Text = "Stopping...";
        StatusText.Foreground = ThemeManager.GetBrush("ChatMutedForeground");

        RemoveWaitingBubble();
        RemoveInactivityWarning();
        FinalizeThinking();
        FinalizeExecution();

        var session = _session;
        _session = null;
        _processor = null; // queued ops are dropped by ApplyOps once _session is null
        if (session != null)
        {
            await session.StopAsync();
            session.Dispose();
        }

        ChatPanel.Visibility = Visibility.Collapsed;
        StartPanel.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
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
        AddMenuItem(menu, "Stop Session", "/stop", hasSession, ExecuteLocalCommand);
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
        if (string.IsNullOrEmpty(text))
            return;

        InputBox.Text = "";

        if (text.StartsWith('/'))
        {
            if (TryHandleLocalCommand(text))
                return;
        }

        if (_session == null || _session.State != ClaudeSessionState.Idle)
            return;

        // Defensive turn-boundary finalize — should already have happened on
        // the previous result, but covers edge cases (manual /compact mid-stream etc.).
        FinalizeThinking();
        FinalizeExecution();
        _processor?.Reset();

        _lastMessageSentTime = DateTime.UtcNow;
        AddUserMessage(text);
        ShowWaitingBubble();
        _session.SendMessage(text);
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
                    FinalizeThinking();
                    FinalizeExecution();
                    _processor?.Reset();
                    _lastMessageSentTime = DateTime.UtcNow;
                    AddUserMessage("/compact");
                    ShowWaitingBubble();
                    _session.SendMessage("/compact");
                }
                break;

            case "/stop":
                Stop_Click(this, new RoutedEventArgs());
                break;

            case "/logs":
                var logPath = Log.LogFilePath;
                if (logPath != null)
                {
                    var folder = System.IO.Path.GetDirectoryName(logPath);
                    if (folder != null && System.IO.Directory.Exists(folder))
                        System.Diagnostics.Process.Start("explorer.exe", folder);
                }
                break;
        }
    }

    // --- Message Collection Helpers ---

    private void AddUserMessage(string text)
    {
        // Sending always snaps to the newest message, even if the user had
        // scrolled up to read history.
        _stickToBottom = true;
        Messages.Add(new UserMessage(Guid.NewGuid(), text));
    }

    private void AddSystemMessage(string text, bool isWarning = false)
    {
        Messages.Add(new SystemMessage(Guid.NewGuid(), text, isWarning));
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
        // Stop streaming VMs' internal throttle timers and unsubscribe so the
        // VMs (and this control) become GC-eligible together.
        if (_streamingThinkingVm != null)
        {
            _streamingThinkingVm.Flush();
            _streamingThinkingVm.PropertyChanged -= OnStreamingTextChanged;
        }
        Messages.Clear();
        _streamingThinkingVm = null;
        _executionVm = null;
        _waitingVm = null;
        _inactivityVm = null;
        _processor?.Reset();
    }

    // --- Content Block / Stream Events ---

    /// <summary>
    /// Sticky-bottom follow during streaming: when a live VM's text changes,
    /// scroll to the new bottom <i>only</i> if the user was already at the
    /// bottom. If they've scrolled up to read history, don't yank them back.
    /// </summary>
    private void OnStreamingTextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StreamingTextMessage.Text)) return;
        // Nudge to the bottom while following. ScrollToEnd only sets a pending
        // offset (no forced layout); once the new text lays out and the extent
        // grows, OnScrollChanged re-pins authoritatively. Cheap no-op when the
        // user has scrolled up, and on background tabs whose extent isn't moving.
        if (_stickToBottom)
            GetScrollViewer()?.ScrollToEnd();
    }

    // Applies the TurnComplete op: the finalizes already ran as preceding ops, so
    // this only surfaces errors and the cost/token summary and updates stats.
    private void ApplyTurnComplete(ClaudeResultMessage result)
    {
        if (result.IsError && result.Errors?.Count > 0)
            AddSystemMessage($"Error: {string.Join("; ", result.Errors)}", isWarning: true);

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

    // --- Finalize / Swap (live VM → immutable record) ---

    private void EnsureExecutionVm()
    {
        if (_executionVm != null) return;
        // Execution output ends the thinking phase — drop the placeholder and
        // finalize any open thinking window so it renders above the execution
        // bubble (and so the next thinking block starts a fresh window).
        RemoveWaitingBubble();
        FinalizeThinking();
        _executionVm = new BuildingExecutionMessage(Guid.NewGuid());
        Messages.Add(_executionVm);
    }

    private void FinalizeThinking()
    {
        if (_streamingThinkingVm == null) return;
        _streamingThinkingVm.Flush();
        _streamingThinkingVm.PropertyChanged -= OnStreamingTextChanged;

        var idx = Messages.IndexOf(_streamingThinkingVm);
        if (idx >= 0)
        {
            // Defensive: a window that somehow ended up with no real content
            // (e.g. only whitespace separators) is dropped rather than left
            // behind as an empty thinking bubble.
            if (string.IsNullOrWhiteSpace(_streamingThinkingVm.Text))
                Messages.RemoveAt(idx);
            else
                Messages[idx] = new ThinkingMessage(
                    _streamingThinkingVm.Id,
                    _streamingThinkingVm.Text,
                    isExpanded: false);
        }
        _streamingThinkingVm = null;
    }

    private void FinalizeExecution()
    {
        if (_executionVm == null) return;
        var idx = Messages.IndexOf(_executionVm);
        if (idx >= 0)
        {
            Messages[idx] = new ExecutionMessage(
                _executionVm.Id,
                _executionVm.Tools.ToList(),
                isExpanded: false);
        }
        _executionVm = null;
    }

    // --- Permission Handling ---

    private void OnPermissionRequested(ClaudePermissionRequest req)
    {
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
                        Cursor = Cursors.Hand,
                        Background = ThemeManager.GetBrush("ChatOptionButtonBackground"),
                        BorderBrush = ThemeManager.GetBrush("ChatOptionButtonBorderBrush"),
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
                        Foreground = ThemeManager.GetBrush("ChatTextForeground")
                    });
                    if (!string.IsNullOrEmpty(desc))
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = desc,
                            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                            FontSize = 10,
                            Foreground = ThemeManager.GetBrush("ChatMutedForeground"),
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
            Log.Error("Failed to parse AskUserQuestion input", ex);
            PermissionToolName.Text = $"Tool: {req.ToolName}";
            PermissionDetail.Text = FormatToolInput(req.Input);
            InputPanel.Visibility = Visibility.Collapsed;
            PermissionPanel.Visibility = Visibility.Visible;
        }
    }

    private void SubmitQuestionAnswer(string answer)
    {
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
        if (e.Key == Key.Enter)
        {
            var text = QuestionCustomInput.Text.Trim();
            if (!string.IsNullOrEmpty(text))
                SubmitQuestionAnswer(text);
        }
    }

    private void PermissionAllow_Click(object sender, RoutedEventArgs e)
    {
        _session?.AllowPermission();
        PermissionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
    }

    private void PermissionDeny_Click(object sender, RoutedEventArgs e)
    {
        _session?.DenyPermission();
        PermissionPanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
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
                Stop_Click(this, new RoutedEventArgs());
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

    private void ThinkingHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ThinkingMessage msg)
        {
            var idx = Messages.IndexOf(msg);
            if (idx >= 0)
                Messages[idx] = new ThinkingMessage(msg.Id, msg.Text, !msg.IsExpanded);
        }
        e.Handled = true;
    }

    private void ExecutionHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ExecutionMessage msg)
        {
            var idx = Messages.IndexOf(msg);
            if (idx >= 0)
                Messages[idx] = new ExecutionMessage(msg.Id, msg.Tools, !msg.IsExpanded);
        }
        e.Handled = true;
    }

    private void BuildingExecutionHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is BuildingExecutionMessage msg)
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

    private static void AttachAssistantDocument(MarkdownScrollViewer viewer, AssistantMessage? msg)
    {
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
        var sv = GetScrollViewer();
        if (sv == null) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

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
