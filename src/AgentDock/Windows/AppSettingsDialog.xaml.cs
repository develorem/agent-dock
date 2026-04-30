using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentDock.Models;
using AgentDock.Services;
using Microsoft.Win32;

namespace AgentDock.Windows;

public partial class AppSettingsDialog : Window
{
    public class Result
    {
        public string ThemeId { get; set; } = "";
        public string ToolbarPosition { get; set; } = "Top";
        public string ClaudePath { get; set; } = "";
    }

    public Result? Outcome { get; private set; }

    private readonly StackPanel[] _sections;

    private AppSettingsDialog(string currentThemeId, string currentToolbarPosition, string currentClaudePath)
    {
        InitializeComponent();
        _sections = [AppearanceSection, IntegrationsSection, DiagnosticsSection];

        ThemeCombo.ItemsSource = ThemeRegistry.All;
        ThemeCombo.SelectedItem =
            ThemeRegistry.FindById(currentThemeId) ?? ThemeRegistry.Default;

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

        var logFile = Log.LogFilePath;
        LogsPathText.Text = !string.IsNullOrEmpty(logFile) && Path.GetDirectoryName(logFile) is { } dir
            ? $"Folder: {dir}"
            : "Log folder not yet created.";
    }

    public static Result? Show(Window owner, string currentThemeId, string currentToolbarPosition, string currentClaudePath)
    {
        var dlg = new AppSettingsDialog(currentThemeId, currentToolbarPosition, currentClaudePath) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Outcome : null;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag)
            return;

        foreach (var section in _sections)
            section.Visibility = Visibility.Collapsed;

        var target = tag switch
        {
            "integrations" => IntegrationsSection,
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
        var logPath = Log.LogFilePath;
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
        var theme = ThemeCombo.SelectedItem as ThemeDescriptor ?? ThemeRegistry.Default;
        var toolbar = (ToolbarPositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Top";

        Outcome = new Result
        {
            ThemeId = theme.Id,
            ToolbarPosition = toolbar,
            ClaudePath = ClaudePathTextBox.Text.Trim()
        };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
