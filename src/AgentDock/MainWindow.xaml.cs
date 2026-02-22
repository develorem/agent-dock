using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentDock;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand AddProjectCommand =
        new("Add Project", nameof(AddProjectCommand), typeof(MainWindow));

    public static readonly RoutedUICommand SaveWorkspaceCommand =
        new("Save Workspace", nameof(SaveWorkspaceCommand), typeof(MainWindow));

    private readonly MenuItem[] _toolbarPositionMenuItems;
    private string _currentToolbarPosition = "Top";

    public MainWindow()
    {
        InitializeComponent();

        _toolbarPositionMenuItems = [ToolbarTopMenu, ToolbarLeftMenu, ToolbarRightMenu, ToolbarBottomMenu];

        CommandBindings.Add(new CommandBinding(AddProjectCommand, (_, _) => AddProject()));
        CommandBindings.Add(new CommandBinding(SaveWorkspaceCommand, (_, _) => SaveWorkspace()));
    }

    // --- File Menu ---

    private void AddProject_Click(object sender, RoutedEventArgs e) => AddProject();

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        // Stub — Task 12
    }

    private void SaveWorkspace_Click(object sender, RoutedEventArgs e) => SaveWorkspace();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // --- Settings Menu ---

    private void ThemeLight_Click(object sender, RoutedEventArgs e)
    {
        ThemeLightMenu.IsChecked = true;
        ThemeDarkMenu.IsChecked = false;
        // Theme switching — Task 11
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e)
    {
        ThemeDarkMenu.IsChecked = true;
        ThemeLightMenu.IsChecked = false;
        // Theme switching — Task 11
    }

    private void ToolbarPosition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem clicked || clicked.Tag is not string position)
            return;

        SetToolbarPosition(position);
    }

    private void ClaudePathOverride_Click(object sender, RoutedEventArgs e)
    {
        // Stub — Task 13/Settings
    }

    // --- Help Menu ---

    private void GettingStarted_Click(object sender, RoutedEventArgs e)
    {
        // Stub — Task 13
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Agent Dock v0.1.0\n\n" +
            "Manage multiple Claude Code AI sessions across projects.\n\n" +
            "Built by Develorem\n" +
            "https://github.com/develorem/agent-dock",
            "About Agent Dock",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // --- Core Methods ---

    private void AddProject()
    {
        // Stub — Task 3
    }

    private void SaveWorkspace()
    {
        // Stub — Task 12
    }

    private void SetToolbarPosition(string position)
    {
        if (position == _currentToolbarPosition)
            return;

        // Uncheck all position menu items, check the selected one
        foreach (var item in _toolbarPositionMenuItems)
            item.IsChecked = false;

        // Update border thickness to show separator on the correct edge
        Dock dock;
        Thickness borderThickness;
        Orientation orientation;

        switch (position)
        {
            case "Top":
                dock = Dock.Top;
                borderThickness = new Thickness(0, 0, 0, 1);
                orientation = Orientation.Horizontal;
                ToolbarTopMenu.IsChecked = true;
                break;
            case "Bottom":
                dock = Dock.Bottom;
                borderThickness = new Thickness(0, 1, 0, 0);
                orientation = Orientation.Horizontal;
                ToolbarBottomMenu.IsChecked = true;
                break;
            case "Left":
                dock = Dock.Left;
                borderThickness = new Thickness(0, 0, 1, 0);
                orientation = Orientation.Vertical;
                ToolbarLeftMenu.IsChecked = true;
                break;
            case "Right":
                dock = Dock.Right;
                borderThickness = new Thickness(1, 0, 0, 0);
                orientation = Orientation.Vertical;
                ToolbarRightMenu.IsChecked = true;
                break;
            default:
                return;
        }

        DockPanel.SetDock(ToolbarBorder, dock);
        ToolbarBorder.BorderThickness = borderThickness;
        ToolbarPanel.Orientation = orientation;
        _currentToolbarPosition = position;

        // Force layout recalculation — remove and re-add children of WorkspacePanel
        // DockPanel doesn't re-layout when Dock changes on existing children
        var children = new UIElement[WorkspacePanel.Children.Count];
        for (int i = 0; i < WorkspacePanel.Children.Count; i++)
            children[i] = WorkspacePanel.Children[i];

        WorkspacePanel.Children.Clear();
        foreach (var child in children)
            WorkspacePanel.Children.Add(child);
    }
}
