using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentDock.Models;
using AgentDock.Services;
using Microsoft.Win32;

using AgentDock.Services.Abstractions;

namespace AgentDock.Windows;

public partial class AppSettingsDialog : Window
{
    public class Result
    {
        public string ThemeId { get; set; } = "";
        public string ToolbarPosition { get; set; } = "Top";
        public string ClaudePath { get; set; } = "";
        public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;
    }

    public Result? Outcome { get; private set; }

    // Null until InitializeComponent returns. The nav list's first item is selected in
    // XAML, so NavList_SelectionChanged fires mid-load, before this is assigned.
    private readonly StackPanel[]? _sections;

    private readonly IThemeRegistry _themeRegistry;
    private readonly ILogService _log;

    private AppSettingsDialog(
        IThemeRegistry themeRegistry,
        ILogService log,
        string currentThemeId,
        string currentToolbarPosition,
        string currentClaudePath,
        UpdateChannel currentChannel)
    {
        _themeRegistry = themeRegistry;
        _log = log;
        InitializeComponent();
        _sections = [AppearanceSection, IntegrationsSection, UpdatesSection, DiagnosticsSection];

        ThemeCombo.ItemsSource = _themeRegistry.All;
        ThemeCombo.SelectedItem =
            _themeRegistry.FindById(currentThemeId) ?? _themeRegistry.Default;

        foreach (ComboBoxItem item in ToolbarPositionCombo.Items)
        {
            if ((string)item.Tag == currentToolbarPosition)
            {
                ToolbarPositionCombo.SelectedItem = item;
                break;
            }
        }
        if (ToolbarPositionCombo.SelectedItem == null)
            ToolbarPositionCombo.SelectedIndex = 0;

        // Empty / "claude" means default — show empty so the user knows it's the default.
        ClaudePathTextBox.Text = currentClaudePath is "" or "claude" ? "" : currentClaudePath;

        var channelTag = currentChannel.ToString();
        foreach (ComboBoxItem item in UpdateChannelCombo.Items)
        {
            if ((string)item.Tag == channelTag)
            {
                UpdateChannelCombo.SelectedItem = item;
                break;
            }
        }
        if (UpdateChannelCombo.SelectedItem == null)
            UpdateChannelCombo.SelectedIndex = 0;

        var logFile = _log.LogFilePath;
        LogsPathText.Text = !string.IsNullOrEmpty(logFile) && Path.GetDirectoryName(logFile) is { } dir
            ? $"Folder: {dir}"
            : "Log folder not yet created.";
    }

    public static Result? Show(
        IThemeRegistry themeRegistry,
        ILogService log,
        Window owner,
        string currentThemeId,
        string currentToolbarPosition,
        string currentClaudePath,
        UpdateChannel currentChannel)
    {
        var dlg = new AppSettingsDialog(
            themeRegistry, log, currentThemeId, currentToolbarPosition, currentClaudePath, currentChannel)
        { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Outcome : null;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent for the XAML-selected item; the sections
        // already have their correct start visibility from XAML, so nothing to do yet.
        if (_sections == null) return;

        if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag)
            return;

        foreach (var section in _sections)
            section.Visibility = Visibility.Collapsed;

        var target = tag switch
        {
            "integrations" => IntegrationsSection,
            "updates" => UpdatesSection,
            "diagnostics" => DiagnosticsSection,
            _ => AppearanceSection
        };
        target.Visibility = Visibility.Visible;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void BrowseClaudePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Claude Binary",
            Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*",
            FileName = ClaudePathTextBox.Text
        };

        if (dialog.ShowDialog(this) == true)
            ClaudePathTextBox.Text = dialog.FileName;
    }

    private void ResetClaudePath_Click(object sender, RoutedEventArgs e)
    {
        ClaudePathTextBox.Text = "";
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var logPath = _log.LogFilePath;
        var folder = !string.IsNullOrEmpty(logPath) ? Path.GetDirectoryName(logPath) : null;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            ThemedMessageBox.Show(this, "Logs folder not found.", "Open Logs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeCombo.SelectedItem as ThemeDescriptor ?? _themeRegistry.Default;
        var toolbar = (ToolbarPositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Top";
        var channelTag = (UpdateChannelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Stable";
        var channel = Enum.TryParse<UpdateChannel>(channelTag, ignoreCase: true, out var c)
            ? c
            : UpdateChannel.Stable;

        Outcome = new Result
        {
            ThemeId = theme.Id,
            ToolbarPosition = toolbar,
            ClaudePath = ClaudePathTextBox.Text.Trim(),
            UpdateChannel = channel
        };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
