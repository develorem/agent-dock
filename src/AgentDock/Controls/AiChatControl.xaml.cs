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

    // Tracks the currently streaming message block so we can append deltas
    private TextBox? _streamingBlock;
    private string _streamingText = "";

    // Thinking bubble tracking
    private TextBox? _thinkingBlock;
    private string _thinkingText = "";
    private Border? _thinkingBubble;
    private bool _thinkingExpanded = true; // expanded while streaming, collapsed when done

    // Animated working indicator
    private DispatcherTimer? _workingTimer;
    private int _spinnerIndex;
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // In-chat thinking placeholder (shown when Working, no deltas yet)
    private Border? _waitingBubble;

    // Execution bubble — groups tool-use blocks under a collapsible heading
    private TextBox? _executionContentBlock;
    private string _executionFullText = "";
    private Border? _executionBubble;

    // Pending AskUserQuestion — tracks question text for response
    private string? _pendingQuestionText;

    // Inactivity warning bubble (removable when activity resumes)
    private Border? _inactivityWarning;
    private DispatcherTimer? _inactivityElapsedTimer;
    private TextBlock? _inactivityElapsedText;
    private DateTime _lastMessageSentTime;

    /// <summary>
    /// Raised when session state changes (for toolbar icon updates).
    /// </summary>
    public event Action<ClaudeSessionState>? SessionStateChanged;

    /// <summary>
    /// Raised when cumulative session stats change (cost, tokens).
    /// </summary>
    public event Action<SessionStats>? SessionStatsChanged;

    /// <summary>
    /// Cumulative stats for the current session.
    /// </summary>
    public SessionStats Stats { get; } = new();

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
        Log.Info("AiChatControl: InitializeComponent complete");
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

    public void Shutdown()
    {
        _session?.Dispose();
        _session = null;
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
        // Stop the working animation for any state change
        StopWorkingAnimation();

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

        // Show danger icon when session is active in dangerous mode
        var showDanger = _session?.IsDangerousMode == true
            && state != ClaudeSessionState.NotStarted
            && state != ClaudeSessionState.Exited;
        DangerIcon.Visibility = showDanger ? Visibility.Visible : Visibility.Collapsed;

        var canSend = state == ClaudeSessionState.Idle;
        InputBox.IsEnabled = canSend;
        SendButton.IsEnabled = canSend;

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
    }

    // --- Sending Messages ---

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrentMessage();
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        // Immediate UI feedback — disable controls, show stopping state
        StopWorkingAnimation();
        StopButton.IsEnabled = false;
        InputBox.IsEnabled = false;
        SendButton.IsEnabled = false;
        StatusText.Text = "Stopping...";
        StatusText.Foreground = ThemeManager.GetBrush("ChatMutedForeground");

        RemoveWaitingBubble();
        RemoveInactivityWarning();
        FinalizeThinkingBlock();
        FinalizeStreamingBlock();

        // Await the actual process kill (runs off UI thread)
        var session = _session;
        _session = null;
        if (session != null)
        {
            await session.StopAsync();
            session.Dispose();
        }

        // Now reset to start panel
        ChatPanel.Visibility = Visibility.Collapsed;
        StartPanel.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        MessageList.Children.Clear();
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

        // Only show popup while typing the command word (starts with /, no whitespace yet)
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
        // Only complete if the click landed on a ListBoxItem (not the empty area or scrollbar)
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
            Style = null, // use default WPF style
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

        // Check for slash commands
        if (text.StartsWith('/'))
        {
            if (TryHandleLocalCommand(text))
                return;

            // Not a local command — pass through to Claude Code as-is
        }

        if (_session == null || _session.State != ClaudeSessionState.Idle)
            return;

        // Collapse previous streaming block if still open
        FinalizeStreamingBlock();
        _executionContentBlock = null;
        _executionFullText = "";
        _executionBubble = null;

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
                return false; // not a local command — pass through
        }
    }

    private void ExecuteLocalCommand(string command)
    {
        switch (command)
        {
            case "/clear":
                if (_session != null && _session.State == ClaudeSessionState.Idle)
                {
                    MessageList.Children.Clear();
                    AddSystemMessage("Chat cleared.");
                }
                break;

            case "/compact":
                // Compact is a Claude Code command — send it through
                if (_session != null && _session.State == ClaudeSessionState.Idle)
                {
                    FinalizeStreamingBlock();
                    _executionContentBlock = null;
                    _executionFullText = "";
                    _executionBubble = null;
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

    // --- Waiting Bubble (shown until first delta arrives) ---

    private void ShowWaitingBubble()
    {
        var tb = new TextBlock
        {
            Text = "Thinking...",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 11,
            Foreground = ThemeManager.GetBrush("ChatMutedForeground"),
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap
        };

        _waitingBubble = new Border
        {
            Background = ThemeManager.GetBrush("ChatThinkingBackground"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 2, 40, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = ThemeManager.GetBrush("ChatThinkingBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = tb
        };

        MessageList.Children.Add(_waitingBubble);
        ScrollToBottom();
    }

    private void RemoveWaitingBubble()
    {
        if (_waitingBubble != null)
        {
            MessageList.Children.Remove(_waitingBubble);
            _waitingBubble = null;
        }
    }

    // --- Content Block Events ---

    private void OnContentBlockStarted(ClaudeContentBlockEvent evt)
    {
        RemoveWaitingBubble();

        if (evt.BlockType == "thinking")
        {
            // Start a new thinking bubble
            _thinkingText = "";
            _thinkingBlock = CreateSelectableText(11,
                ThemeManager.GetBrush("ChatMutedForeground"),
                FontStyles.Italic);
            _thinkingBubble = CreateThinkingBubble(_thinkingBlock);
            _thinkingExpanded = true;
            MessageList.Children.Add(_thinkingBubble);
        }
    }

    private void OnContentBlockStopped(ClaudeContentBlockEvent evt)
    {
        // When a thinking block stops, collapse it
        if (_thinkingBlock != null)
        {
            FinalizeThinkingBlock();
        }
    }

    // --- Streaming ---

    private void OnStreamDelta(ClaudeStreamDelta delta)
    {
        RemoveWaitingBubble();
        RemoveInactivityWarning();

        if (delta.DeltaType == "thinking_delta")
        {
            OnThinkingDelta(delta);
            return;
        }

        // First text_delta — collapse any open thinking bubble
        if (_streamingBlock == null && _thinkingBlock != null)
        {
            FinalizeThinkingBlock();
        }

        if (_streamingBlock == null)
        {
            // Create a new streaming message block
            _streamingText = "";
            _streamingBlock = CreateStreamingBlock();
            var container = CreateAssistantBubble(_streamingBlock);
            MessageList.Children.Add(container);
        }

        _streamingText += delta.Text;
        _streamingBlock.Text = _streamingText;
        ScrollToBottom();
    }

    private void OnThinkingDelta(ClaudeStreamDelta delta)
    {
        if (_thinkingBlock == null)
        {
            // No content_block_start arrived — create thinking bubble on first delta
            _thinkingText = "";
            _thinkingBlock = CreateSelectableText(11,
                ThemeManager.GetBrush("ChatMutedForeground"),
                FontStyles.Italic);
            _thinkingBubble = CreateThinkingBubble(_thinkingBlock);
            _thinkingExpanded = true;
            MessageList.Children.Add(_thinkingBubble);
        }

        _thinkingText += delta.Text;
        if (_thinkingExpanded)
        {
            _thinkingBlock.Text = _thinkingText;
        }
        ScrollToBottom();
    }

    private void OnAssistantMessage(ClaudeAssistantMessage msg)
    {
        // If we have streaming text, the stream deltas already rendered it.
        // The assistant message is the "complete" version.
        // We finalize the streaming block with the full text.
        var fullText = string.Join("", msg.Content
            .Where(c => c.Type == "text" && c.Text != null)
            .Select(c => c.Text));

        if (_streamingBlock != null && !string.IsNullOrEmpty(fullText))
        {
            _streamingBlock.Text = fullText;
            _streamingText = fullText;
        }

        // Group tool-use blocks into collapsible "Execution" bubble
        var toolBlocks = msg.Content.Where(c => c.Type == "tool_use").ToList();
        if (toolBlocks.Count > 0)
        {
            foreach (var block in toolBlocks)
            {
                var toolText = $"[Tool: {block.Name}]";
                if (block.Input is JsonElement input)
                {
                    var inputStr = input.ValueKind == JsonValueKind.Object
                        ? FormatToolInput(input)
                        : input.ToString();
                    toolText += $"\n{inputStr}";
                }

                AppendToExecutionBubble(toolText);
            }
        }

        FinalizeStreamingBlock();
    }

    private void OnResultReceived(ClaudeResultMessage result)
    {
        RemoveWaitingBubble();
        RemoveInactivityWarning();
        FinalizeThinkingBlock();
        FinalizeStreamingBlock();

        if (result.IsError && result.Errors?.Count > 0)
        {
            AddSystemMessage($"Error: {string.Join("; ", result.Errors)}", isWarning: true);
        }

        // Update session stats (values from Claude Code are cumulative)
        var (costDelta, tokenDelta) = Stats.Update(result);

        // Build per-interaction summary line using deltas
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

    private void FinalizeThinkingBlock()
    {
        if (_thinkingBlock == null || _thinkingBubble == null)
            return;

        var contentBlock = _thinkingBlock;
        var fullText = _thinkingText;
        var expanded = false;

        // Collapse content, add chevron to header
        _thinkingExpanded = false;
        contentBlock.Visibility = Visibility.Collapsed;

        var panel = (StackPanel)_thinkingBubble.Child;
        var label = (UIElement)panel.Children[0];
        panel.Children.RemoveAt(0);

        var chevron = CreateCollapseChevron(false);
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand };
        headerRow.Children.Add(chevron);
        headerRow.Children.Add(label);
        panel.Children.Insert(0, headerRow);

        headerRow.MouseLeftButtonUp += (_, e) =>
        {
            expanded = !expanded;
            chevron.Text = expanded ? "▼" : "▶";
            contentBlock.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        };

        _thinkingBlock = null;
        _thinkingText = "";
        _thinkingBubble = null;
    }

    private void FinalizeStreamingBlock()
    {
        if (_streamingBlock == null)
            return;

        // Find the parent container and add markdown toggle + collapsible support
        var parent = _streamingBlock.Parent as StackPanel;
        if (parent?.Parent is Border border)
        {
            // Add markdown preview toggle for multi-line assistant messages
            if (_streamingText.Contains('\n'))
            {
                AddMarkdownToggle(border, parent, _streamingBlock, _streamingText);
            }
        }

        _streamingBlock = null;
        _streamingText = "";
    }

    // --- Permission Handling ---

    private void OnPermissionRequested(ClaudePermissionRequest req)
    {
        // AskUserQuestion gets a specialized question panel instead of generic Allow/Deny
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
                // Fallback to regular permission panel
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
            // Fallback: show as regular permission
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
        if (_session == null || _inactivityWarning != null)
            return; // Already showing a warning or no session

        var elapsed = DateTime.UtcNow - _lastMessageSentTime;
        var warningText = new TextBlock
        {
            Text = "Claude hasn't responded for a while — it may be stuck.",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 11,
            Foreground = ThemeManager.GetBrush("ChatStatusWarningForeground"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };

        _inactivityElapsedText = new TextBlock
        {
            Text = FormatElapsed(elapsed),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = ThemeManager.GetBrush("ChatMutedForeground"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        // Start a timer to keep the elapsed display up to date
        _inactivityElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _inactivityElapsedTimer.Tick += (_, _) =>
        {
            if (_inactivityElapsedText != null)
                _inactivityElapsedText.Text = FormatElapsed(DateTime.UtcNow - _lastMessageSentTime);
        };
        _inactivityElapsedTimer.Start();

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal };

        var waitBtn = new Button
        {
            Content = "Wait longer",
            Cursor = Cursors.Hand,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 11,
            Background = ThemeManager.GetBrush("ChatButtonBackground"),
            Foreground = ThemeManager.GetBrush("ChatButtonForeground"),
            BorderBrush = ThemeManager.GetBrush("ChatButtonBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 0)
        };
        waitBtn.Click += (_, _) =>
        {
            RemoveInactivityWarning();
            _session?.ExtendInactivityTimer();
        };

        var killBtn = new Button
        {
            Content = "Kill process",
            Cursor = Cursors.Hand,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 11,
            Background = ThemeManager.GetBrush("ChatKillButtonBackground"),
            Foreground = ThemeManager.GetBrush("ChatDangerForeground"),
            BorderBrush = ThemeManager.GetBrush("ChatKillButtonBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        killBtn.Click += (_, _) =>
        {
            RemoveInactivityWarning();
            Stop_Click(this, new RoutedEventArgs());
        };

        buttonPanel.Children.Add(waitBtn);
        buttonPanel.Children.Add(killBtn);

        var panel = new StackPanel { Margin = new Thickness(0) };
        panel.Children.Add(warningText);
        panel.Children.Add(_inactivityElapsedText);
        panel.Children.Add(buttonPanel);

        _inactivityWarning = new Border
        {
            Background = ThemeManager.GetBrush("ChatWarningBackground"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(8, 4, 8, 4),
            BorderBrush = ThemeManager.GetBrush("ChatWarningBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = panel
        };

        MessageList.Children.Add(_inactivityWarning);
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
        _inactivityElapsedText = null;
        if (_inactivityWarning != null)
        {
            MessageList.Children.Remove(_inactivityWarning);
            _inactivityWarning = null;
        }
    }

    // --- Error / Exit ---

    private void OnErrorOutput(string text)
    {
        // Only show meaningful errors, not debug noise
        if (text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            AddSystemMessage(text, isWarning: true);
        }
    }

    private void OnProcessExited(int exitCode)
    {
        if (exitCode != 0)
        {
            AddSystemMessage($"Claude process exited with code {exitCode}", isWarning: true);
        }
        else
        {
            AddSystemMessage("Session ended");
        }
    }

    // --- Message Rendering ---

    private void AddUserMessage(string text)
    {
        var bubble = new Border
        {
            Background = ThemeManager.GetBrush("ChatUserBubbleBackground"),
            CornerRadius = new CornerRadius(8, 8, 2, 8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(40, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var panel = new StackPanel();
        var label = new TextBlock
        {
            Text = "You",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = ThemeManager.GetBrush("ChatUserLabelForeground"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        var content = CreateSelectableText(12,
            ThemeManager.GetBrush("ChatTextForeground"));
        content.Text = text;
        panel.Children.Add(label);
        panel.Children.Add(content);
        bubble.Child = panel;

        MessageList.Children.Add(bubble);
        ScrollToBottom();
    }

    private Border CreateAssistantBubble(TextBox contentBlock)
    {
        var bubble = new Border
        {
            Background = ThemeManager.GetBrush("ChatAssistantBubbleBackground"),
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(8, 4, 40, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var panel = new StackPanel();
        var label = new TextBlock
        {
            Text = "Claude",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = ThemeManager.GetBrush("ChatAssistantLabelForeground"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        panel.Children.Add(label);
        panel.Children.Add(contentBlock);
        bubble.Child = panel;

        return bubble;
    }

    private static TextBox CreateStreamingBlock()
    {
        return CreateSelectableText(12,
            ThemeManager.GetBrush("ChatTextForeground"));
    }

    private static Border CreateThinkingBubble(TextBox contentBlock)
    {
        var bubble = new Border
        {
            Background = ThemeManager.GetBrush("ChatThinkingBackground"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 2, 40, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = ThemeManager.GetBrush("ChatThinkingBorderBrush"),
            BorderThickness = new Thickness(1)
        };

        var panel = new StackPanel();
        var label = new TextBlock
        {
            Text = "Thinking",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = ThemeManager.GetBrush("ChatSubtleForeground"),
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 0, 0, 2)
        };
        panel.Children.Add(label);
        panel.Children.Add(contentBlock);
        bubble.Child = panel;

        return bubble;
    }

    private void AppendToExecutionBubble(string toolText)
    {
        if (_executionBubble == null)
        {
            _executionContentBlock = CreateSelectableText(10,
                ThemeManager.GetBrush("ChatExecutionForeground"),
                FontStyles.Normal);
            _executionBubble = CreateExecutionBubble(_executionContentBlock);
            MessageList.Children.Add(_executionBubble);
            _executionFullText = "";
        }

        _executionFullText += (string.IsNullOrEmpty(_executionFullText) ? "" : "\n\n") + toolText;
        _executionContentBlock!.Text = _executionFullText;
        ScrollToBottom();
    }

    private static Border CreateExecutionBubble(TextBox contentBlock)
    {
        var bubble = new Border
        {
            Background = ThemeManager.GetBrush("ChatExecutionBackground"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 2, 40, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = ThemeManager.GetBrush("ChatExecutionBorderBrush"),
            BorderThickness = new Thickness(1)
        };

        var panel = new StackPanel();

        var label = new TextBlock
        {
            Text = "Execution",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = ThemeManager.GetBrush("ChatExecutionForeground"),
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 0, 0, 2)
        };

        var chevron = CreateCollapseChevron(false);
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand };
        headerRow.Children.Add(chevron);
        headerRow.Children.Add(label);

        contentBlock.Visibility = Visibility.Collapsed;
        var expanded = false;

        headerRow.MouseLeftButtonUp += (_, e) =>
        {
            expanded = !expanded;
            chevron.Text = expanded ? "▼" : "▶";
            contentBlock.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        };

        panel.Children.Add(headerRow);
        panel.Children.Add(contentBlock);
        bubble.Child = panel;

        return bubble;
    }

    private void AddSystemMessage(string text, bool isWarning = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = isWarning
                ? ThemeManager.GetBrush("ChatDangerForeground")
                : ThemeManager.GetBrush("ChatSubtleForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 6, 8, 6)
        };

        MessageList.Children.Add(tb);
        ScrollToBottom();
    }

    // --- Markdown Toggle + Collapsible Messages ---

    private void AddMarkdownToggle(Border bubble, StackPanel panel, TextBox contentBlock, string fullText)
    {
        // Create the markdown rendered view
        var markdownViewer = new MarkdownScrollViewer
        {
            Background = Brushes.Transparent,
            Foreground = ThemeManager.GetBrush("ChatTextForeground"),
            MarkdownStyle = (Style)FindResource("ChatMarkdownStyle"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0)
        };
        MarkdownHelper.RenderTo(markdownViewer, fullText);

        var isRendered = true;
        contentBlock.Visibility = Visibility.Collapsed;
        panel.Children.Add(markdownViewer);

        // Toggle button (top-right of bubble)
        var toggleIcon = new TextBlock
        {
            Text = "\uE943", // Code icon (showing rendered, click to see source)
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = ThemeManager.GetBrush("ChatMutedForeground")
        };

        var toggleButton = new Button
        {
            Content = toggleIcon,
            Background = Brushes.Transparent,
            BorderBrush = ThemeManager.GetBrush("ChatThinkingBorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Padding = new Thickness(4, 2, 4, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Switch to source view",
            Opacity = 0.7
        };

        // Remove default button chrome
        var btnTemplate = new ControlTemplate(typeof(Button));
        var bdFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        bdFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bdFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        bdFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        bdFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        bdFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bdFactory.AppendChild(cp);
        btnTemplate.VisualTree = bdFactory;
        toggleButton.Template = btnTemplate;

        toggleButton.Click += (_, _) =>
        {
            if (isRendered)
            {
                markdownViewer.Visibility = Visibility.Collapsed;
                contentBlock.Visibility = Visibility.Visible;
                toggleIcon.Text = "\uE890"; // Preview/eye icon
                toggleButton.ToolTip = "Switch to rendered view";
                isRendered = false;
            }
            else
            {
                contentBlock.Visibility = Visibility.Collapsed;
                markdownViewer.Visibility = Visibility.Visible;
                toggleIcon.Text = "\uE943"; // Code icon
                toggleButton.ToolTip = "Switch to source view";
                isRendered = true;
            }
        };

        // Insert toggle button at the top of the panel (after label)
        panel.Children.Insert(1, toggleButton);
    }

    private void MakeCollapsible(Border bubble, StackPanel panel, TextBox contentBlock, string fullText)
    {
        var lines = fullText.Split('\n');
        if (lines.Length <= 3)
            return; // Not worth collapsing

        var firstLine = lines[0].Length > 80 ? lines[0][..80] + "..." : lines[0];
        var collapsed = $"{firstLine}\n  [{lines.Length} lines]";

        // Add chevron to the header row next to the label
        var isExpanded = true;
        var label = (UIElement)panel.Children[0];
        panel.Children.RemoveAt(0);

        var chevron = CreateCollapseChevron(true);
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand };
        headerRow.Children.Add(chevron);
        headerRow.Children.Add(label);
        panel.Children.Insert(0, headerRow);

        headerRow.MouseLeftButtonUp += (_, e) =>
        {
            isExpanded = !isExpanded;
            chevron.Text = isExpanded ? "▼" : "▶";
            contentBlock.Text = isExpanded ? fullText : collapsed;
            e.Handled = true;
        };
    }

    // --- Helpers ---

    private static TextBlock CreateCollapseChevron(bool expanded)
    {
        return new TextBlock
        {
            Text = expanded ? "▼" : "▶",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = ThemeManager.GetBrush("ChatMutedForeground"),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static TextBox CreateSelectableText(double fontSize, Brush foreground,
        FontStyle? fontStyle = null)
    {
        var tb = new TextBox
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = fontSize,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        if (fontStyle.HasValue)
            tb.FontStyle = fontStyle.Value;
        return tb;
    }

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

    private void MessageScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Prevent child controls (TextBox, MarkdownScrollViewer) from stealing wheel events
        MessageScroller.ScrollToVerticalOffset(MessageScroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void ScrollToBottom()
    {
        MessageScroller.ScrollToEnd();
    }
}
