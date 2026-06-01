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
    private string _projectPath = "";

    // The virtualized chat list. Past messages are immutable VM classes;
    // only the live tail (streaming text, building execution, inactivity
    // warning) carries INPC. Past-message containers in the
    // VirtualizingStackPanel are realized only when scrolled into view.
    public ObservableCollection<ChatMessageVm> Messages { get; } = [];

    // Live-tail references — set when the corresponding VM is in the
    // collection, nulled on finalize / removal.
    private StreamingAssistantMessage? _streamingVm;
    private StreamingThinkingMessage? _streamingThinkingVm;
    // Content-block index of the thinking deltas currently feeding
    // _streamingThinkingVm. Lets consecutive thinking blocks (no intervening
    // response/execution) merge into the one window instead of each opening a
    // new one. Reset to -1 whenever the thinking window is finalized.
    private int _lastThinkingBlockIndex = -1;
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

        _session.StateChanged += state => Dispatcher.BeginInvoke(() => OnStateChanged(state));
        _session.Initialized += init => Dispatcher.BeginInvoke(() => OnInitialized(init));
        _session.StreamDelta += delta => Dispatcher.BeginInvoke(() => OnStreamDelta(delta));
        _session.AssistantMessageReceived += msg => Dispatcher.BeginInvoke(() => OnAssistantMessage(msg));
        _session.ContentBlockStarted += evt => Dispatcher.BeginInvoke(() => OnContentBlockStarted(evt));
        _session.ContentBlockStopped += evt => Dispatcher.BeginInvoke(() => OnContentBlockStopped(evt));
        _session.PermissionRequested += req => Dispatcher.BeginInvoke(() => OnPermissionRequested(req));
        _session.ResultReceived += result => Dispatcher.BeginInvoke(() => OnResultReceived(result));
        _session.ErrorOutput += text => Dispatcher.BeginInvoke(() => OnErrorOutput(text));
        _session.ProcessExited += code => Dispatcher.BeginInvoke(() => OnProcessExited(code));
        _session.InactivityTimeout += () => Dispatcher.BeginInvoke(OnInactivityTimeout);
    }

    // --- State Handling ---

    private void OnStateChanged(ClaudeSessionState state)
    {
        StopWorkingAnimation();

        // TEMP perf instrumentation — track how many sessions across all tabs
        // are Working at once, so a slow keystroke can be correlated with
        // background-tab contention. Remove with the rest of the PERF block.
        var nowWorking = state == ClaudeSessionState.Working;
        if (nowWorking != _countedAsWorking)
        {
            _countedAsWorking = nowWorking;
            s_workingSessionCount += nowWorking ? 1 : -1;
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
        FinalizeStreaming();
        FinalizeExecution();

        var session = _session;
        _session = null;
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
        // TEMP perf instrumentation (input-lag investigation) — remove once the
        // chat-list layout cost is diagnosed.
        MeasureKeystrokeLatency();

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
        FinalizeStreaming();
        FinalizeExecution();

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
                    FinalizeStreaming();
                    FinalizeExecution();
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
        if (_streamingVm != null)
        {
            _streamingVm.Flush();
            _streamingVm.PropertyChanged -= OnStreamingTextChanged;
        }
        if (_streamingThinkingVm != null)
        {
            _streamingThinkingVm.Flush();
            _streamingThinkingVm.PropertyChanged -= OnStreamingTextChanged;
        }
        Messages.Clear();
        _streamingVm = null;
        _streamingThinkingVm = null;
        _lastThinkingBlockIndex = -1;
        _executionVm = null;
        _waitingVm = null;
        _inactivityVm = null;
    }

    // --- Content Block / Stream Events ---

    private void OnContentBlockStarted(ClaudeContentBlockEvent evt)
    {
        if (evt.BlockType == "thinking")
        {
            // Keep the single "Thinking..." placeholder visible through the
            // thinking phase. claude-opus-4-7/4-8 send redacted thinking
            // (signature_delta only, no thinking_delta), so the placeholder is
            // the only "working" signal until real content arrives — and it
            // must NOT be duplicated per thinking block. The thinking window
            // itself is created lazily (on the first non-empty thinking_delta)
            // by OnThinkingDelta, so empty/redacted thinking never spawns a
            // window. Consecutive thinking blocks merge into one window there.
            return;
        }

        // A non-thinking block (text / tool_use) ends the thinking phase, so
        // drop the placeholder. Real content (response text via OnStreamDelta,
        // execution via EnsureExecutionVm) takes over from here.
        RemoveWaitingBubble();
    }

    private void OnContentBlockStopped(ClaudeContentBlockEvent evt)
    {
        // Intentionally does NOT finalize the thinking window. A thinking block
        // ending does not mean the thinking phase is over — the model may open
        // another thinking block immediately. We only finalize the thinking
        // window when genuinely different content arrives (response text,
        // execution) or the turn ends (OnResultReceived). This is what lets
        // back-to-back thinking blocks share one window instead of stacking up.
    }

    private void OnStreamDelta(ClaudeStreamDelta delta)
    {
        RemoveInactivityWarning();

        if (delta.DeltaType == "thinking_delta")
        {
            OnThinkingDelta(delta);
            return;
        }

        // Real response text — the thinking phase is over. Drop the placeholder
        // and finalize any open thinking window so it renders as a separate
        // (collapsible) message above the response.
        RemoveWaitingBubble();
        if (_streamingVm == null && _streamingThinkingVm != null)
            FinalizeThinking();

        if (_streamingVm == null)
        {
            _streamingVm = new StreamingAssistantMessage(Guid.NewGuid());
            _streamingVm.PropertyChanged += OnStreamingTextChanged;
            Messages.Add(_streamingVm);
        }

        // Coalesced through a ~30 fps throttle inside the VM so a fast model
        // stream doesn't flood the binding system.
        _streamingVm.AppendText(delta.Text);
    }

    private void OnThinkingDelta(ClaudeStreamDelta delta)
    {
        // Redacted thinking emits empty/whitespace thinking_deltas with no real
        // content. Don't open a thinking window for those — keep the single
        // "Thinking..." placeholder instead. This is what stops empty thinking
        // blocks from stacking up as blank windows.
        if (_streamingThinkingVm == null && string.IsNullOrWhiteSpace(delta.Text))
            return;

        if (_streamingThinkingVm == null)
        {
            // First real thinking content — replace the placeholder with a live
            // thinking window.
            RemoveWaitingBubble();
            _streamingThinkingVm = new StreamingThinkingMessage(Guid.NewGuid());
            _streamingThinkingVm.PropertyChanged += OnStreamingTextChanged;
            Messages.Add(_streamingThinkingVm);
            _lastThinkingBlockIndex = delta.ContentBlockIndex;
        }
        else if (delta.ContentBlockIndex != _lastThinkingBlockIndex)
        {
            // A new thinking block opened with no intervening response/execution
            // — merge it into the existing window (separated for readability)
            // rather than opening a second one.
            _streamingThinkingVm.AppendText("\n\n");
            _lastThinkingBlockIndex = delta.ContentBlockIndex;
        }

        _streamingThinkingVm.AppendText(delta.Text);
    }

    /// <summary>
    /// Sticky-bottom follow during streaming: when a live VM's text changes,
    /// scroll to the new bottom <i>only</i> if the user was already at the
    /// bottom. If they've scrolled up to read history, don't yank them back.
    /// </summary>
    private void OnStreamingTextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StreamingTextMessage.Text)) return;
        var sv = GetScrollViewer();
        if (sv == null) return;
        var atBottom = sv.ScrollableHeight == 0 || sv.VerticalOffset >= sv.ScrollableHeight - 50;
        if (atBottom)
            Dispatcher.BeginInvoke(() => sv.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnAssistantMessage(ClaudeAssistantMessage msg)
    {
        // Apply the authoritative full text to the streaming VM.
        var fullText = string.Join("", msg.Content
            .Where(c => c.Type == "text" && c.Text != null)
            .Select(c => c.Text));

        if (_streamingVm != null && !string.IsNullOrEmpty(fullText))
            _streamingVm.Text = fullText; // setter clears throttle buffer + timer

        var toolBlocks = msg.Content.Where(c => c.Type == "tool_use").ToList();

        if (toolBlocks.Count > 0)
        {
            // This assistant message calls tools, so any text it produced is
            // intermediate commentary ("let me check…", "now I'll…"), not the
            // final answer. Fold that text into the collapsed thinking window
            // so only executions and the final answer stay expanded — keeping
            // the transcript short and cheap to render.
            FoldStreamingTextIntoThinking();

            // Collapse the commentary immediately. (EnsureExecutionVm only
            // finalizes thinking when it creates the turn's first execution
            // bubble; tools after that reuse the existing bubble, so we can't
            // rely on it to collapse commentary before later tool calls.)
            FinalizeThinking();
            EnsureExecutionVm();
            foreach (var block in toolBlocks)
            {
                var inputStr = "";
                if (block.Input is JsonElement input)
                {
                    inputStr = input.ValueKind == JsonValueKind.Object
                        ? FormatToolInput(input)
                        : input.ToString();
                }
                _executionVm!.Tools.Add(new ToolEntry(block.Name ?? "(unnamed)", inputStr));
            }
        }
        else
        {
            // No tool calls — this text is (part of) the final answer. Collapse
            // any open thinking and keep the response visible as an immutable
            // AssistantMessage (gets the markdown toggle if multi-line).
            FinalizeThinking();
            FinalizeStreaming();
        }
    }

    /// <summary>
    /// Reclassifies the current streaming assistant text as intermediate
    /// commentary: drops its visible bubble and appends the text to the
    /// thinking window (which is collapsed on finalize). No-op if there's no
    /// streaming text or it's empty.
    /// </summary>
    private void FoldStreamingTextIntoThinking()
    {
        if (_streamingVm == null) return;

        _streamingVm.Flush();
        _streamingVm.PropertyChanged -= OnStreamingTextChanged;
        var text = _streamingVm.Text;
        var idx = Messages.IndexOf(_streamingVm);
        if (idx >= 0) Messages.RemoveAt(idx);
        _streamingVm = null;

        if (string.IsNullOrWhiteSpace(text)) return;

        if (_streamingThinkingVm == null)
        {
            _streamingThinkingVm = new StreamingThinkingMessage(Guid.NewGuid());
            _streamingThinkingVm.PropertyChanged += OnStreamingTextChanged;
            Messages.Add(_streamingThinkingVm);
        }
        else if (!string.IsNullOrEmpty(_streamingThinkingVm.Text))
        {
            // Separate commentary from any thinking text already in the window.
            _streamingThinkingVm.AppendText("\n\n");
        }
        _streamingThinkingVm.AppendText(text);
    }

    private void OnResultReceived(ClaudeResultMessage result)
    {
        RemoveWaitingBubble();
        RemoveInactivityWarning();
        FinalizeThinking();
        FinalizeStreaming();
        FinalizeExecution();

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

    private void FinalizeStreaming()
    {
        if (_streamingVm == null) return;
        // Drain any buffered deltas the throttle timer hasn't flushed yet,
        // then drop the PropertyChanged subscription so the VM is GC-eligible.
        _streamingVm.Flush();
        _streamingVm.PropertyChanged -= OnStreamingTextChanged;

        var idx = Messages.IndexOf(_streamingVm);
        if (idx >= 0)
        {
            var text = _streamingVm.Text;
            var hasNl = text.Contains('\n');

            // Build the rendered FlowDocument exactly once, here at finalize.
            // The same document instance is re-attached to whichever container
            // realizes this message; scroll-past-and-back doesn't re-parse.
            // Single-line messages render as plain text — markdown rendering
            // would be no-op and the toggle is useless, so skip the build.
            FlowDocument? document = null;
            if (hasNl)
            {
                document = MarkdownHelper.BuildDocument(
                    text,
                    markdownStyle: (System.Windows.Style)FindResource("ChatMarkdownStyle"),
                    projectPath: _projectPath,
                    onFileLinkClicked: p => FileReferenceClicked?.Invoke(p));
            }

            Messages[idx] = new AssistantMessage(
                id: _streamingVm.Id,
                text: text,
                isMarkdownView: hasNl,
                hasMarkdownToggle: hasNl,
                markdown: document);
        }
        _streamingVm = null;
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
        _lastThinkingBlockIndex = -1;
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
                // Reuse the same FlowDocument — toggling the view doesn't
                // re-parse markdown.
                Messages[idx] = new AssistantMessage(
                    msg.Id, msg.Text, !msg.IsMarkdownView, msg.HasMarkdownToggle, msg.Markdown);
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
        if (msg?.Markdown == null)
        {
            viewer.Document = null;
            return;
        }
        if (ReferenceEquals(viewer.Document, msg.Markdown))
            return;

        // FlowDocument has at most one logical parent. If our cached document
        // is currently attached to another (recycled) viewer, detach it first.
        if (msg.Markdown.Parent is System.Windows.Controls.FlowDocumentScrollViewer prev
            && !ReferenceEquals(prev, viewer))
        {
            prev.Document = null;
        }

        viewer.Document = msg.Markdown;
    }

    // --- Scroll + Wheel ---

    private ScrollViewer? GetScrollViewer()
    {
        if (_scrollViewer != null) return _scrollViewer;
        _scrollViewer = FindVisualChild<ScrollViewer>(MessageList);
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

    // --- TEMP perf instrumentation (input-lag investigation) ---
    // Times how long the UI thread stays busy from a keystroke in the prompt
    // box until the resulting layout/render cycle completes, and reports how
    // many heavy message visuals (MdXaml viewers, AvalonEdit editors) are
    // realized at that moment. Only slow frames are logged, to avoid spam.
    // Remove this block (and its call in InputBox_TextChanged) once the chat
    // layout cost is understood.
    private readonly System.Diagnostics.Stopwatch _keystrokeSw = new();
    private bool _keystrokeSamplePending;
    // Count of sessions (across all tabs) currently in the Working state.
    private static int s_workingSessionCount;
    private bool _countedAsWorking;

    private void MeasureKeystrokeLatency()
    {
        if (_keystrokeSamplePending) return; // one sample in flight at a time
        _keystrokeSamplePending = true;
        _keystrokeSw.Restart();

        // Loaded priority runs after the Render-priority layout pass for this
        // dispatcher cycle, so the elapsed time covers the layout work the
        // keystroke triggered.
        Dispatcher.BeginInvoke(() =>
        {
            _keystrokeSamplePending = false;
            var ms = _keystrokeSw.Elapsed.TotalMilliseconds;
            if (ms < 20) return; // ignore snappy keystrokes

            var (viewers, editors) = CountRealizedHeavyVisuals();
            Log.Warn($"PERF keystroke->layout {ms:F0}ms | realized mdxaml={viewers}, " +
                     $"avalonedit={editors}, total messages={Messages.Count}, " +
                     $"input len={InputBox.Text.Length}, working sessions={s_workingSessionCount}");
        }, DispatcherPriority.Loaded);
    }

    private (int viewers, int editors) CountRealizedHeavyVisuals()
    {
        int viewers = 0, editors = 0;
        foreach (var d in EnumerateVisualTree(MessageList))
        {
            // Match by type name so we don't take a compile dependency on the
            // MdXaml / AvalonEdit assemblies just for instrumentation.
            switch (d.GetType().Name)
            {
                case "MarkdownScrollViewer": viewers++; break;
                case "TextEditor": editors++; break;
            }
        }
        return (viewers, editors);
    }

    private static IEnumerable<DependencyObject> EnumerateVisualTree(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in EnumerateVisualTree(child))
                yield return d;
        }
    }

    private void ScrollToBottom()
    {
        var sv = GetScrollViewer();
        if (sv != null)
            sv.ScrollToEnd();
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

        var sv = GetScrollViewer();
        if (sv == null)
        {
            // Template not applied yet — schedule scroll after first render.
            Dispatcher.BeginInvoke(ScrollToBottom, DispatcherPriority.Loaded);
            return;
        }

        var atBottom = sv.ScrollableHeight == 0
            || sv.VerticalOffset >= sv.ScrollableHeight - 50;
        if (atBottom)
        {
            // Defer until after the new container is realized & measured.
            Dispatcher.BeginInvoke(() => sv.ScrollToEnd(), DispatcherPriority.Background);
        }
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
