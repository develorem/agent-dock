using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentDock.Models;
using AgentDock.Services;

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

    // Finalized thinking bubble — kept so tool-use blocks can be appended
    private TextBox? _finalizedThinkingContentBlock;
    private string _finalizedThinkingFullText = "";

    /// <summary>
    /// Raised when session state changes (for toolbar icon updates).
    /// </summary>
    public event Action<ClaudeSessionState>? SessionStateChanged;

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
                ClaudeSessionState.Idle => _session?.IsDangerousMode == true ? "Idle (dangerous mode)" : "Idle",
                ClaudeSessionState.WaitingForPermission => "Waiting for permission...",
                ClaudeSessionState.Exited => "Session ended",
                ClaudeSessionState.Error => "Error",
                _ => ""
            };
        }

        StatusText.Foreground = state switch
        {
            ClaudeSessionState.Working => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
            ClaudeSessionState.WaitingForPermission => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)),
            ClaudeSessionState.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
        };

        var canSend = state == ClaudeSessionState.Idle;
        InputBox.IsEnabled = canSend;
        SendButton.IsEnabled = canSend;

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
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        RemoveWaitingBubble();
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

    private void SendCurrentMessage()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _session == null || _session.State != ClaudeSessionState.Idle)
            return;

        InputBox.Text = "";

        // Collapse previous streaming block if still open
        FinalizeStreamingBlock();
        _finalizedThinkingContentBlock = null;
        _finalizedThinkingFullText = "";

        AddUserMessage(text);
        ShowWaitingBubble();
        _session.SendMessage(text);
    }

    // --- Waiting Bubble (shown until first delta arrives) ---

    private void ShowWaitingBubble()
    {
        var tb = new TextBlock
        {
            Text = "Thinking...",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap
        };

        _waitingBubble = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 2, 40, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
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
                new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
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
                new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
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

        // Roll tool-use blocks into the thinking bubble (collapsible together)
        var toolBlocks = msg.Content.Where(c => c.Type == "tool_use").ToList();
        if (toolBlocks.Count > 0)
        {
            foreach (var block in toolBlocks)
            {
                var toolText = $"\n\n[Tool: {block.Name}]";
                if (block.Input is JsonElement input)
                {
                    var inputStr = input.ValueKind == JsonValueKind.Object
                        ? FormatToolInput(input)
                        : input.ToString();
                    toolText += $"\n{inputStr}";
                }

                if (_finalizedThinkingContentBlock != null)
                {
                    _finalizedThinkingFullText += toolText;
                }
                else
                {
                    // No thinking bubble — show tool as separate message
                    AddToolMessage(toolText.TrimStart('\n'));
                }
            }
        }

        FinalizeStreamingBlock();
    }

    private void OnResultReceived(ClaudeResultMessage result)
    {
        RemoveWaitingBubble();
        FinalizeThinkingBlock();
        FinalizeStreamingBlock();

        if (result.IsError && result.Errors?.Count > 0)
        {
            AddSystemMessage($"Error: {string.Join("; ", result.Errors)}", isWarning: true);
        }

        if (result.TotalCostUsd.HasValue)
        {
            var costText = $"Cost: ${result.TotalCostUsd:F4}";
            if (result.DurationMs.HasValue)
                costText += $" | {result.DurationMs / 1000.0:F1}s";
            AddSystemMessage(costText);
        }
    }

    private void FinalizeThinkingBlock()
    {
        if (_thinkingBlock == null || _thinkingBubble == null)
            return;

        // Save references so tool-use blocks can be appended later
        _finalizedThinkingFullText = _thinkingText;
        _finalizedThinkingContentBlock = _thinkingBlock;

        var contentBlock = _thinkingBlock;
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
            if (expanded)
            {
                // Read from field so appended tool info is included
                contentBlock.Text = _finalizedThinkingFullText;
                contentBlock.Visibility = Visibility.Visible;
            }
            else
            {
                contentBlock.Visibility = Visibility.Collapsed;
            }
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

        // Find the parent container and make it collapsible if long
        var parent = _streamingBlock.Parent as StackPanel;
        if (parent?.Parent is Border border && _streamingText.Contains('\n'))
        {
            MakeCollapsible(border, parent, _streamingBlock, _streamingText);
        }

        _streamingBlock = null;
        _streamingText = "";
    }

    // --- Permission Handling ---

    private void OnPermissionRequested(ClaudePermissionRequest req)
    {
        PermissionToolName.Text = $"Tool: {req.ToolName}";

        var detail = req.Input.ValueKind == JsonValueKind.Object
            ? FormatToolInput(req.Input)
            : req.Input.ToString();
        PermissionDetail.Text = detail;

        InputPanel.Visibility = Visibility.Collapsed;
        PermissionPanel.Visibility = Visibility.Visible;
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
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
            Margin = new Thickness(0, 0, 0, 2)
        };
        var content = CreateSelectableText(12,
            new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)));
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
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
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
            new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)));
    }

    private static Border CreateThinkingBubble(TextBox contentBlock)
    {
        var bubble = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 2, 40, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            BorderThickness = new Thickness(1)
        };

        var panel = new StackPanel();
        var label = new TextBlock
        {
            Text = "Thinking",
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 0, 0, 2)
        };
        panel.Children.Add(label);
        panel.Children.Add(contentBlock);
        bubble.Child = panel;

        return bubble;
    }

    private void AddToolMessage(string text)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, 2, 40, 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)),
            TextWrapping = TextWrapping.Wrap
        };

        border.Child = tb;
        MessageList.Children.Add(border);
        ScrollToBottom();
    }

    private void AddSystemMessage(string text, bool isWarning = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 10,
            Foreground = isWarning
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 6, 8, 6)
        };

        MessageList.Children.Add(tb);
        ScrollToBottom();
    }

    // --- Collapsible Messages ---

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
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
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

    private void ScrollToBottom()
    {
        MessageScroller.ScrollToEnd();
    }
}
