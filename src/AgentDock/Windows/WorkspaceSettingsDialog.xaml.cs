using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentDock.Models;
using AgentDock.Services;

using AgentDock.Services.Abstractions;

namespace AgentDock.Windows;

public partial class WorkspaceSettingsDialog : Window
{
    public class Result
    {
        public string ThemeId { get; set; } = "";
        public string ToolbarPosition { get; set; } = "Top";
        public bool ShowActiveProjectsGroup { get; set; }
        public int ActiveProjectsLimit { get; set; } = WorkspaceFile.DefaultActiveProjectsLimit;
    }

    public Result? Outcome { get; private set; }

    private readonly IThemeRegistry _themeRegistry;

    private WorkspaceSettingsDialog(
        IThemeRegistry themeRegistry,
        string currentThemeId,
        string currentToolbarPosition,
        bool showActiveProjectsGroup,
        int activeProjectsLimit)
    {
        _themeRegistry = themeRegistry;

        InitializeComponent();

        ShowActiveProjectsCheck.IsChecked = showActiveProjectsGroup;
        ActiveProjectsLimitBox.Text =
            WorkspaceFile.ClampActiveProjectsLimit(activeProjectsLimit).ToString();
        ActiveProjectsLimitLabel.Text =
            $"projects shown ({WorkspaceFile.MinActiveProjectsLimit}–{WorkspaceFile.MaxActiveProjectsLimit})";

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
    }

    public static Result? Show(
        IThemeRegistry themeRegistry,
        Window owner,
        string currentThemeId,
        string currentToolbarPosition,
        bool showActiveProjectsGroup,
        int activeProjectsLimit)
    {
        var dlg = new WorkspaceSettingsDialog(
            themeRegistry, currentThemeId, currentToolbarPosition, showActiveProjectsGroup, activeProjectsLimit)
        { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Outcome : null;
    }

    private static readonly Regex NonDigit = new("[^0-9]", RegexOptions.Compiled);

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = NonDigit.IsMatch(e.Text);

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Single section for now — placeholder for when more sections are added.
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeCombo.SelectedItem as ThemeDescriptor ?? _themeRegistry.Default;
        var toolbar = (ToolbarPositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Top";

        // An empty or out-of-range box falls back to the default rather than blocking OK.
        var limit = int.TryParse(ActiveProjectsLimitBox.Text, out var parsed)
            ? WorkspaceFile.ClampActiveProjectsLimit(parsed)
            : WorkspaceFile.DefaultActiveProjectsLimit;

        Outcome = new Result
        {
            ThemeId = theme.Id,
            ToolbarPosition = toolbar,
            ShowActiveProjectsGroup = ShowActiveProjectsCheck.IsChecked == true,
            ActiveProjectsLimit = limit
        };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
