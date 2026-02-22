using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentDock.Controls;
using AgentDock.Models;
using AgentDock.Services;
using AvalonDock;
using AvalonDock.Layout;
using Microsoft.Win32;

namespace AgentDock;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand AddProjectCommand =
        new("Add Project", nameof(AddProjectCommand), typeof(MainWindow));

    public static readonly RoutedUICommand SaveWorkspaceCommand =
        new("Save Workspace", nameof(SaveWorkspaceCommand), typeof(MainWindow));

    public static readonly RoutedUICommand CloseProjectCommand =
        new("Close Project", nameof(CloseProjectCommand), typeof(MainWindow));

    private readonly MenuItem[] _toolbarPositionMenuItems;
    private string _currentToolbarPosition = "Top";

    private readonly List<ProjectInfo> _projects = [];
    private readonly Dictionary<ProjectInfo, Button> _projectTabButtons = [];
    private readonly Dictionary<ProjectInfo, UIElement> _projectContents = [];
    private readonly Dictionary<ProjectInfo, AiChatControl> _projectChatControls = [];
    private ProjectInfo? _activeProject;

    public MainWindow()
    {
        Log.Info("MainWindow constructor starting");
        InitializeComponent();

        _toolbarPositionMenuItems = [ToolbarTopMenu, ToolbarLeftMenu, ToolbarRightMenu, ToolbarBottomMenu];

        CommandBindings.Add(new CommandBinding(AddProjectCommand, (_, _) => AddProject()));
        CommandBindings.Add(new CommandBinding(SaveWorkspaceCommand, (_, _) => SaveWorkspace()));
        CommandBindings.Add(new CommandBinding(CloseProjectCommand, (_, _) => CloseActiveProject()));
        Log.Info("MainWindow constructor complete");
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

    // --- Project Management ---

    private void AddProject()
    {
        Log.Info("AddProject: opening folder dialog");
        var dialog = new OpenFolderDialog
        {
            Title = "Select Project Folder"
        };

        if (dialog.ShowDialog() != true)
        {
            Log.Info("AddProject: dialog cancelled");
            return;
        }

        var folderPath = dialog.FolderName;
        Log.Info($"AddProject: selected folder '{folderPath}'");

        // Prevent duplicates
        if (_projects.Any(p => string.Equals(p.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warn($"AddProject: duplicate folder '{folderPath}'");
            MessageBox.Show(
                $"The folder '{folderPath}' is already open.",
                "Duplicate Project",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            // Switch to the existing project
            var existing = _projects.First(p =>
                string.Equals(p.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));
            SwitchToProject(existing);
            return;
        }

        var project = new ProjectInfo { FolderPath = folderPath };
        _projects.Add(project);
        Log.Info($"AddProject: created ProjectInfo for '{project.FolderName}'");

        // Create the tab button
        Log.Info("AddProject: creating tab button");
        var tabButton = CreateProjectTabButton(project);
        _projectTabButtons[project] = tabButton;

        // Insert tab button before the + button
        var addButtonIndex = ToolbarPanel.Children.IndexOf(AddProjectButton);
        ToolbarPanel.Children.Insert(addButtonIndex, tabButton);
        Log.Info("AddProject: tab button inserted");

        // Create docking layout for this project
        Log.Info("AddProject: creating docking layout");
        var (content, chatControl) = CreateProjectDockingLayout(project);
        _projectContents[project] = content;
        _projectChatControls[project] = chatControl;
        Log.Info("AddProject: docking layout created");

        // Switch to the new project
        Log.Info("AddProject: switching to project");
        SwitchToProject(project);
        Log.Info("AddProject: complete");
    }

    private Button CreateProjectTabButton(ProjectInfo project)
    {
        var button = new Button
        {
            MinWidth = 44,
            Height = 36,
            Margin = new Thickness(2),
            Padding = new Thickness(8, 4, 8, 4),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Tag = project,
            ToolTip = project.FolderPath,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    // Generic project icon (placeholder — Task 10 will add proper icons)
                    new TextBlock
                    {
                        Text = "\uD83D\uDCC1", // folder emoji as placeholder
                        FontSize = 14,
                        Margin = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = project.FolderName,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
                    }
                }
            }
        };

        button.Click += (_, _) => SwitchToProject(project);

        // Right-click context menu
        button.ContextMenu = new ContextMenu
        {
            Items =
            {
                CreateMenuItem("Close Project", () => CloseProject(project)),
                CreateMenuItem("Open in Explorer", () => OpenInExplorer(project))
            }
        };

        return button;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static (DockingManager, AiChatControl) CreateProjectDockingLayout(ProjectInfo project)
    {
        Log.Info("CreateDockingLayout: starting");
        var dockingManager = new DockingManager();

        // --- Left column: File Explorer (top) + Git Status (bottom) ---
        Log.Info("CreateDockingLayout: creating FileExplorerControl");
        var fileExplorerControl = new FileExplorerControl();
        fileExplorerControl.LoadDirectory(project.FolderPath);
        Log.Info("CreateDockingLayout: FileExplorerControl loaded");

        var fileExplorer = new LayoutAnchorable
        {
            Title = $"{project.FolderName} — File Explorer",
            ContentId = $"fileExplorer_{project.FolderPath.GetHashCode()}",
            CanClose = false,
            CanHide = false,
            Content = fileExplorerControl
        };

        Log.Info("CreateDockingLayout: creating GitStatusControl");
        var gitStatusControl = new GitStatusControl();
        gitStatusControl.LoadRepository(project.FolderPath);
        Log.Info("CreateDockingLayout: GitStatusControl loaded");

        var gitStatus = new LayoutAnchorable
        {
            Title = "Git Status",
            ContentId = $"gitStatus_{project.FolderPath.GetHashCode()}",
            CanClose = false,
            CanHide = false,
            Content = gitStatusControl
        };

        var leftTopPane = new LayoutAnchorablePane(fileExplorer);
        var leftBottomPane = new LayoutAnchorablePane(gitStatus);

        var leftColumn = new LayoutAnchorablePaneGroup
        {
            Orientation = Orientation.Vertical,
            DockWidth = new GridLength(250, GridUnitType.Pixel)
        };
        leftColumn.Children.Add(leftTopPane);
        leftColumn.Children.Add(leftBottomPane);
        Log.Info("CreateDockingLayout: left column assembled");

        // --- Center column: File Preview ---
        Log.Info("CreateDockingLayout: creating FilePreviewControl");
        var filePreviewControl = new FilePreviewControl();

        // Wire file explorer clicks to preview panel
        fileExplorerControl.FileSelected += filePath =>
        {
            Log.Info($"FileSelected: {filePath}");
            filePreviewControl.ShowFile(filePath);
        };

        // Wire git status diff clicks to preview panel
        gitStatusControl.DiffRequested += (filePath, diffContent) =>
        {
            Log.Info($"DiffRequested: {filePath}");
            filePreviewControl.ShowDiff(diffContent);
        };

        var filePreview = new LayoutDocument
        {
            Title = "File Preview",
            ContentId = $"filePreview_{project.FolderPath.GetHashCode()}",
            CanClose = false,
            Content = filePreviewControl
        };

        var centerPane = new LayoutDocumentPane(filePreview);
        var centerColumn = new LayoutDocumentPaneGroup();
        centerColumn.Children.Add(centerPane);
        Log.Info("CreateDockingLayout: center column assembled");

        // --- Right column: AI Chat ---
        Log.Info("CreateDockingLayout: creating AiChatControl");
        var aiChatControl = new AiChatControl();
        aiChatControl.Initialize(project.FolderPath);
        Log.Info("CreateDockingLayout: AiChatControl initialized");

        var aiChat = new LayoutAnchorable
        {
            Title = "AI Chat",
            ContentId = $"aiChat_{project.FolderPath.GetHashCode()}",
            CanClose = false,
            CanHide = false,
            Content = aiChatControl
        };

        var rightColumn = new LayoutAnchorablePaneGroup
        {
            DockWidth = new GridLength(350, GridUnitType.Pixel)
        };
        rightColumn.Children.Add(new LayoutAnchorablePane(aiChat));
        Log.Info("CreateDockingLayout: right column assembled");

        // --- Assemble the root layout ---
        var rootPanel = new LayoutPanel
        {
            Orientation = Orientation.Horizontal
        };
        rootPanel.Children.Add(leftColumn);
        rootPanel.Children.Add(centerColumn);
        rootPanel.Children.Add(rightColumn);

        var layoutRoot = new LayoutRoot();
        layoutRoot.RootPanel = rootPanel;

        dockingManager.Layout = layoutRoot;

        Log.Info("CreateDockingLayout: complete");
        return (dockingManager, aiChatControl);
    }

    private static UIElement CreatePanelPlaceholder(string title, string subtitle)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 16,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 4)
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };
    }

    private void SwitchToProject(ProjectInfo project)
    {
        Log.Info($"SwitchToProject: '{project.FolderName}'");
        if (_activeProject == project)
            return;

        // Update tab button styles
        if (_activeProject != null && _projectTabButtons.TryGetValue(_activeProject, out var prevButton))
            SetTabButtonActive(prevButton, false);

        _activeProject = project;

        if (_projectTabButtons.TryGetValue(project, out var newButton))
            SetTabButtonActive(newButton, true);

        // Show the project's content
        if (_projectContents.TryGetValue(project, out var content))
        {
            ProjectContentHost.Content = content;
            ProjectContentHost.Visibility = Visibility.Visible;
            EmptyStateText.Visibility = Visibility.Collapsed;
        }

        // Update title bar
        Title = $"Agent Dock — {project.FolderName}";
    }

    private static void SetTabButtonActive(Button button, bool active)
    {
        if (active)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0xD0, 0xE0, 0xF0));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x80, 0xC0));
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        }
    }

    private void CloseActiveProject()
    {
        if (_activeProject != null)
            CloseProject(_activeProject);
    }

    private void CloseProject(ProjectInfo project)
    {
        // Shutdown AI chat session
        if (_projectChatControls.TryGetValue(project, out var chatControl))
        {
            chatControl.Shutdown();
            _projectChatControls.Remove(project);
        }

        // Remove tab button
        if (_projectTabButtons.TryGetValue(project, out var button))
        {
            ToolbarPanel.Children.Remove(button);
            _projectTabButtons.Remove(project);
        }

        // Remove content
        _projectContents.Remove(project);

        // Remove from list
        _projects.Remove(project);

        // If this was the active project, switch to another or show empty state
        if (_activeProject == project)
        {
            _activeProject = null;

            if (_projects.Count > 0)
            {
                SwitchToProject(_projects[^1]);
            }
            else
            {
                ProjectContentHost.Content = null;
                ProjectContentHost.Visibility = Visibility.Collapsed;
                EmptyStateText.Visibility = Visibility.Visible;
                Title = "Agent Dock";
            }
        }
    }

    private static void OpenInExplorer(ProjectInfo project)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = project.FolderPath,
            UseShellExecute = true
        });
    }

    // --- Workspace ---

    private void SaveWorkspace()
    {
        // Stub — Task 12
    }

    // --- Toolbar Position ---

    private void SetToolbarPosition(string position)
    {
        if (position == _currentToolbarPosition)
            return;

        foreach (var item in _toolbarPositionMenuItems)
            item.IsChecked = false;

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

        // Force DockPanel layout recalculation
        var children = new UIElement[WorkspacePanel.Children.Count];
        for (int i = 0; i < WorkspacePanel.Children.Count; i++)
            children[i] = WorkspacePanel.Children[i];

        WorkspacePanel.Children.Clear();
        foreach (var child in children)
            WorkspacePanel.Children.Add(child);
    }

    // --- Window Closing ---

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Kill all Claude sessions gracefully
        foreach (var chatControl in _projectChatControls.Values)
            chatControl.Shutdown();

        _projectChatControls.Clear();
    }
}
