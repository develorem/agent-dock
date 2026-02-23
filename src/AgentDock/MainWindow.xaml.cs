using System.IO;
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
using AgentDock.Windows;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
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
    private readonly Dictionary<ProjectInfo, GitStatusControl> _projectGitControls = [];
    private readonly Dictionary<ProjectInfo, Grid> _projectTabIcons = [];
    private readonly Dictionary<ProjectInfo, DispatcherTimer> _tabIconTimers = [];
    private readonly Dictionary<ProjectInfo, DockingManager> _projectDockingManagers = [];
    private ProjectInfo? _activeProject;

    // Workspace state
    private string? _currentWorkspacePath;
    private bool _workspaceDirty;
    private bool _suppressDirty; // suppress during workspace load

    // Prerequisites check results (populated on startup)
    private List<(string Name, bool Found, string Detail)>? _prerequisiteResults;

    // Panel ContentId constants (stable across app runs)
    private const string FileExplorerId = "fileExplorer";
    private const string GitStatusId = "gitStatus";
    private const string FilePreviewId = "filePreview";
    private const string AiChatId = "aiChat";

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

        // Restore saved toolbar position
        var savedPosition = AppSettings.GetString("ToolbarPosition", "Top");
        if (savedPosition != "Top")
            SetToolbarPosition(savedPosition);

        // Restore saved Claude path override
        var savedClaudePath = AppSettings.GetString("ClaudePath", "");
        if (!string.IsNullOrEmpty(savedClaudePath))
            ClaudeSession.ClaudeBinaryPath = savedClaudePath;

        // Populate recent workspaces menu
        PopulateRecentWorkspacesMenu();

        // Check for startup arguments
        if (App.StartupWorkspacePath != null)
        {
            // Defer to after window is fully loaded
            Loaded += (_, _) => OpenWorkspaceFile(App.StartupWorkspacePath);
        }
        else if (App.StartupProjectFolders.Count > 0)
        {
            Loaded += (_, _) =>
            {
                foreach (var folder in App.StartupProjectFolders)
                    AddProjectFromPath(folder);
            };
        }

        // Run prerequisite checks in background on startup
        Loaded += async (_, _) => await RunStartupPrerequisiteChecks();

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
        // Prompt to save if dirty
        if (!PromptSaveIfDirty())
            return; // user cancelled

        var dialog = new OpenFileDialog
        {
            Title = "Open Workspace",
            Filter = "Agent Dock Workspace (*.agentdock)|*.agentdock|All Files (*.*)|*.*",
            DefaultExt = ".agentdock"
        };

        if (dialog.ShowDialog() != true)
            return;

        OpenWorkspaceFile(dialog.FileName);
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
        AppSettings.SetString("ToolbarPosition", position);
    }

    private void ClaudePathOverride_Click(object sender, RoutedEventArgs e)
    {
        var currentPath = ClaudeSession.ClaudeBinaryPath;
        var result = ThemedMessageBox.Show(
            this,
            $"Current Claude path: {currentPath}\n\n" +
            "Do you want to select a custom Claude binary?\n" +
            "Click Yes to browse, No to reset to default (\"claude\" from PATH).",
            "Claude Path Override",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
            return;

        if (result == MessageBoxResult.No)
        {
            ClaudeSession.ClaudeBinaryPath = "claude";
            AppSettings.SetString("ClaudePath", "");
            Log.Info("ClaudePathOverride: reset to default");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select Claude Binary",
            Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*",
            FileName = currentPath == "claude" ? "" : currentPath
        };

        if (dialog.ShowDialog() != true)
            return;

        ClaudeSession.ClaudeBinaryPath = dialog.FileName;
        AppSettings.SetString("ClaudePath", dialog.FileName);
        Log.Info($"ClaudePathOverride: set to '{dialog.FileName}'");
    }

    // --- Help Menu ---

    private void GettingStarted_Click(object sender, RoutedEventArgs e)
    {
        var window = new Windows.GettingStartedWindow { Owner = this };
        window.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var window = new Windows.AboutWindow { Owner = this };
        window.ShowDialog();
    }

    private void CheckPrerequisites_Click(object sender, RoutedEventArgs e)
    {
        if (_prerequisiteResults == null)
        {
            // Shouldn't happen, but run synchronously as fallback
            _prerequisiteResults = RunPrerequisiteChecks();
        }
        ShowPrerequisiteResults();
    }

    private void ShowPrerequisiteResults()
    {
        var lines = _prerequisiteResults!
            .Select(r => r.Found
                ? $"\u2714  {r.Name} — {r.Detail}"
                : $"\u2716  {r.Name} — not found")
            .ToList();

        var allPassed = _prerequisiteResults!.All(r => r.Found);
        var icon = allPassed ? MessageBoxImage.Information : MessageBoxImage.Warning;
        var summary = allPassed ? "All prerequisites found." : "Some prerequisites are missing.";

        ThemedMessageBox.Show(
            this,
            string.Join("\n", lines) + "\n\n" + summary,
            "Prerequisites Check",
            MessageBoxButton.OK,
            icon);
    }

    private static List<(string Name, bool Found, string Detail)> RunPrerequisiteChecks()
    {
        var results = new List<(string Name, bool Found, string Detail)>();

        // Command-line tools
        var cliChecks = new (string Name, string Command, string Args)[]
        {
            ("Claude Code CLI", ClaudeSession.ClaudeBinaryPath, "--version"),
            ("Git", "git", "--version"),
            ("VS Code", "code", "--version"),
            ("Cursor", "cursor", "--version"),
        };

        foreach (var (name, command, args) in cliChecks)
        {
            var (found, detail) = CheckCommand(command, args);
            results.Add((name, found, detail));
        }

        // Visual Studio — check install directories
        var vsResult = FindVisualStudio();
        results.Add(vsResult);

        return results;
    }

    private static (bool Found, string Detail) CheckCommand(string command, string args)
    {
        try
        {
            // Use cmd.exe /c to resolve .cmd/.bat wrappers (e.g. code, cursor)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command} {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return (false, "");

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            if (process.ExitCode == 0)
            {
                // Take just the first line (e.g. VS Code outputs multiple lines)
                var firstLine = output.Split('\n')[0].Trim();
                return (true, firstLine);
            }

            return (false, "");
        }
        catch
        {
            return (false, "");
        }
    }

    private static (string Name, bool Found, string Detail) FindVisualStudio()
    {
        // Try vswhere.exe first (official VS detection tool)
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (File.Exists(vswhere))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-latest -property catalog_productDisplayVersion -format value",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);

                    if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        // Get the display name too
                        var namePsi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = vswhere,
                            Arguments = "-latest -property catalog_productLineVersion -format value",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var nameProcess = System.Diagnostics.Process.Start(namePsi);
                        var yearName = nameProcess?.StandardOutput.ReadToEnd().Trim() ?? "";
                        nameProcess?.WaitForExit(5000);

                        return ("Visual Studio", true, $"{yearName} ({output})".Trim());
                    }
                }
            }
            catch { /* fall through to directory scan */ }
        }

        // Fallback: scan install directories
        var editions = new[] { "Enterprise", "Professional", "Community", "Preview", "BuildTools" };
        var years = new[] { "2026", "2022" };

        foreach (var year in years)
        {
            foreach (var edition in editions)
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft Visual Studio", year, edition, "Common7", "IDE", "devenv.exe");
                if (File.Exists(path))
                    return ("Visual Studio", true, $"{year} {edition}");
            }
        }

        return ("Visual Studio", false, "");
    }

    private async System.Threading.Tasks.Task RunStartupPrerequisiteChecks()
    {
        Log.Info("Startup prerequisite check: starting");
        TitleBarText.Text = "Checking prerequisites...";

        _prerequisiteResults = await System.Threading.Tasks.Task.Run(RunPrerequisiteChecks);

        // Populate the static available tools set for context menus
        FileExplorerControl.AvailableTools.Clear();
        foreach (var (name, found, _) in _prerequisiteResults)
        {
            if (found)
                FileExplorerControl.AvailableTools.Add(name);
        }

        UpdateTitleBar(); // restore normal title
        Log.Info("Startup prerequisite check: complete");
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

        AddProjectFromPath(dialog.FolderName);
    }

    /// <summary>
    /// Adds a project by folder path. Used both by the dialog-based AddProject and workspace loading.
    /// </summary>
    /// <param name="folderPath">Path to the project folder.</param>
    /// <param name="layoutXml">Optional AvalonDock layout XML to restore instead of default layout.</param>
    /// <returns>The ProjectInfo if added, or null if duplicate/invalid.</returns>
    private ProjectInfo? AddProjectFromPath(string folderPath, string? layoutXml = null)
    {
        Log.Info($"AddProjectFromPath: '{folderPath}'");

        // Prevent duplicates
        if (_projects.Any(p => string.Equals(p.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warn($"AddProjectFromPath: duplicate folder '{folderPath}'");
            var existing = _projects.First(p =>
                string.Equals(p.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));
            SwitchToProject(existing);
            return existing;
        }

        if (!Directory.Exists(folderPath))
        {
            Log.Warn($"AddProjectFromPath: folder does not exist '{folderPath}'");
            ThemedMessageBox.Show(
                this,
                $"The folder '{folderPath}' does not exist.",
                "Folder Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        var project = new ProjectInfo { FolderPath = folderPath };
        _projects.Add(project);
        Log.Info($"AddProjectFromPath: created ProjectInfo for '{project.FolderName}'");

        // Create the tab button
        var tabButton = CreateProjectTabButton(project);
        _projectTabButtons[project] = tabButton;

        // Add tab button to toolbar
        ToolbarPanel.Children.Add(tabButton);
        ToolbarBorder.Visibility = Visibility.Visible;

        // Create docking layout for this project
        var (content, chatControl, gitControl) = CreateProjectDockingLayout(project, layoutXml);
        _projectContents[project] = content;
        _projectChatControls[project] = chatControl;
        _projectGitControls[project] = gitControl;
        chatControl.SessionStateChanged += state => UpdateTabIcon(project, state);

        // Switch to the new project
        SwitchToProject(project);

        SetWorkspaceDirty();
        Log.Info("AddProjectFromPath: complete");
        return project;
    }

    private static string? FindProjectLogo(string folderPath)
    {
        var folderName = Path.GetFileName(folderPath).ToLowerInvariant();
        var folderNameCompact = folderName.Replace("-", "").Replace("_", "").Replace(" ", "");
        // Search for common logo/icon file names, plus <foldername>.png/ico
        var basenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "logo", "icon", folderName, folderNameCompact };
        string[] extensions = [".png", ".ico"]; // WPF can't render .svg natively
        string[] dirs = [folderPath, Path.Combine(folderPath, "assets")];

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var basename in basenames)
            {
                foreach (var ext in extensions)
                {
                    var path = Path.Combine(dir, basename + ext);
                    if (File.Exists(path)) return path;
                }
            }
        }
        return null;
    }

    private Button CreateProjectTabButton(ProjectInfo project)
    {
        // Project logo or fallback folder icon
        var logoPath = FindProjectLogo(project.FolderPath);
        UIElement iconElement;

        if (logoPath != null && !logoPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var img = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(logoPath)),
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            iconElement = img;
        }
        else
        {
            iconElement = new TextBlock
            {
                Text = "\uED25",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = ThemeManager.GetBrush("TabIconNoSessionForeground"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
        }

        // Status diamond area (right of name): diamond + skull badge + error badge
        var statusGrid = new Grid
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var diamondIcon = new TextBlock
        {
            Name = "diamondIcon",
            Text = "\u25C7", // ◇ outline diamond — no session
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 14,
            Foreground = ThemeManager.GetBrush("TabIconInactiveDiamondForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var skullBadge = new TextBlock
        {
            Name = "skullBadge",
            Text = "\u2620", // ☠
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };

        var statusBadge = new TextBlock
        {
            Name = "statusBadge",
            Text = "",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };

        statusGrid.Children.Add(diamondIcon);
        statusGrid.Children.Add(skullBadge);
        statusGrid.Children.Add(statusBadge);

        _projectTabIcons[project] = statusGrid;

        // Custom template: border only, no default button chrome
        var tabTemplate = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentPresenter);
        tabTemplate.VisualTree = borderFactory;

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
            Template = tabTemplate,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    iconElement,
                    new TextBlock
                    {
                        Text = project.FolderName,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = ThemeManager.GetBrush("TabButtonForeground")
                    },
                    statusGrid
                }
            }
        };

        button.Click += (_, _) => SwitchToProject(project);

        // Hover: highlight border only (no background fill)
        button.MouseEnter += (_, _) =>
        {
            if (project != _activeProject)
                button.BorderBrush = ThemeManager.GetBrush("TabButtonHoverBorderBrush");
        };
        button.MouseLeave += (_, _) =>
        {
            if (project != _activeProject)
                button.BorderBrush = ThemeManager.GetBrush("TabButtonBorderBrush");
            else
                button.BorderBrush = ThemeManager.GetBrush("TabButtonActiveBorderBrush");
        };

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

    private (DockingManager, AiChatControl, GitStatusControl) CreateProjectDockingLayout(ProjectInfo project, string? layoutXml = null)
    {
        Log.Info("CreateDockingLayout: starting");
        var dockingManager = new DockingManager
        {
            Theme = ThemeManager.CurrentTheme == AppTheme.Dark
                ? new Vs2013DarkTheme()
                : new Vs2013LightTheme()
        };

        // Create controls
        var fileExplorerControl = new FileExplorerControl();
        fileExplorerControl.LoadDirectory(project.FolderPath);

        var gitStatusControl = new GitStatusControl();
        gitStatusControl.LoadRepository(project.FolderPath);

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

        var aiChatControl = new AiChatControl();
        aiChatControl.Initialize(project.FolderPath);

        // Refresh git status when Claude finishes working (transitions to Idle)
        aiChatControl.SessionStateChanged += state =>
        {
            if (state == ClaudeSessionState.Idle)
                gitStatusControl.RefreshStatus();
        };

        // Map ContentId → control for layout serialization callback
        var controlMap = new Dictionary<string, object>
        {
            [FileExplorerId] = fileExplorerControl,
            [GitStatusId] = gitStatusControl,
            [FilePreviewId] = filePreviewControl,
            [AiChatId] = aiChatControl
        };

        // Title map for restored anchorables
        var titleMap = new Dictionary<string, string>
        {
            [FileExplorerId] = $"{project.FolderName} — File Explorer",
            [GitStatusId] = "Git Status",
            [FilePreviewId] = "File Preview",
            [AiChatId] = "AI Chat"
        };

        if (layoutXml != null)
        {
            // Restore layout from saved XML
            Log.Info("CreateDockingLayout: restoring saved layout");
            try
            {
                var serializer = new XmlLayoutSerializer(dockingManager);
                serializer.LayoutSerializationCallback += (_, args) =>
                {
                    if (args.Model.ContentId != null && controlMap.TryGetValue(args.Model.ContentId, out var control))
                    {
                        args.Content = control;
                        // Restore title for anchorables
                        if (args.Model is LayoutAnchorable anchorable)
                        {
                            if (titleMap.TryGetValue(args.Model.ContentId, out var title))
                                anchorable.Title = title;
                            anchorable.CanClose = false;
                            anchorable.CanHide = false;
                        }
                        else if (args.Model is LayoutDocument doc)
                        {
                            if (titleMap.TryGetValue(args.Model.ContentId, out var title))
                                doc.Title = title;
                            doc.CanClose = false;
                        }
                    }
                    else
                    {
                        args.Cancel = true;
                    }
                };

                using var reader = new StringReader(layoutXml);
                serializer.Deserialize(reader);
            }
            catch (Exception ex)
            {
                Log.Warn($"CreateDockingLayout: failed to restore layout, falling back to default — {ex.Message}");
                // Fall through to build default layout
                BuildDefaultLayout(dockingManager, project, fileExplorerControl, gitStatusControl, filePreviewControl, aiChatControl);
            }
        }
        else
        {
            // Build default layout
            BuildDefaultLayout(dockingManager, project, fileExplorerControl, gitStatusControl, filePreviewControl, aiChatControl);
        }

        _projectDockingManagers[project] = dockingManager;

        // Track layout changes (panel rearrangement) for dirty state.
        // Subscribe to Layout.Updated, and re-subscribe if AvalonDock replaces the Layout.
        void SubscribeLayoutUpdated(LayoutRoot layout)
            => layout.Updated += (_, _) => SetWorkspaceDirty();
        SubscribeLayoutUpdated(dockingManager.Layout);
        dockingManager.LayoutChanged += (_, _) =>
        {
            if (dockingManager.Layout != null)
                SubscribeLayoutUpdated(dockingManager.Layout);
            SetWorkspaceDirty();
        };

        Log.Info("CreateDockingLayout: complete");
        return (dockingManager, aiChatControl, gitStatusControl);
    }

    private void BuildDefaultLayout(
        DockingManager dockingManager,
        ProjectInfo project,
        FileExplorerControl fileExplorerControl,
        GitStatusControl gitStatusControl,
        FilePreviewControl filePreviewControl,
        AiChatControl aiChatControl)
    {
        // Calculate column widths as ~33% each based on actual window width
        var availableWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var thirdWidth = (int)(availableWidth / 3);

        // --- Left column: AI Chat ---
        var aiChat = new LayoutAnchorable
        {
            Title = "AI Chat",
            ContentId = AiChatId,
            CanClose = false,
            CanHide = false,
            Content = aiChatControl
        };

        var leftColumn = new LayoutAnchorablePaneGroup
        {
            DockWidth = new GridLength(thirdWidth, GridUnitType.Pixel)
        };
        leftColumn.Children.Add(new LayoutAnchorablePane(aiChat));

        // --- Center column: File Preview ---
        var filePreview = new LayoutDocument
        {
            Title = "File Preview",
            ContentId = FilePreviewId,
            CanClose = false,
            Content = filePreviewControl
        };

        var centerPane = new LayoutDocumentPane(filePreview);
        var centerColumn = new LayoutDocumentPaneGroup();
        centerColumn.Children.Add(centerPane);

        // --- Right column: File Explorer (top) + Git Status (bottom) ---
        var fileExplorer = new LayoutAnchorable
        {
            Title = $"{project.FolderName} — File Explorer",
            ContentId = FileExplorerId,
            CanClose = false,
            CanHide = false,
            Content = fileExplorerControl
        };

        var gitStatus = new LayoutAnchorable
        {
            Title = "Git Status",
            ContentId = GitStatusId,
            CanClose = false,
            CanHide = false,
            Content = gitStatusControl
        };

        var leftTopPane = new LayoutAnchorablePane(fileExplorer);
        var leftBottomPane = new LayoutAnchorablePane(gitStatus);

        var rightColumn = new LayoutAnchorablePaneGroup
        {
            Orientation = Orientation.Vertical,
            DockWidth = new GridLength(thirdWidth, GridUnitType.Pixel)
        };
        rightColumn.Children.Add(leftTopPane);
        rightColumn.Children.Add(leftBottomPane);

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
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }

        // Update title bar
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        if (_activeProject == null)
        {
            Title = "Agent Dock";
            TitleBarText.Text = "";
            return;
        }

        var workspaceName = _currentWorkspacePath != null
            ? Path.GetFileNameWithoutExtension(_currentWorkspacePath)
            : null;

        var dirtyMarker = _workspaceDirty ? " *" : "";

        if (workspaceName != null)
        {
            Title = $"Agent Dock — {workspaceName}{dirtyMarker} — {_activeProject.FolderName}";
            TitleBarText.Text = $"{workspaceName}{dirtyMarker} — {_activeProject.FolderName}";
        }
        else
        {
            Title = $"Agent Dock — {_activeProject.FolderName}{dirtyMarker}";
            TitleBarText.Text = $"{_activeProject.FolderName}{dirtyMarker}";
        }
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

        // Stop file system watcher for git status
        if (_projectGitControls.TryGetValue(project, out var gitControl))
        {
            gitControl.StopWatching();
            _projectGitControls.Remove(project);
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

        SetWorkspaceDirty();

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
                EmptyStatePanel.Visibility = Visibility.Visible;
                ToolbarBorder.Visibility = Visibility.Collapsed;
                UpdateTitleBar();
            }
        }
    }

    private void CloseAllProjects()
    {
        // Close all projects without dirty tracking (caller handles that)
        var projectsCopy = _projects.ToList();
        foreach (var project in projectsCopy)
        {
            if (_tabIconTimers.TryGetValue(project, out var timer))
            {
                timer.Stop();
                _tabIconTimers.Remove(project);
            }

            if (_projectGitControls.TryGetValue(project, out var gitControl))
            {
                gitControl.StopWatching();
                _projectGitControls.Remove(project);
            }

            if (_projectChatControls.TryGetValue(project, out var chatControl))
            {
                chatControl.Shutdown();
                _projectChatControls.Remove(project);
            }

            if (_projectTabButtons.TryGetValue(project, out var button))
            {
                ToolbarPanel.Children.Remove(button);
                _projectTabButtons.Remove(project);
            }

            _projectTabIcons.Remove(project);
            _projectDockingManagers.Remove(project);
            _projectContents.Remove(project);
        }

        _projects.Clear();
        _activeProject = null;
        ProjectContentHost.Content = null;
        ProjectContentHost.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Visible;
        ToolbarBorder.Visibility = Visibility.Collapsed;
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
        if (!_projectTabIcons.TryGetValue(project, out var statusGrid))
            return;

        var diamondIcon = (TextBlock)statusGrid.Children[0];
        var skullBadge = (TextBlock)statusGrid.Children[1];
        var statusBadge = (TextBlock)statusGrid.Children[2];

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
        diamondIcon.Opacity = 1.0;

        switch (state)
        {
            case ClaudeSessionState.NotStarted:
            case ClaudeSessionState.Exited:
                diamondIcon.Text = "\u25C7"; // ◇ outline diamond
                diamondIcon.Foreground = ThemeManager.GetBrush("TabIconInactiveDiamondForeground");
                break;

            case ClaudeSessionState.Initializing:
                diamondIcon.Text = "\u25C6"; // ◆ filled diamond
                diamondIcon.Foreground = ThemeManager.GetBrush("TabIconWaitingForeground");
                if (isDangerous)
                    skullBadge.Visibility = Visibility.Visible;
                break;

            case ClaudeSessionState.Idle:
                diamondIcon.Text = "\u25C6"; // ◆
                diamondIcon.Foreground = ThemeManager.GetBrush("TabIconIdleForeground");
                if (isDangerous)
                    skullBadge.Visibility = Visibility.Visible;
                break;

            case ClaudeSessionState.Working:
                diamondIcon.Text = "\u25C6"; // ◆
                diamondIcon.Foreground = ThemeManager.GetBrush("TabIconWorkingForeground");
                if (isDangerous)
                    skullBadge.Visibility = Visibility.Visible;
                StartDiamondPulse(project, diamondIcon);
                break;

            case ClaudeSessionState.WaitingForPermission:
                diamondIcon.Text = "\u25C6"; // ◆
                diamondIcon.Foreground = ThemeManager.GetBrush("TabIconWaitingForeground");
                if (isDangerous)
                    skullBadge.Visibility = Visibility.Visible;
                break;

            case ClaudeSessionState.Error:
                diamondIcon.Text = "\u25C6"; // ◆
                diamondIcon.Foreground = ThemeManager.GetBrush("TabIconErrorForeground");
                statusBadge.Text = "!";
                statusBadge.FontWeight = FontWeights.Bold;
                statusBadge.Foreground = ThemeManager.GetBrush("TabIconErrorForeground");
                statusBadge.Visibility = Visibility.Visible;
                break;
        }
    }

    private void StartDiamondPulse(ProjectInfo project, TextBlock diamondIcon)
    {
        var bright = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            bright = !bright;
            diamondIcon.Opacity = bright ? 1.0 : 0.3;
        };
        timer.Start();
        _tabIconTimers[project] = timer;
    }

    // --- Workspace Save/Load ---

    private void SetWorkspaceDirty()
    {
        if (_suppressDirty || _workspaceDirty)
            return;
        _workspaceDirty = true;
        UpdateTitleBar();
    }

    private void SaveWorkspace()
    {
        if (_currentWorkspacePath == null)
        {
            // No existing workspace file — show SaveFileDialog
            var dialog = new SaveFileDialog
            {
                Title = "Save Workspace",
                Filter = "Agent Dock Workspace (*.agentdock)|*.agentdock",
                DefaultExt = ".agentdock"
            };

            if (dialog.ShowDialog() != true)
                return;

            _currentWorkspacePath = dialog.FileName;
        }

        Log.Info($"SaveWorkspace: saving to '{_currentWorkspacePath}'");

        var workspace = new WorkspaceFile
        {
            Theme = ThemeManager.CurrentTheme.ToString(),
            ToolbarPosition = _currentToolbarPosition,
            ActiveProjectPath = _activeProject?.FolderPath
        };

        foreach (var project in _projects)
        {
            var wp = new WorkspaceProject
            {
                FolderPath = project.FolderPath
            };

            // Serialize AvalonDock layout
            if (_projectDockingManagers.TryGetValue(project, out var dm))
            {
                try
                {
                    var serializer = new XmlLayoutSerializer(dm);
                    using var writer = new StringWriter();
                    serializer.Serialize(writer);
                    wp.DockingLayout = writer.ToString();
                }
                catch (Exception ex)
                {
                    Log.Warn($"SaveWorkspace: failed to serialize layout for '{project.FolderName}' — {ex.Message}");
                }
            }

            workspace.Projects.Add(wp);
        }

        try
        {
            WorkspaceManager.Save(_currentWorkspacePath, workspace);
            _workspaceDirty = false;
            UpdateTitleBar();
            PopulateRecentWorkspacesMenu();
            Log.Info("SaveWorkspace: complete");
        }
        catch (Exception ex)
        {
            Log.Error($"SaveWorkspace: failed — {ex.Message}");
            ThemedMessageBox.Show(
                this,
                $"Failed to save workspace:\n\n{ex.Message}",
                "Save Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenWorkspaceFile(string filePath)
    {
        Log.Info($"OpenWorkspaceFile: '{filePath}'");

        WorkspaceFile? workspace;
        try
        {
            workspace = WorkspaceManager.Load(filePath);
        }
        catch (Exception ex)
        {
            Log.Error($"OpenWorkspaceFile: failed to load — {ex.Message}");
            ThemedMessageBox.Show(
                this,
                $"Failed to open workspace:\n\n{ex.Message}",
                "Open Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (workspace == null)
        {
            ThemedMessageBox.Show(
                this,
                $"Could not read workspace file:\n{filePath}",
                "Open Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        // Close all current projects
        CloseAllProjects();

        // Suppress dirty tracking while restoring
        _suppressDirty = true;

        // Apply theme
        if (Enum.TryParse<AppTheme>(workspace.Theme, out var theme))
            ThemeManager.ApplyTheme(theme);

        // Apply toolbar position
        if (!string.IsNullOrEmpty(workspace.ToolbarPosition))
        {
            SetToolbarPosition(workspace.ToolbarPosition);
            AppSettings.SetString("ToolbarPosition", workspace.ToolbarPosition);
        }

        // Restore projects
        ProjectInfo? activeProject = null;
        foreach (var wp in workspace.Projects)
        {
            var project = AddProjectFromPath(wp.FolderPath, wp.DockingLayout);
            if (project != null && string.Equals(wp.FolderPath, workspace.ActiveProjectPath, StringComparison.OrdinalIgnoreCase))
                activeProject = project;
        }

        // Switch to the active project
        if (activeProject != null)
            SwitchToProject(activeProject);

        _suppressDirty = false;
        _currentWorkspacePath = filePath;
        _workspaceDirty = false;
        UpdateTitleBar();
        PopulateRecentWorkspacesMenu();
        Log.Info("OpenWorkspaceFile: complete");
    }

    /// <summary>
    /// Prompts the user to save if the workspace is dirty.
    /// Returns true if it's OK to proceed, false if user cancelled.
    /// </summary>
    private bool PromptSaveIfDirty()
    {
        if (!_workspaceDirty || _projects.Count == 0)
            return true;

        var result = ThemedMessageBox.Show(
            this,
            "Save workspace changes before continuing?",
            "Agent Dock",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Yes:
                SaveWorkspace();
                return true;
            case MessageBoxResult.No:
                return true;
            case MessageBoxResult.Cancel:
            default:
                return false;
        }
    }

    // --- Recent Workspaces ---

    private void PopulateRecentWorkspacesMenu()
    {
        var recent = WorkspaceManager.GetRecentWorkspaces();

        RecentWorkspacesMenu.Items.Clear();

        if (recent.Count == 0)
        {
            RecentWorkspacesMenu.IsEnabled = false;
            return;
        }

        RecentWorkspacesMenu.IsEnabled = true;

        foreach (var path in recent)
        {
            var item = new MenuItem
            {
                Header = Path.GetFileNameWithoutExtension(path),
                ToolTip = path
            };
            var capturedPath = path;
            item.Click += (_, _) =>
            {
                if (!PromptSaveIfDirty())
                    return;
                OpenWorkspaceFile(capturedPath);
            };
            RecentWorkspacesMenu.Items.Add(item);
        }
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
        // Prompt to save if dirty
        if (_workspaceDirty && _projects.Count > 0)
        {
            var result = ThemedMessageBox.Show(
                this,
                "Save workspace changes before closing?",
                "Agent Dock",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            switch (result)
            {
                case MessageBoxResult.Cancel:
                    e.Cancel = true;
                    return;
                case MessageBoxResult.Yes:
                    SaveWorkspace();
                    break;
                // No — just close
            }
        }

        ThemeManager.ThemeChanged -= OnThemeChanged;

        // Stop all icon animation timers
        foreach (var timer in _tabIconTimers.Values)
            timer.Stop();
        _tabIconTimers.Clear();

        // Stop all file system watchers
        foreach (var gitControl in _projectGitControls.Values)
            gitControl.StopWatching();
        _projectGitControls.Clear();

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

            if (button.Content is StackPanel sp)
            {
                // Update folder icon foreground (child 0)
                if (sp.Children.Count > 0 && sp.Children[0] is TextBlock folderIcon)
                    folderIcon.Foreground = ThemeManager.GetBrush("TabIconNoSessionForeground");

                // Update tab text foreground (child 1)
                if (sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                    tb.Foreground = ThemeManager.GetBrush("TabButtonForeground");
            }
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
