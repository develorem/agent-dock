using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentDock.Models;
using AgentDock.Services;

namespace AgentDock.Windows;

public partial class WorkspaceSettingsDialog : Window
{
    public class Result
    {
        public string ThemeId { get; set; } = "";
        public string ToolbarPosition { get; set; } = "Top";
    }

    public Result? Outcome { get; private set; }

    private WorkspaceSettingsDialog(string currentThemeId, string currentToolbarPosition)
    {
        InitializeComponent();

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
    }

    public static Result? Show(Window owner, string currentThemeId, string currentToolbarPosition)
    {
        var dlg = new WorkspaceSettingsDialog(currentThemeId, currentToolbarPosition) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Outcome : null;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Single section for now — placeholder for when more sections are added.
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeCombo.SelectedItem as ThemeDescriptor ?? ThemeRegistry.Default;
        var toolbar = (ToolbarPositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Top";

        Outcome = new Result
        {
            ThemeId = theme.Id,
            ToolbarPosition = toolbar
        };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
