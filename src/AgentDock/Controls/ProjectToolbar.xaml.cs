using System.Windows;
using System.Windows.Controls;
using AgentDock.Models;
using AgentDock.Services;
using AvalonDock;
using AvalonDock.Layout;

namespace AgentDock.Controls;

/// <summary>
/// Per-project chrome above the docking area: start/stop the session, quick-toggle which panels
/// are on screen, glance at the send queue / scheduled message, and control autopilot for this one
/// repo. Session, queue and schedule concerns call straight into <see cref="AiChatControl"/>, which
/// already owns that state — only autopilot membership and prompt edits are raised as events for
/// <c>MainWindow</c> to handle, because the schedule is shared app-wide state this control does not
/// own.
/// </summary>
public partial class ProjectToolbar : UserControl
{
    private static readonly (string Id, string Label)[] PanelIds =
    [
        ("fileExplorer", "Files"),
        ("gitStatus", "Git"),
        ("filePreview", "Preview"),
        ("aiChat", "Chat"),
        ("projectDescription", "Notes"),
        ("todoList", "Todo"),
    ];

    private readonly AiChatControl _chat;
    private readonly DockingManager _dockingManager;
    private readonly Dictionary<string, Button> _panelButtons = new();

    public event Action? AutopilotToggleRequested;

    /// <summary>Fires with the trimmed text on edit, or null when "Reset to default" was clicked.</summary>
    public event Action<string?>? AutopilotPromptChanged;

    public ProjectToolbar(AiChatControl chat, DockingManager dockingManager)
    {
        _chat = chat;
        _dockingManager = dockingManager;
        InitializeComponent();

        var toggleStyle = (Style)FindResource("PanelToggleButton");
        foreach (var (id, label) in PanelIds)
        {
            var button = new Button { Content = label, Style = toggleStyle, Tag = "visible" };
            button.Click += (_, _) => TogglePanel(id);
            _panelButtons[id] = button;
            PanelTogglesPanel.Children.Add(button);
        }

        _chat.SessionStateChanged += _ => UpdateStartStopButton();
        _chat.ScheduleChanged += UpdateQueueIndicators;

        UpdateStartStopButton();
        UpdateQueueIndicators();
        RefreshPanelToggles();
    }

    // --- session ---

    private void UpdateStartStopButton()
    {
        var running = _chat.CurrentState is not (ClaudeSessionState.NotStarted or ClaudeSessionState.Exited);
        StartStopButton.Content = running ? "■ Stop" : "▶ Start";
        StartStopButton.ToolTip = running ? "End this session" : "Start a Claude session here";
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chat.CurrentState is ClaudeSessionState.NotStarted or ClaudeSessionState.Exited)
            _chat.StartSession(false);
        else
            _ = _chat.EndSessionAsync();
    }

    // --- panels ---

    private void TogglePanel(string contentId)
    {
        var anchorable = FindAnchorable(contentId);
        if (anchorable == null) return;

        if (anchorable.IsVisible) anchorable.Hide();
        else anchorable.Show();

        RefreshPanelToggles();
    }

    private LayoutAnchorable? FindAnchorable(string contentId) =>
        _dockingManager.Layout.Descendents().OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);

    private void RefreshPanelToggles()
    {
        foreach (var (id, button) in _panelButtons)
        {
            var anchorable = FindAnchorable(id);
            button.Tag = anchorable is { IsVisible: false } ? "hidden" : "visible";
        }
    }

    // --- queue / scheduled message ---

    private void UpdateQueueIndicators()
    {
        var count = _chat.QueuedMessages.Count;
        QueueButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueButton.Content = $"☰ {count}";
        QueueButton.ToolTip = $"{count} message(s) queued to send";

        var fireAt = _chat.ScheduledFireTimeUtc;
        ScheduledButton.Visibility = fireAt != null ? Visibility.Visible : Visibility.Collapsed;
        if (fireAt is { } utc)
        {
            var local = utc.ToLocalTime();
            ScheduledButton.Content = $"◷ {local:h:mmtt}".ToLowerInvariant();
            ScheduledButton.ToolTip = $"A message is scheduled for {local:h:mm tt}";
        }
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e) => FocusChatPanel();
    private void ScheduledButton_Click(object sender, RoutedEventArgs e) => FocusChatPanel();

    private void FocusChatPanel()
    {
        if (FindAnchorable("aiChat") is { } anchorable)
        {
            anchorable.Show();
            anchorable.IsActive = true;
        }
        _chat.FocusInput();
    }

    // --- autopilot ---

    private void AutopilotButton_Click(object sender, RoutedEventArgs e) =>
        AutopilotPopup.IsOpen = !AutopilotPopup.IsOpen;

    private void AutopilotMembershipButton_Click(object sender, RoutedEventArgs e) =>
        AutopilotToggleRequested?.Invoke();

    private void PromptBox_LostFocus(object sender, RoutedEventArgs e) =>
        AutopilotPromptChanged?.Invoke(PromptBox.Text);

    private void ResetPrompt_Click(object sender, RoutedEventArgs e)
    {
        PromptBox.Text = AutopilotPolicy.DefaultPrompt;
        AutopilotPromptChanged?.Invoke(null);
    }

    /// <summary>Pushed by MainWindow on every autopilot tick and whenever this project's
    /// membership changes.</summary>
    public void SetAutopilotStatus(string statusLine, bool isMember)
    {
        AutopilotStatusText.Text = statusLine;
        AutopilotMembershipButton.Content = isMember ? "Remove from autopilot" : "Add to autopilot";
        AutopilotButton.Content = isMember ? "✈ Autopilot ●" : "✈ Autopilot";
    }

    /// <summary>Pushed once at construction and whenever the saved prompt changes elsewhere (e.g.
    /// this project was just added to autopilot for the first time via the full dialog). Skipped
    /// while the box has focus so a live edit is never overwritten mid-keystroke.</summary>
    public void SetAutopilotPrompt(string text)
    {
        if (!PromptBox.IsFocused) PromptBox.Text = text;
    }
}
