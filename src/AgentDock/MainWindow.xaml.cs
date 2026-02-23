using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgentDock.Controls;
using AgentDock.Models;
using AgentDock.Services;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Themes;
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
    private readonly Dictionary<ProjectInfo, Grid> _projectTabIcons = [];
    private readonly Dictionary<ProjectInfo, DispatcherTimer> _tabIconTimers = [];
    private readonly Dictionary<ProjectInfo, DockingManager> _projectDockingManagers = [];
    private ProjectInfo? _activeProject;

    public MainWindow()
    {
        Log.Info("MainWindow constructor starting");
        InitializeComponent();

        _toolbarPositionMenuItems = [ToolbarTopMenu, ToolbarLeftMenu, ToolbarRightMenu, ToolbarBottomMenu];

        CommandBindings.Add(new CommandBinding(AddProjectCommand, (_, _) => AddProject()));
        CommandBindings.Add(new CommandBinding(SaveWorkspaceCommand, (_, _) => SaveWorkspace()));
        CommandBindings.Add(new CommandBinding(CloseProjectCommand, (_, _) => CloseActiveProject()));

        // Set initial theme menu checkmarks
        UpdateThemeMenuCheckmarks();
        ThemeManager.ThemeChanged += OnThemeChanged;

        // Sync maximize/restore icon whenever window state changes (button click, double-click, aero snap, etc.)
        StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();

        Log.Info("MainWindow constructor complete");
    }

    // --- Maximized window sizing (prevent overflow beyond screen) ---

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_GETMINMAXINFO — constrain maximized size to the work area
        if (msg == 0x0024)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref mi);
                mmi.ptMaxPosition.X = Math.Abs(mi.rcWork.Left - mi.rcMonitor.Left);
                mmi.ptMaxPosition.Y = Math.Abs(mi.rcWork.Top - mi.rcMonitor.Top);
                mmi.ptMaxSize.X = Math.Abs(mi.rcWork.Right - mi.rcWork.Left);
                mmi.ptMaxSize.Y = Math.Abs(mi.rcWork.Bottom - mi.rcWork.Top);
            }
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    // --- Window Buttons ---

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeIcon()
    {
        if (WindowState == WindowState.Maximized)
        {
            MaximizeIcon.Text = "\uE923"; // Restore
            MaximizeButton.ToolTip = "Restore Down";
        }
        else
        {
            MaximizeIcon.Text = "\uE922"; // Maximize
            MaximizeButton.ToolTip = "Maximize";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
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
        ThemeManager.ApplyTheme(AppTheme.Light);
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyTheme(AppTheme.Dark);
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

        // Add tab button to toolbar
        ToolbarPanel.Children.Add(tabButton);
        ToolbarBorder.Visibility = Visibility.Visible;
        Log.Info("AddProject: tab button added");

        // Create docking layout for this project
        Log.Info("AddProject: creating docking layout");
        var (content, chatControl) = CreateProjectDockingLayout(project);
        _projectContents[project] = content;
        _projectChatControls[project] = chatControl;
        chatControl.SessionStateChanged += state => UpdateTabIcon(project, state);
        Log.Info("AddProject: docking layout created");

        // Switch to the new project
        Log.Info("AddProject: switching to project");
        SwitchToProject(project);
        Log.Info("AddProject: complete");
    }

    private Button CreateProjectTabButton(ProjectInfo project)
    {
        // Icon container: base icon + overlay badges
        var iconGrid = new Grid
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var baseIcon = new TextBlock
        {
            Name = "baseIcon",
            Text = "\uED25", // folder icon — no session
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = ThemeManager.GetBrush("TabIconNoSessionForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var skullBadge = new TextBlock
        {
            Name = "skullBadge",
            Text = "\u2620", // ☠
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };

        var statusBadge = new TextBlock
        {
            Name = "statusBadge",
            Text = "",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };

        iconGrid.Children.Add(baseIcon);
        iconGrid.Children.Add(skullBadge);
        iconGrid.Children.Add(statusBadge);

        _projectTabIcons[project] = iconGrid;

        var button = new Button
        {
            MinWidth = 44,
            Height = 36,
            Margin = new Thickness(2),
            Padding = new Thickness(8, 4, 8, 4),
            Background = Brushes.Transparent,
            BorderBrush = ThemeManager.GetBrush("TabButtonBorderBrush"),
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
                    iconGrid,
                    new TextBlock
                    {
                        Text = project.FolderName,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = ThemeManager.GetBrush("TabButtonForeground")
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

    private (DockingManager, AiChatControl) CreateProjectDockingLayout(ProjectInfo project)
    {
        Log.Info("CreateDockingLayout: starting");
        var dockingManager = new DockingManager
        {
            Theme = ThemeManager.CurrentTheme == AppTheme.Dark
                ? new Vs2013DarkTheme()
                : new Vs2013LightTheme()
        };

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
        _projectDockingManagers[project] = dockingManager;

        Log.Info("CreateDockingLayout: complete");
        return (dockingManager, aiChatControl);
    }

    private static UIElement CreatePanelPlaceholder(string title, string subtitle)
    {
        return new Border
        {
            Background = ThemeManager.GetBrush("PlaceholderBackground"),
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
                        Foreground = ThemeManager.GetBrush("PlaceholderTitleForeground"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 4)
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        FontSize = 12,
                        Foreground = ThemeManager.GetBrush("PlaceholderSubtitleForeground"),
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
        TitleBarText.Text = project.FolderName;
    }

    private static void SetTabButtonActive(Button button, bool active)
    {
        if (active)
        {
            button.Background = ThemeManager.GetBrush("TabButtonActiveBackground");
            button.BorderBrush = ThemeManager.GetBrush("TabButtonActiveBorderBrush");
        }
        else
        {
            button.Background = ThemeManager.GetBrush("TabButtonInactiveBackground");
            button.BorderBrush = ThemeManager.GetBrush("TabButtonBorderBrush");
        }
    }

    private void CloseActiveProject()
    {
        if (_activeProject != null)
            CloseProject(_activeProject);
    }

    private void CloseProject(ProjectInfo project)
    {
        // Stop icon animation timer
        if (_tabIconTimers.TryGetValue(project, out var timer))
        {
            timer.Stop();
            _tabIconTimers.Remove(project);
        }

        // Shutdown AI chat session
        if (_projectChatControls.TryGetValue(project, out var chatControl))
        {
            chatControl.Shutdown();
            _projectChatControls.Remove(project);
        }

        // Remove tab button and icon reference
        if (_projectTabButtons.TryGetValue(project, out var button))
        {
            ToolbarPanel.Children.Remove(button);
            _projectTabButtons.Remove(project);
        }

        _projectTabIcons.Remove(project);
        _projectDockingManagers.Remove(project);

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
                ToolbarBorder.Visibility = Visibility.Collapsed;
                Title = "Agent Dock";
                TitleBarText.Text = "";
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

    // --- Tab Icon Updates ---

    private void UpdateTabIcon(ProjectInfo project, ClaudeSessionState state)
    {
        if (!_projectTabIcons.TryGetValue(project, out var iconGrid))
            return;

        var baseIcon = (TextBlock)iconGrid.Children[0];
        var skullBadge = (TextBlock)iconGrid.Children[1];
        var statusBadge = (TextBlock)iconGrid.Children[2];

        // Stop any existing pulse timer for this project
        if (_tabIconTimers.TryGetValue(project, out var existingTimer))
        {
            existingTimer.Stop();
            _tabIconTimers.Remove(project);
        }

        // Check dangerous mode
        var isDangerous = _projectChatControls.TryGetValue(project, out var chat) && chat.IsDangerousMode;

        // Reset
        skullBadge.Visibility = Visibility.Collapsed;
        statusBadge.Visibility = Visibility.Collapsed;
        statusBadge.Opacity = 1.0;
        baseIcon.Opacity = 1.0;
        baseIcon.FontSize = 18;

        switch (state)
        {
            case ClaudeSessionState.NotStarted:
            case ClaudeSessionState.Exited:
                baseIcon.Text = "\uED25"; // folder icon
                baseIcon.FontFamily = new FontFamily("Segoe MDL2 Assets");
                baseIcon.FontSize = 16;
                baseIcon.Foreground = ThemeManager.GetBrush("TabIconNoSessionForeground");
                break;

            case ClaudeSessionState.Initializing:
                baseIcon.Text = "\u25C6"; // ◆
                baseIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)); // orange — waiting
                if (isDangerous)
                    skullBadge.Visibility = Visibility.Visible;
                break;

            case ClaudeSessionState.Idle:
                baseIcon.Text = "\u25C6"; // ◆
                baseIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)); // green
                if (isDangerous)
                    skullBadge.Visibility = Visibility.Visible;
                break;

            case ClaudeSessionState.Working:
                baseIcon.Text = "\u25C6"; // ◆
                baseIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)); // blue
                if (isDangerous)
                    skullBadge.Visibility = Visibility.Visible;
                // Pulse the main diamond
                StartBaseIconPulse(project, baseIcon);
                break;

            case ClaudeSessionState.WaitingForPermission:
                baseIcon.Text = "\u25C6"; // ◆
                baseIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)); // orange
                if (isDangerous)
                    skullBadge.Visibility = Visibility.Visible;
                break;

            case ClaudeSessionState.Error:
                baseIcon.Text = "\u25C6"; // ◆
                baseIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)); // red
                statusBadge.Text = "!";
                statusBadge.FontWeight = FontWeights.Bold;
                statusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)); // red
                statusBadge.Visibility = Visibility.Visible;
                break;
        }
    }

    private void StartBaseIconPulse(ProjectInfo project, TextBlock baseIcon)
    {
        var bright = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            bright = !bright;
            baseIcon.Opacity = bright ? 1.0 : 0.3;
        };
        timer.Start();
        _tabIconTimers[project] = timer;
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
        ThemeManager.ThemeChanged -= OnThemeChanged;

        // Stop all icon animation timers
        foreach (var timer in _tabIconTimers.Values)
            timer.Stop();
        _tabIconTimers.Clear();

        // Kill all Claude sessions gracefully
        foreach (var chatControl in _projectChatControls.Values)
            chatControl.Shutdown();

        _projectChatControls.Clear();
    }

    // --- Theme ---

    private void OnThemeChanged(AppTheme theme)
    {
        UpdateThemeMenuCheckmarks();

        // Update AvalonDock theme on all DockingManagers
        var dockTheme = theme == AppTheme.Dark
            ? (Theme)new Vs2013DarkTheme()
            : new Vs2013LightTheme();

        foreach (var dm in _projectDockingManagers.Values)
            dm.Theme = dockTheme;

        // Re-apply tab button colors
        foreach (var (project, button) in _projectTabButtons)
        {
            var isActive = project == _activeProject;
            SetTabButtonActive(button, isActive);

            // Update tab text foreground
            if (button.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                tb.Foreground = ThemeManager.GetBrush("TabButtonForeground");
        }

        // Re-apply tab icon foregrounds for no-session state
        foreach (var (project, iconGrid) in _projectTabIcons)
        {
            if (_projectChatControls.TryGetValue(project, out var chat))
                UpdateTabIcon(project, chat.CurrentState);
        }
    }

    private void UpdateThemeMenuCheckmarks()
    {
        ThemeLightMenu.IsChecked = ThemeManager.CurrentTheme == AppTheme.Light;
        ThemeDarkMenu.IsChecked = ThemeManager.CurrentTheme == AppTheme.Dark;
    }
}
