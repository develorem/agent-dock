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
    private readonly Dictionary<ProjectInfo, ProjectDescriptionControl> _projectDescriptionControls = [];
    private readonly Dictionary<ProjectInfo, TodoListControl> _projectTodoListControls = [];
    private ProjectInfo? _activeProject;

    // Workspace state
    private string? _currentWorkspacePath;
    private bool _workspaceDirty;
    private bool _suppressDirty; // suppress during workspace load

    // Prerequisites check results (populated on startup)
    private List<(string Name, bool Found, string Detail)>? _prerequisiteResults;

    // App session start time (for elapsed display in title bar)
    private readonly DateTime _appStartTime = DateTime.Now;

    // Tab drag-and-drop reordering state
    private Point _tabDragStartPoint;
    private bool _tabDragging;
    private Button? _tabDragSource;
    private Border? _tabDragIndicator; // visual insertion line shown during drag

    // Toolbar add-project button (always last in ToolbarPanel)
    private readonly Button _toolbarAddButton;

    // Panel ContentId constants (stable across app runs)
    private const string FileExplorerId = "fileExplorer";
    private const string GitStatusId = "gitStatus";
    private const string FilePreviewId = "filePreview";
    private const string AiChatId = "aiChat";
    private const string ProjectDescriptionId = "projectDescription";
    private const string TodoListId = "todoList";

    public MainWindow()
    {
        Log.Info("MainWindow constructor starting");
        InitializeComponent();

        _toolbarPositionMenuItems = [ToolbarTopMenu, ToolbarLeftMenu, ToolbarRightMenu, ToolbarBottomMenu];

        // Create the + button for the project tab bar
        _toolbarAddButton = CreateToolbarAddButton();
        ToolbarPanel.Children.Add(_toolbarAddButton);

        // ToolbarPanel handles drag-over/drop for tab reordering (provides full-panel hit area)
        ToolbarPanel.AllowDrop = true;
        ToolbarPanel.DragOver += ToolbarPanel_DragOver;
        ToolbarPanel.Drop += ToolbarPanel_Drop;
        ToolbarPanel.DragLeave += (_, _) => RemoveTabDragIndicator();

        CommandBindings.Add(new CommandBinding(AddProjectCommand, (_, _) => AddProject()));
        CommandBindings.Add(new CommandBinding(SaveWorkspaceCommand, (_, _) => SaveWorkspace()));
        CommandBindings.Add(new CommandBinding(CloseProjectCommand, (_, _) => CloseActiveProject()));

        // Build theme menu and subscribe to changes
        PopulateThemeMenu();
        ThemeManager.ThemeChanged += OnThemeChanged;
        UpdateTaskbarIcon();

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

        // Run prerequisite checks and update check on startup
        Loaded += async (_, _) =>
        {
            await RunStartupPrerequisiteChecks();
            await CheckForAppUpdateAsync();
        };

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

    private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string themeId })
            ThemeManager.ApplyTheme(themeId);
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
        // Log environment info for diagnostics
        Log.Info($"Prereq: OS={Environment.OSVersion}, .NET={Environment.Version}, 64-bit={Environment.Is64BitProcess}");
        Log.Info($"Prereq: User={Environment.UserName}, Machine={Environment.MachineName}");

        var results = new List<(string Name, bool Found, string Detail)>();

        // Command-line tools — each gets: name, found/not-found, resolved path, version
        var cliChecks = new (string Name, string Command, string Args)[]
        {
            ("Claude Code CLI", ClaudeSession.ClaudeBinaryPath, "--version"),
            ("Git", "git", "--version"),
            ("VS Code", "code", "--version"),
            ("Cursor", "cursor", "--version"),
        };

        foreach (var (name, command, args) in cliChecks)
        {
            var (found, version) = CheckCommandVersion(command, args);
            var resolvedPath = ResolveCommandPath(command);
            if (found)
                Log.Info($"Prereq: {name} — FOUND, path='{resolvedPath}', version='{version}'");
            else
                Log.Info($"Prereq: {name} — NOT FOUND (searched PATH for '{command}')");
            results.Add((name, found, version));
        }

        // Claude-specific extras: log all matches on PATH and any custom override
        LogClaudePathDetails();

        // Visual Studio — check install directories
        var vsResult = FindVisualStudio();
        if (vsResult.Found)
            Log.Info($"Prereq: {vsResult.Name} — FOUND, {vsResult.Detail}");
        else
            Log.Info($"Prereq: {vsResult.Name} — NOT FOUND");
        results.Add(vsResult);

        return results;
    }

    /// <summary>
    /// Resolves a command name to its full path on the system PATH.
    /// Returns the first match (preferring .cmd/.bat for npm wrappers), or the bare name if not found.
    /// </summary>
    private static string ResolveCommandPath(string command)
    {
        if (Path.IsPathRooted(command) && File.Exists(command))
            return command;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = new[] { ".cmd", ".bat", "", ".exe" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, command + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return command;
    }

    /// <summary>
    /// Logs all claude binaries found on PATH and any custom path override.
    /// This helps diagnose cases where the wrong binary is picked up.
    /// </summary>
    private static void LogClaudePathDetails()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = new[] { ".cmd", ".bat", "", ".exe" };
        var found = new List<string>();

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, "claude" + ext);
                if (File.Exists(candidate))
                    found.Add(candidate);
            }
        }

        if (found.Count > 1)
            Log.Info($"Prereq: Claude — all matches on PATH ({found.Count}): {string.Join(", ", found)}");

        if (ClaudeSession.ClaudeBinaryPath != "claude")
            Log.Info($"Prereq: Claude — custom path override = '{ClaudeSession.ClaudeBinaryPath}'");
    }

    /// <summary>
    /// Runs a command via cmd.exe /c to get its version output.
    /// Returns (found, version-string).
    /// </summary>
    private static (bool Found, string Version) CheckCommandVersion(string command, string args)
    {
        try
        {
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

    private async System.Threading.Tasks.Task CheckForAppUpdateAsync()
    {
#if DEBUG
        Log.Info("UpdateCheck: skipping update check in Debug build");
        return;
#else
        var updateInfo = await System.Threading.Tasks.Task.Run(UpdateCheckService.CheckForUpdateAsync);
        if (updateInfo == null)
            return;

        Log.Info($"Update available: v{updateInfo.Version}");
        var dialog = new UpdateDialog(this, updateInfo);
        dialog.ShowDialog();
#endif
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

        // Add tab button to toolbar (before the + button, which is always last)
        var addBtnIdx = ToolbarPanel.Children.IndexOf(_toolbarAddButton);
        if (addBtnIdx >= 0)
            ToolbarPanel.Children.Insert(addBtnIdx, tabButton);
        else
            ToolbarPanel.Children.Add(tabButton);
        ToolbarBorder.Visibility = Visibility.Visible;

        // Create docking layout for this project
        var (content, chatControl, gitControl, descControl, todoControl) = CreateProjectDockingLayout(project, layoutXml);
        _projectContents[project] = content;
        _projectChatControls[project] = chatControl;
        _projectGitControls[project] = gitControl;
        _projectDescriptionControls[project] = descControl;
        _projectTodoListControls[project] = todoControl;
        chatControl.SessionStateChanged += state => UpdateTabIcon(project, state);

        // Switch to the new project
        SwitchToProject(project);

        SetWorkspaceDirty();
        Log.Info("AddProjectFromPath: complete");
        return project;
    }

    /// <summary>
    /// Discovers all candidate project logo/icon image files in the project folder.
    /// Returns absolute paths.
    /// </summary>
    internal static List<string> FindAllProjectLogos(string folderPath)
    {
        var results = new List<string>();
        var folderName = Path.GetFileName(folderPath).ToLowerInvariant();
        var folderNameCompact = folderName.Replace("-", "").Replace("_", "").Replace(" ", "");
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
                    if (File.Exists(path) && !results.Contains(path))
                        results.Add(path);
                }
            }
        }
        return results;
    }

    private static string? FindProjectLogo(string folderPath)
        => FindAllProjectLogos(folderPath).FirstOrDefault();

    /// <summary>
    /// Resolves the project icon: reads from .agentdock/settings.json,
    /// auto-discovers if not set, persists the result, and creates the UI element.
    /// </summary>
    private static UIElement ResolveProjectIcon(string folderPath)
    {
        var settings = ProjectSettingsManager.Load(folderPath);

        if (settings.Icon == null)
        {
            // Auto-discover and persist
            var discovered = FindProjectLogo(folderPath);
            if (discovered != null)
            {
                // Store as relative path if inside the project folder
                settings.Icon = discovered.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetRelativePath(folderPath, discovered)
                    : discovered;
            }
            else
            {
                settings.Icon = "folder"; // default built-in
            }

            ProjectSettingsManager.Save(folderPath, settings);
        }

        return CreateIconElement(settings.Icon, folderPath, settings.IconColor);
    }

    /// <summary>
    /// Creates a UIElement for the given icon value (built-in name or file path).
    /// </summary>
    private static UIElement CreateIconElement(string icon, string folderPath,
        string? iconColor = null)
    {
        // Check if it's a built-in icon name
        var builtIn = BuiltInIcons.Find(icon);
        if (builtIn != null)
        {
            var foreground = iconColor != null
                ? ParseHexBrush(iconColor) ?? ThemeManager.GetBrush("TabIconNoSessionForeground")
                : ThemeManager.GetBrush("TabIconNoSessionForeground");

            return new TextBlock
            {
                Text = builtIn.Glyph,
                FontFamily = new FontFamily(builtIn.FontFamily),
                FontSize = 14,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
        }

        // Resolve file path (relative to project folder or absolute)
        var filePath = Path.IsPathRooted(icon) ? icon : Path.Combine(folderPath, icon);

        if (File.Exists(filePath) && !filePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var img = new Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(filePath)),
                    Width = 16,
                    Height = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0)
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                return img;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to load project icon '{filePath}': {ex.Message}");
            }
        }

        // Fallback to default folder icon
        var fallback = BuiltInIcons.Default;
        return new TextBlock
        {
            Text = fallback.Glyph,
            FontFamily = new FontFamily(fallback.FontFamily),
            FontSize = 14,
            Foreground = ThemeManager.GetBrush("TabIconNoSessionForeground"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
    }

    /// <summary>
    /// Parses a hex colour string (#RRGGBB) into a SolidColorBrush, or null if invalid.
    /// </summary>
    private static SolidColorBrush? ParseHexBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return null;
        }
    }

    private Button CreateProjectTabButton(ProjectInfo project)
    {
        var iconElement = ResolveProjectIcon(project.FolderPath);

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

        button.Click += (_, _) =>
        {
            if (!_tabDragging)
                SwitchToProject(project);
        };

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

        // Drag initiation (DragOver/Drop handled at ToolbarPanel level)
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _tabDragStartPoint = e.GetPosition(null);
            _tabDragSource = button;
            _tabDragging = false;
        };
        button.PreviewMouseMove += (_, e) =>
        {
            if (_tabDragSource != button || e.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(null);
            var diff = pos - _tabDragStartPoint;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _tabDragging = true;
                DragDrop.DoDragDrop(button, new DataObject("ProjectTab", project), DragDropEffects.Move);
                _tabDragSource = null;
                RemoveTabDragIndicator();
            }
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

    private Button CreateToolbarAddButton()
    {
        // Custom template: border only, no default button chrome
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentPresenter);
        template.VisualTree = borderFactory;

        var button = new Button
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(2),
            Background = Brushes.Transparent,
            BorderBrush = ThemeManager.GetBrush("AddButtonBorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Add Project (Ctrl+N)",
            Template = template,
            Content = new TextBlock
            {
                Text = "+",
                FontSize = 18,
                FontWeight = FontWeights.Light,
                Foreground = ThemeManager.GetBrush("AddButtonForeground"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        button.Click += (_, _) => AddProject();

        button.MouseEnter += (_, _) =>
        {
            button.BorderBrush = ThemeManager.GetBrush("TabButtonHoverBorderBrush");
        };
        button.MouseLeave += (_, _) =>
        {
            button.BorderBrush = ThemeManager.GetBrush("AddButtonBorderBrush");
        };

        return button;
    }

    // --- Tab drag-and-drop (panel-level handlers) ---

    /// <summary>
    /// Finds the insertion index among tab buttons for the given mouse position.
    /// Returns the index in _projects where the dragged tab should be inserted.
    /// </summary>
    private int GetTabInsertionIndex(DragEventArgs e)
    {
        var isHorizontal = ToolbarPanel.Orientation == Orientation.Horizontal;

        for (int i = 0; i < ToolbarPanel.Children.Count; i++)
        {
            if (ToolbarPanel.Children[i] is not FrameworkElement child)
                continue;
            if (child == _toolbarAddButton || child == _tabDragIndicator || child.Tag is not ProjectInfo)
                continue;

            var pos = e.GetPosition(child);
            var midpoint = isHorizontal ? child.RenderSize.Width / 2 : child.RenderSize.Height / 2;
            var coord = isHorizontal ? pos.X : pos.Y;

            if (coord < midpoint)
            {
                // Before this tab — find its project index
                var proj = (ProjectInfo)child.Tag;
                return _projects.IndexOf(proj);
            }
        }

        // After all tabs
        return _projects.Count;
    }

    private void ToolbarPanel_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ProjectTab"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        // Show insertion indicator
        var insertIdx = GetTabInsertionIndex(e);
        ShowTabDragIndicator(insertIdx);
    }

    private void ToolbarPanel_Drop(object sender, DragEventArgs e)
    {
        RemoveTabDragIndicator();

        if (e.Data.GetData("ProjectTab") is not ProjectInfo sourceProject)
            return;

        var targetIdx = GetTabInsertionIndex(e);
        var sourceIdx = _projects.IndexOf(sourceProject);
        if (sourceIdx < 0 || targetIdx < 0) return;

        // Adjust target if dragging forward (removal shifts indices)
        if (targetIdx > sourceIdx)
            targetIdx--;
        if (targetIdx == sourceIdx)
            return;

        // Rearrange data model
        _projects.RemoveAt(sourceIdx);
        _projects.Insert(targetIdx, sourceProject);

        // Rearrange toolbar UI
        if (_projectTabButtons.TryGetValue(sourceProject, out var sourceBtn))
        {
            ToolbarPanel.Children.Remove(sourceBtn);
            // Insert at the correct UI position (before the + button)
            var addBtnIdx = ToolbarPanel.Children.IndexOf(_toolbarAddButton);
            var uiIdx = Math.Min(targetIdx, addBtnIdx >= 0 ? addBtnIdx : ToolbarPanel.Children.Count);
            ToolbarPanel.Children.Insert(uiIdx, sourceBtn);
        }

        SetWorkspaceDirty();
        e.Handled = true;
    }

    private void ShowTabDragIndicator(int projectIndex)
    {
        var isHorizontal = ToolbarPanel.Orientation == Orientation.Horizontal;

        // Create indicator if needed
        if (_tabDragIndicator == null)
        {
            _tabDragIndicator = new Border
            {
                Background = ThemeManager.GetBrush("TabButtonActiveBorderBrush"),
                IsHitTestVisible = false
            };
        }

        // Size the indicator
        if (isHorizontal)
        {
            _tabDragIndicator.Width = 3;
            _tabDragIndicator.Height = 30;
            _tabDragIndicator.Margin = new Thickness(-1, 3, -1, 3);
        }
        else
        {
            _tabDragIndicator.Width = 30;
            _tabDragIndicator.Height = 3;
            _tabDragIndicator.Margin = new Thickness(3, -1, 3, -1);
        }

        // Remove from current position
        ToolbarPanel.Children.Remove(_tabDragIndicator);

        // Find the UI insertion point (clamped before the + button)
        var addBtnIdx = ToolbarPanel.Children.IndexOf(_toolbarAddButton);
        var uiIdx = Math.Min(projectIndex, addBtnIdx >= 0 ? addBtnIdx : ToolbarPanel.Children.Count);
        ToolbarPanel.Children.Insert(uiIdx, _tabDragIndicator);
    }

    private void RemoveTabDragIndicator()
    {
        if (_tabDragIndicator != null)
        {
            ToolbarPanel.Children.Remove(_tabDragIndicator);
            _tabDragIndicator = null;
        }
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private (DockingManager, AiChatControl, GitStatusControl, ProjectDescriptionControl, TodoListControl) CreateProjectDockingLayout(ProjectInfo project, string? layoutXml = null)
    {
        Log.Info("CreateDockingLayout: starting");
        var dockingManager = new DockingManager
        {
            Theme = ThemeManager.BaseVariant == ThemeBaseVariant.Dark
                ? new Vs2013DarkTheme()
                : new Vs2013LightTheme()
        };

        // Create controls
        var fileExplorerControl = new FileExplorerControl();
        fileExplorerControl.LoadDirectory(project.FolderPath);

        var gitStatusControl = new GitStatusControl();
        gitStatusControl.LoadRepository(project.FolderPath);

        var filePreviewControl = new FilePreviewControl();

        var descriptionControl = new ProjectDescriptionControl();
        descriptionControl.LoadProject(project.FolderPath);

        var todoListControl = new TodoListControl();
        todoListControl.LoadProject(project.FolderPath);

        // Shared handler for refreshing UI after project settings change
        void OnProjectSettingsChanged()
        {
            var settings = ProjectSettingsManager.Load(project.FolderPath);

            if (_projectTabButtons.TryGetValue(project, out var tabBtn) &&
                tabBtn.Content is StackPanel sp && sp.Children.Count > 0)
            {
                sp.Children.RemoveAt(0);
                sp.Children.Insert(0, CreateIconElement(
                    settings.Icon ?? "folder", project.FolderPath,
                    settings.IconColor));
            }

            descriptionControl.SetDescription(settings.Description);
        }

        // Open settings dialog from description panel's settings icon
        descriptionControl.OpenSettingsRequested += () =>
        {
            var result = ProjectSettingsDialog.Show(this, project.FolderPath);
            if (result != null)
            {
                ProjectSettingsManager.Save(project.FolderPath, result);
                OnProjectSettingsChanged();
            }
        };

        // Wire settings change from file explorer settings dialog
        fileExplorerControl.ProjectSettingsChanged += OnProjectSettingsChanged;

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

        // Refresh file explorer when file system changes (reuses git status watcher)
        gitStatusControl.FileSystemChanged += () => fileExplorerControl.Refresh();

        var aiChatControl = new AiChatControl();
        aiChatControl.Initialize(project.FolderPath);

        // Refresh git status when Claude finishes working (transitions to Idle)
        aiChatControl.SessionStateChanged += state =>
        {
            if (state == ClaudeSessionState.Idle)
                gitStatusControl.RefreshStatus();
        };

        // Update AI Chat panel title with cumulative session stats, and update total cost
        aiChatControl.SessionStatsChanged += stats =>
        {
            if (_projectDockingManagers.TryGetValue(project, out var dm))
            {
                var anchorable = dm.Layout.Descendents().OfType<LayoutAnchorable>()
                    .FirstOrDefault(a => a.ContentId == AiChatId);
                if (anchorable != null)
                    anchorable.Title = $"AI Chat — ${stats.TotalCostUsd:F4} · {SessionStats.FormatTokens(stats.TotalTokens)} tokens";
            }

            UpdateTotalCost();
        };

        // Map ContentId → control for layout serialization callback
        var controlMap = new Dictionary<string, object>
        {
            [FileExplorerId] = fileExplorerControl,
            [GitStatusId] = gitStatusControl,
            [FilePreviewId] = filePreviewControl,
            [AiChatId] = aiChatControl,
            [ProjectDescriptionId] = descriptionControl,
            [TodoListId] = todoListControl
        };

        // Title map for restored anchorables
        var titleMap = new Dictionary<string, string>
        {
            [FileExplorerId] = $"{project.FolderName} — File Explorer",
            [GitStatusId] = "Git Status",
            [FilePreviewId] = "File Preview",
            [AiChatId] = "AI Chat",
            [ProjectDescriptionId] = "Project Description",
            [TodoListId] = "Todo List"
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

                // Ensure description panel exists (handles old workspace files that pre-date this feature)
                if (descriptionControl.Parent == null)
                {
                    var descAnchorable = new LayoutAnchorable
                    {
                        Title = "Project Description",
                        ContentId = ProjectDescriptionId,
                        CanClose = false,
                        CanHide = false,
                        CanAutoHide = true,
                        Content = descriptionControl
                    };
                    var anchorGroup = new LayoutAnchorGroup();
                    anchorGroup.Children.Add(descAnchorable);
                    dockingManager.Layout.RightSide.Children.Add(anchorGroup);
                }

                // Ensure todo list panel exists (handles old workspace files that pre-date this feature)
                if (todoListControl.Parent == null)
                {
                    var todoAnchorable = new LayoutAnchorable
                    {
                        Title = "Todo List",
                        ContentId = TodoListId,
                        CanClose = false,
                        CanHide = false,
                        CanAutoHide = true,
                        Content = todoListControl
                    };
                    var anchorGroup = new LayoutAnchorGroup();
                    anchorGroup.Children.Add(todoAnchorable);
                    dockingManager.Layout.RightSide.Children.Add(anchorGroup);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"CreateDockingLayout: failed to restore layout, falling back to default — {ex.Message}");
                // Fall through to build default layout
                BuildDefaultLayout(dockingManager, project, fileExplorerControl, gitStatusControl, filePreviewControl, aiChatControl, descriptionControl, todoListControl);
            }
        }
        else
        {
            // Build default layout
            BuildDefaultLayout(dockingManager, project, fileExplorerControl, gitStatusControl, filePreviewControl, aiChatControl, descriptionControl, todoListControl);
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
        return (dockingManager, aiChatControl, gitStatusControl, descriptionControl, todoListControl);
    }

    private void BuildDefaultLayout(
        DockingManager dockingManager,
        ProjectInfo project,
        FileExplorerControl fileExplorerControl,
        GitStatusControl gitStatusControl,
        FilePreviewControl filePreviewControl,
        AiChatControl aiChatControl,
        ProjectDescriptionControl descriptionControl,
        TodoListControl todoListControl)
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

        // --- Auto-collapsed description panel on right side ---
        var descriptionAnchorable = new LayoutAnchorable
        {
            Title = "Project Description",
            ContentId = ProjectDescriptionId,
            CanClose = false,
            CanHide = false,
            CanAutoHide = true,
            Content = descriptionControl
        };

        var descAnchorGroup = new LayoutAnchorGroup();
        descAnchorGroup.Children.Add(descriptionAnchorable);
        layoutRoot.RightSide.Children.Add(descAnchorGroup);

        // --- Auto-collapsed todo list panel on right side ---
        var todoAnchorable = new LayoutAnchorable
        {
            Title = "Todo List",
            ContentId = TodoListId,
            CanClose = false,
            CanHide = false,
            CanAutoHide = true,
            Content = todoListControl
        };

        var todoAnchorGroup = new LayoutAnchorGroup();
        todoAnchorGroup.Children.Add(todoAnchorable);
        layoutRoot.RightSide.Children.Add(todoAnchorGroup);
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

        // Focus the AI chat input if the session is idle
        if (_projectChatControls.TryGetValue(project, out var chatControl))
            chatControl.FocusInput();
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

    private void UpdateTaskbarIcon()
    {
        var accentBrush = ThemeManager.GetBrush("TaskbarAccentColor");
        Icon = TaskbarIconHelper.CreateThemedIcon(accentBrush.Color);
    }

    private void UpdateTotalCost()
    {
        var totalCost = _projectChatControls.Values.Sum(c => c.Stats.TotalCostUsd);
        var totalTokens = _projectChatControls.Values.Sum(c => c.Stats.TotalTokens);
        if (totalCost > 0)
        {
            var elapsed = DateTime.Now - _appStartTime;
            var elapsedText = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                : $"{elapsed.Minutes}m";
            TotalCostText.Text = $"{elapsedText} · ${totalCost:F4} · {SessionStats.FormatTokens(totalTokens)} tokens";
        }
        else
        {
            TotalCostText.Text = "";
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
        _projectDescriptionControls.Remove(project);
        _projectTodoListControls.Remove(project);

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
            _projectDescriptionControls.Remove(project);
            _projectTodoListControls.Remove(project);
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
            Theme = ThemeManager.CurrentTheme.Id,
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
        ThemeManager.ApplyTheme(ThemeRegistry.Resolve(workspace.Theme));

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

    private readonly List<MenuItem> _recentWorkspaceMenuItems = [];

    private void PopulateRecentWorkspacesMenu()
    {
        // Remove previously inserted recent workspace items from File menu
        foreach (var old in _recentWorkspaceMenuItems)
            FileMenu.Items.Remove(old);
        _recentWorkspaceMenuItems.Clear();

        var recent = WorkspaceManager.GetRecentWorkspaces();
        var shown = recent.Take(5).ToList();

        if (shown.Count == 0)
        {
            RecentWorkspacesSeparator.Visibility = Visibility.Collapsed;
            RecentWorkspacesHeader.Visibility = Visibility.Collapsed;
        }
        else
        {
            RecentWorkspacesSeparator.Visibility = Visibility.Visible;
            RecentWorkspacesHeader.Visibility = Visibility.Visible;

            // Insert recent items between RecentWorkspacesHeader and ExitSeparator
            var insertIndex = FileMenu.Items.IndexOf(ExitSeparator);
            foreach (var path in shown)
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
                FileMenu.Items.Insert(insertIndex, item);
                _recentWorkspaceMenuItems.Add(item);
                insertIndex++;
            }
        }

        // Also refresh the empty state list
        PopulateEmptyStateRecentWorkspaces();
    }

    private void PopulateEmptyStateRecentWorkspaces()
    {
        EmptyStateRecentList.Children.Clear();

        var recent = WorkspaceManager.GetRecentWorkspaces();
        var shown = recent.Take(5).ToList();

        if (shown.Count == 0)
        {
            EmptyStateRecentPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStateRecentPanel.Visibility = Visibility.Visible;

        foreach (var path in shown)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var button = new Button
            {
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                ToolTip = path
            };

            var label = new TextBlock
            {
                Text = name,
                FontSize = 13,
                Foreground = (Brush)FindResource("TabButtonActiveBorderBrush"),
                TextDecorations = TextDecorations.Underline
            };

            button.Content = label;

            var capturedPath = path;
            button.Click += (_, _) => OpenWorkspaceFile(capturedPath);

            EmptyStateRecentList.Children.Add(button);
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
        _projectDescriptionControls.Clear();
    }

    // --- Theme ---

    private void OnThemeChanged(ThemeDescriptor theme)
    {
        UpdateThemeMenuCheckmarks();

        // Update AvalonDock theme on all DockingManagers
        var dockTheme = theme.BaseVariant == ThemeBaseVariant.Dark
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
                // Re-create icon element from settings (handles custom colours + theme default)
                if (sp.Children.Count > 0)
                {
                    var settings = ProjectSettingsManager.Load(project.FolderPath);
                    sp.Children.RemoveAt(0);
                    sp.Children.Insert(0, CreateIconElement(
                        settings.Icon ?? "folder", project.FolderPath,
                        settings.IconColor));
                }

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

        // Update toolbar + button accent
        _toolbarAddButton.BorderBrush = ThemeManager.GetBrush("AddButtonBorderBrush");

        // Update taskbar icon with new theme accent bar
        UpdateTaskbarIcon();
    }

    private void PopulateThemeMenu()
    {
        ThemeMenu.Items.Clear();

        // Dark themes group
        ThemeMenu.Items.Add(new MenuItem
        {
            Header = "Dark Themes",
            IsEnabled = false,
            FontStyle = FontStyles.Italic
        });

        foreach (var theme in ThemeRegistry.All.Where(t => t.BaseVariant == ThemeBaseVariant.Dark))
        {
            var item = new MenuItem
            {
                Header = theme.DisplayName,
                IsCheckable = true,
                IsChecked = ThemeManager.CurrentTheme.Id == theme.Id,
                Tag = theme.Id
            };
            item.Click += ThemeMenuItem_Click;
            ThemeMenu.Items.Add(item);
        }

        ThemeMenu.Items.Add(new Separator());

        // Light themes group
        ThemeMenu.Items.Add(new MenuItem
        {
            Header = "Light Themes",
            IsEnabled = false,
            FontStyle = FontStyles.Italic
        });

        foreach (var theme in ThemeRegistry.All.Where(t => t.BaseVariant == ThemeBaseVariant.Light))
        {
            var item = new MenuItem
            {
                Header = theme.DisplayName,
                IsCheckable = true,
                IsChecked = ThemeManager.CurrentTheme.Id == theme.Id,
                Tag = theme.Id
            };
            item.Click += ThemeMenuItem_Click;
            ThemeMenu.Items.Add(item);
        }
    }

    private void UpdateThemeMenuCheckmarks()
    {
        foreach (var item in ThemeMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string themeId)
                item.IsChecked = ThemeManager.CurrentTheme.Id == themeId;
        }
    }
}
