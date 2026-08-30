using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgentDock.Controls;
using AgentDock.Models;
using AgentDock.Services;
using AgentDock.Services.Abstractions;
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

    /// <summary>Slight top-left/top-right rounding applied to project and group tabs.</summary>
    private const double TabCornerRadius = 5;

    private string _currentToolbarPosition = "Top";

    private readonly List<ProjectInfo> _projects = [];
    private readonly Dictionary<ProjectInfo, Button> _projectTabButtons = [];
    private readonly Dictionary<ProjectInfo, UIElement> _projectContents = [];
    private readonly Dictionary<ProjectInfo, AiChatControl> _projectChatControls = [];
    private readonly Dictionary<ProjectInfo, GitStatusControl> _projectGitControls = [];
    private readonly Dictionary<ProjectInfo, FilePreviewControl> _projectPreviewControls = [];
    private readonly Dictionary<ProjectInfo, FileExplorerControl> _projectExplorerControls = [];
    private readonly Dictionary<ProjectInfo, Grid> _projectTabIcons = [];
    private readonly Dictionary<ProjectInfo, DispatcherTimer> _tabIconTimers = [];
    private readonly Dictionary<ProjectInfo, ClaudeSessionState> _previousTabStates = [];
    private readonly Dictionary<ProjectInfo, DockingManager> _projectDockingManagers = [];
    private readonly Dictionary<ProjectInfo, ProjectDescriptionControl> _projectDescriptionControls = [];
    private readonly Dictionary<ProjectInfo, TodoListControl> _projectTodoListControls = [];
    private ProjectInfo? _activeProject;

    // Tab grouping state (meta tabs)
    private readonly List<ProjectGroup> _groups = [];
    private string? _activeGroupId;

    // Dynamic "Active Projects" group. Not a real ProjectGroup — a virtual meta tab,
    // selected by parking this sentinel in _activeGroupId. Shown on the right of the
    // meta bar (when grouping is in use and the setting is on); it gathers the
    // most-recently-active projects that have a live agent session.
    private const string ActiveProjectsGroupId = "__active_projects__";
    // How many of those projects the group lists. Workspace setting, so a workspace with
    // many long-running sessions can widen it without crowding every other workspace.
    private int _activeProjectsLimit = WorkspaceFile.DefaultActiveProjectsLimit;
    // On by default: the group is meant to be a permanent shortcut to whatever is live,
    // so it shows unless the workspace explicitly turned it off.
    private bool _showActiveProjectsGroup = true;
    // Last time each project's session showed any activity (state change), for MRU
    // ordering of the Active Projects group. Runtime-only — sessions don't survive restart.
    private readonly Dictionary<ProjectInfo, DateTime> _projectLastActivity = [];
    // Group-level status diamond + pulse timer, keyed by group Id (rebuilt with the meta bar)
    private readonly Dictionary<string, Grid> _groupTabIcons = [];
    private readonly Dictionary<string, DispatcherTimer> _groupIconTimers = [];
    // Projects with a completed-but-unseen turn (the flashing-green "new response" state).
    // Drives both the per-tab attention pulse and the group-level indicator.
    private readonly HashSet<ProjectInfo> _projectNewResponse = [];

    // Group tab drag-and-drop reordering state (mirrors the project-tab drag state)
    private Point _groupDragStartPoint;
    private bool _groupDragging;
    private Button? _groupDragSource;
    private Border? _groupDragIndicator;

    // Workspace state
    private string? _currentWorkspacePath;
    private bool _workspaceDirty;
    private bool _suppressDirty; // suppress during workspace load
    private bool _workspaceLoading; // a workspace open is in flight (overlay showing)

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

    // The length of this dependency list is itself the finding: MainWindow does far too
    // much. Phase B extracts these into view-models; for now they are honest, declared
    // dependencies rather than hidden static reach-throughs.
    private readonly ILogService _log;
    private readonly IPerfDiagnostics _perf;
    private readonly IThemeService _theme;
    private readonly IThemeRegistry _themeRegistry;
    private readonly IAppSettingsStore _appSettings;
    private readonly IProjectSettingsStore _projectSettings;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IAccountManager _accounts;
    private readonly IUsageService _usage;
    private readonly ISoundService _sound;
    private readonly ITaskbarIconService _taskbarIcon;
    private readonly IClaudeEnvironment _claudeEnvironment;
    private readonly IClaudeSessionFactory _sessionFactory;
    private readonly IMarkdownRenderer _markdown;
    private readonly IImageAttachmentService _images;
    private readonly IUpdateCheckService _updateCheck;
    private readonly IReleaseNotesService _releaseNotes;
    private readonly Services.Remote.RemoteServerService _remoteServer;
    private readonly Services.Remote.RemoteClientService _remoteClient;

    public MainWindow(
        ILogService log,
        IPerfDiagnostics perf,
        IThemeService theme,
        IThemeRegistry themeRegistry,
        IAppSettingsStore appSettings,
        IProjectSettingsStore projectSettings,
        IWorkspaceStore workspaceStore,
        IAccountManager accounts,
        IUsageService usage,
        ISoundService sound,
        ITaskbarIconService taskbarIcon,
        IClaudeEnvironment claudeEnvironment,
        IClaudeSessionFactory sessionFactory,
        IMarkdownRenderer markdown,
        IImageAttachmentService images,
        IUpdateCheckService updateCheck,
        IReleaseNotesService releaseNotes,
        Services.Remote.RemoteServerService remoteServer,
        Services.Remote.RemoteClientService remoteClient)
    {
        _log = log;
        _perf = perf;
        _theme = theme;
        _themeRegistry = themeRegistry;
        _appSettings = appSettings;
        _projectSettings = projectSettings;
        _workspaceStore = workspaceStore;
        _accounts = accounts;
        _usage = usage;
        _sound = sound;
        _taskbarIcon = taskbarIcon;
        _claudeEnvironment = claudeEnvironment;
        _sessionFactory = sessionFactory;
        _markdown = markdown;
        _images = images;
        _updateCheck = updateCheck;
        _releaseNotes = releaseNotes;
        _remoteServer = remoteServer;
        _remoteClient = remoteClient;

        _log.Info("MainWindow constructor starting");
        InitializeComponent();

        ProductNameText.Text = $"Agent Dock v{App.Version}";

        // Create the + button for the project tab bar
        _toolbarAddButton = CreateToolbarAddButton();
        ToolbarPanel.Children.Add(_toolbarAddButton);

        // ToolbarPanel handles drag-over/drop for tab reordering (provides full-panel hit area)
        ToolbarPanel.AllowDrop = true;
        ToolbarPanel.DragOver += ToolbarPanel_DragOver;
        ToolbarPanel.Drop += ToolbarPanel_Drop;
        ToolbarPanel.DragLeave += (_, _) => RemoveTabDragIndicator();

        // MetaTabPanel is the drop target for moving a project tab into a different group.
        // Each meta tab element also accepts drop individually (set on creation).
        // It also handles GroupTab drags for reordering the groups themselves.
        MetaTabPanel.AllowDrop = true;
        MetaTabPanel.DragOver += MetaTabPanel_DragOver;
        MetaTabPanel.Drop += MetaTabPanel_Drop;
        MetaTabPanel.DragLeave += (_, _) => RemoveGroupDragIndicator();

        CommandBindings.Add(new CommandBinding(AddProjectCommand, (_, _) => AddProject()));
        CommandBindings.Add(new CommandBinding(SaveWorkspaceCommand, (_, _) => SaveWorkspace()));
        CommandBindings.Add(new CommandBinding(CloseProjectCommand, (_, _) => CloseActiveProject()));

        // Subscribe to theme changes
        _theme.ThemeChanged += OnThemeChanged;
        UpdateTaskbarIcon();

        // Sync maximize/restore icon whenever window state changes (button click, double-click, aero snap, etc.).
        // Also grant any process permission to bring us back when we minimize — without this,
        // a long-idle instance whose foreground privilege has expired can't be restored from
        // the taskbar (the click plays a ding and the window stays minimized).
        StateChanged += (_, _) =>
        {
            UpdateMaximizeIcon();
            if (WindowState == WindowState.Minimized)
                AllowSetForegroundWindow(ASFW_ANY);
        };
        UpdateMaximizeIcon();

        // Restore saved toolbar position
        var savedPosition = _appSettings.GetString("ToolbarPosition", "Top");
        if (savedPosition != "Top")
            SetToolbarPosition(savedPosition);

        // Restore saved Claude path override
        var savedClaudePath = _appSettings.GetString("ClaudePath", "");
        if (!string.IsNullOrEmpty(savedClaudePath))
            _claudeEnvironment.BinaryPath = savedClaudePath;

        // Populate recent workspaces menu
        PopulateRecentWorkspacesMenu();

        // Check for startup arguments
        if (App.StartupWorkspacePath != null)
        {
            // Defer to after window is fully loaded
            var startupWorkspace = App.StartupWorkspacePath;
            Loaded += async (_, _) => await OpenWorkspaceFile(startupWorkspace);
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
            ShowWhatsNewIfNewVersion();
            await CheckForAppUpdateAsync();
        };

        // Start fetching Claude Code plan usage for the title bar indicator
        Loaded += (_, _) => InitializeUsageIndicator();

        // Remote roles are wired last so the status strip and menu reflect any state the
        // services already hold.
        InitializeRemote();

        _log.Info("MainWindow constructor complete");
    }

    // --- Maximized window sizing (prevent overflow beyond screen) ---

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // WindowStyle=None strips WS_SYSMENU, which can prevent the shell from
        // activating the window via the taskbar (produces a "ding" instead).
        // Re-adding it fixes taskbar activation while keeping custom chrome.
        const int GWL_STYLE = -16;
        const int WS_SYSMENU = 0x00080000;
        var style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style | WS_SYSMENU);

        var source = HwndSource.FromHwnd(hwnd);
        if (source != null)
        {
            source.AddHook(WndProc);
            _log.Info($"OnSourceInitialized: WndProc hook installed (hwnd=0x{hwnd:X})");
        }
        else
        {
            _log.Warn($"OnSourceInitialized: HwndSource.FromHwnd returned null (hwnd=0x{hwnd:X}) — WndProc hook NOT installed");
        }
    }

    private int _wmGetMinMaxInfoCount;
    private int _wndProcExceptionCount;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_GETMINMAXINFO — constrain maximized size to the work area.
        //
        // Wrapped in try/catch so a managed marshaling failure (bad lParam,
        // unexpected struct layout) is logged before bubbling. A native AV in
        // the OS read/write won't be caught here — that path is what the WER
        // LocalDump configuration is for — but anything inside the CLR will.
        if (msg == 0x0024)
        {
            try
            {
                var n = System.Threading.Interlocked.Increment(ref _wmGetMinMaxInfoCount);
                if (n == 1)
                    _log.Info($"WndProc: first WM_GETMINMAXINFO (hwnd=0x{hwnd:X}, lParam=0x{lParam:X})");

                if (lParam == IntPtr.Zero)
                {
                    _log.Warn($"WndProc: WM_GETMINMAXINFO with null lParam (count={n}) — skipping");
                    return IntPtr.Zero;
                }

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
                else
                {
                    _log.Warn($"WndProc: MonitorFromWindow returned 0 for hwnd=0x{hwnd:X} (count={n}) — using OS defaults");
                }
                // fDeleteOld:false — MINMAXINFO is pure POD (POINTs and ints).
                // No managed pointers in the destination buffer to release.
                Marshal.StructureToPtr(mmi, lParam, false);
                handled = true;
            }
            catch (Exception ex)
            {
                var n = System.Threading.Interlocked.Increment(ref _wndProcExceptionCount);
                _log.Error($"WndProc: managed exception handling WM_GETMINMAXINFO (count={n}, lParam=0x{lParam:X})", ex);
                handled = false;
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int ASFW_ANY = -1;

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

    private async void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        // Swapping the workspace would replace the very sessions a connected client is
        // driving. Blocked rather than resynced: nobody is at this machine while hosting, so
        // the ability buys nothing and costs a whole resync path.
        if (_remoteServer.IsRunning)
        {
            ThemedMessageBox.Show(this,
                "Stop server mode before opening a different workspace.",
                "Server Mode Active");
            return;
        }

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

        await OpenWorkspaceFile(dialog.FileName);
    }

    private void SaveWorkspace_Click(object sender, RoutedEventArgs e) => SaveWorkspace();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // --- Settings Menu ---

    private void WorkspaceSettings_Click(object sender, RoutedEventArgs e)
    {
        var result = Windows.WorkspaceSettingsDialog.Show(
            _themeRegistry,
            this,
            _theme.CurrentTheme.Id,
            _currentToolbarPosition,
            _showActiveProjectsGroup,
            _activeProjectsLimit);

        if (result == null) return;

        if (result.ThemeId != _theme.CurrentTheme.Id)
            _theme.ApplyTheme(result.ThemeId);

        if (result.ToolbarPosition != _currentToolbarPosition)
        {
            SetToolbarPosition(result.ToolbarPosition);
            _appSettings.SetString("ToolbarPosition", result.ToolbarPosition);
        }

        if (result.ShowActiveProjectsGroup != _showActiveProjectsGroup
            || result.ActiveProjectsLimit != _activeProjectsLimit)
        {
            _showActiveProjectsGroup = result.ShowActiveProjectsGroup;
            _activeProjectsLimit = result.ActiveProjectsLimit;

            // Turning it off while it's the current view — fall back to a real group.
            if (!_showActiveProjectsGroup && _activeGroupId == ActiveProjectsGroupId)
            {
                var firstGroup = _groups.OrderBy(g => g.Order).FirstOrDefault();
                _activeGroupId = firstGroup?.Id;
                var firstProject = _activeGroupId != null
                    ? _projects.FirstOrDefault(p => p.GroupId == _activeGroupId)
                    : null;
                if (firstProject != null)
                    SwitchToProject(firstProject);
            }

            RefreshMetaTabBar();
            RefreshProjectTabVisibility();
            SetWorkspaceDirty();
        }
    }

    private void AppSettings_Click(object sender, RoutedEventArgs e)
    {
        var currentClaudePath = _appSettings.GetString("ClaudePath");
        var currentChannelStr = _appSettings.GetString("UpdateChannel", "Stable");
        var currentChannel = Enum.TryParse<UpdateChannel>(currentChannelStr, ignoreCase: true, out var c)
            ? c
            : UpdateChannel.Stable;

        var result = Windows.AppSettingsDialog.Show(
            _themeRegistry,
            _log,
            this,
            _theme.CurrentTheme.Id,
            _currentToolbarPosition,
            currentClaudePath,
            currentChannel);

        if (result == null) return;

        if (result.ThemeId != _theme.CurrentTheme.Id)
            _theme.ApplyTheme(result.ThemeId);

        if (result.ToolbarPosition != _currentToolbarPosition)
        {
            SetToolbarPosition(result.ToolbarPosition);
            _appSettings.SetString("ToolbarPosition", result.ToolbarPosition);
        }

        var newClaudePath = string.IsNullOrEmpty(result.ClaudePath) ? "claude" : result.ClaudePath;
        if (newClaudePath != _claudeEnvironment.BinaryPath)
        {
            _claudeEnvironment.BinaryPath = newClaudePath;
            _appSettings.SetString("ClaudePath", string.IsNullOrEmpty(result.ClaudePath) ? "" : result.ClaudePath);
            _log.Info($"AppSettings: ClaudePath set to '{newClaudePath}'");
        }

        if (result.UpdateChannel != currentChannel)
        {
            _appSettings.SetString("UpdateChannel", result.UpdateChannel.ToString());
            _log.Info($"AppSettings: UpdateChannel set to '{result.UpdateChannel}'");
        }
    }

    private void Accounts_Click(object sender, RoutedEventArgs e)
    {
        Windows.AccountsDialog.Show(this, _accounts);
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

    private List<(string Name, bool Found, string Detail)> RunPrerequisiteChecks()
    {
        // Log environment info for diagnostics
        _log.Info($"Prereq: OS={Environment.OSVersion}, .NET={Environment.Version}, 64-bit={Environment.Is64BitProcess}");
        _log.Info($"Prereq: OSDescription='{System.Runtime.InteropServices.RuntimeInformation.OSDescription}', " +
                 $"Framework='{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}', " +
                 $"Arch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        _log.Info($"Prereq: User={Environment.UserName}, Machine={Environment.MachineName}");

        var results = new List<(string Name, bool Found, string Detail)>();

        // Command-line tools — each gets: name, found/not-found, resolved path, version
        var cliChecks = new (string Name, string Command, string Args)[]
        {
            ("Claude Code CLI", _claudeEnvironment.BinaryPath, "--version"),
            ("Git", "git", "--version"),
            ("VS Code", "code", "--version"),
            ("Cursor", "cursor", "--version"),
        };

        foreach (var (name, command, args) in cliChecks)
        {
            var (found, version) = CheckCommandVersion(command, args);
            var resolvedPath = ResolveCommandPath(command);
            if (found)
                _log.Info($"Prereq: {name} — FOUND, path='{resolvedPath}', version='{version}'");
            else
                _log.Info($"Prereq: {name} — NOT FOUND (searched PATH for '{command}')");
            results.Add((name, found, version));
        }

        // Claude-specific extras: log all matches on PATH and any custom override
        LogClaudePathDetails();

        // Visual Studio — check install directories
        var vsResult = FindVisualStudio();
        if (vsResult.Found)
            _log.Info($"Prereq: {vsResult.Name} — FOUND, {vsResult.Detail}");
        else
            _log.Info($"Prereq: {vsResult.Name} — NOT FOUND");
        results.Add(vsResult);

        return results;
    }

    /// <summary>
    /// Resolves a command name to its full path on the system PATH.
    /// Returns the first match (preferring .cmd/.bat for npm wrappers), or the bare name if not found.
    /// </summary>
    private string ResolveCommandPath(string command)
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
    private void LogClaudePathDetails()
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
            _log.Info($"Prereq: Claude — all matches on PATH ({found.Count}): {string.Join(", ", found)}");

        if (_claudeEnvironment.BinaryPath != "claude")
            _log.Info($"Prereq: Claude — custom path override = '{_claudeEnvironment.BinaryPath}'");
    }

    /// <summary>
    /// Runs a command via cmd.exe /c to get its version output.
    /// Returns (found, version-string).
    /// </summary>
    private (bool Found, string Version) CheckCommandVersion(string command, string args)
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

    private (string Name, bool Found, string Detail) FindVisualStudio()
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
        _log.Info("Startup prerequisite check: starting");
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
        _log.Info("Startup prerequisite check: complete");
    }

    private const string LastWhatsNewVersionKey = "LastWhatsNewVersionShown";

    /// <summary>
    /// If the app version has changed since the last time we showed the "What's New" popup,
    /// show release notes for the new version. On first install (no prior key), records the
    /// current version silently without showing the popup.
    /// </summary>
    private void ShowWhatsNewIfNewVersion()
    {
        try
        {
            var currentVersion = App.Version;
            var lastShown = _appSettings.GetString(LastWhatsNewVersionKey, "");

            if (string.IsNullOrEmpty(lastShown))
            {
                _log.Info($"WhatsNew: first install, recording v{currentVersion} silently");
                _appSettings.SetString(LastWhatsNewVersionKey, currentVersion);
                return;
            }

            if (lastShown == currentVersion)
                return;

            var notes = _releaseNotes.GetNotesForVersion(currentVersion);
            if (string.IsNullOrWhiteSpace(notes))
            {
                _log.Info($"WhatsNew: no embedded notes for v{currentVersion}, skipping popup");
                _appSettings.SetString(LastWhatsNewVersionKey, currentVersion);
                return;
            }

            _log.Info($"WhatsNew: showing release notes for v{currentVersion} (was v{lastShown})");
            var window = new WhatsNewWindow(this, currentVersion, notes, _markdown);
            window.ShowDialog();

            _appSettings.SetString(LastWhatsNewVersionKey, currentVersion);
        }
        catch (Exception ex)
        {
            _log.Warn($"WhatsNew: failed to show popup — {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task CheckForAppUpdateAsync()
    {
#if DEBUG
        _log.Info("UpdateCheck: skipping update check in Debug build");
        return;
#else
        var channelStr = _appSettings.GetString("UpdateChannel", "Stable");
        var channel = Enum.TryParse<UpdateChannel>(channelStr, ignoreCase: true, out var c)
            ? c
            : UpdateChannel.Stable;
        var updateInfo = await System.Threading.Tasks.Task.Run(() => _updateCheck.CheckForUpdateAsync(channel));
        if (updateInfo == null)
            return;

        _log.Info($"Update available: v{updateInfo.Version}");
        var dialog = new UpdateDialog(this, updateInfo, _markdown, _updateCheck);
        dialog.ShowDialog();
#endif
    }

    // --- Project Management ---

    private void AddProject()
    {
        _log.Info("AddProject: opening folder dialog");
        var dialog = new OpenFolderDialog
        {
            Title = "Select Project Folder"
        };

        if (dialog.ShowDialog() != true)
        {
            _log.Info("AddProject: dialog cancelled");
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
        _log.Info($"AddProjectFromPath: '{folderPath}'");

        // Prevent duplicates
        if (_projects.Any(p => string.Equals(p.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            _log.Warn($"AddProjectFromPath: duplicate folder '{folderPath}'");
            var existing = _projects.First(p =>
                string.Equals(p.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));
            SwitchToProject(existing);
            return existing;
        }

        if (!Directory.Exists(folderPath))
        {
            _log.Warn($"AddProjectFromPath: folder does not exist '{folderPath}'");
            ThemedMessageBox.Show(
                this,
                $"The folder '{folderPath}' does not exist.",
                "Folder Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        var project = new ProjectInfo { FolderPath = folderPath };
        project.CustomName = _projectSettings.Load(folderPath).Name;

        // If groups are active, the new project joins whichever group is currently visible.
        // The Active Projects tab is a virtual view over live sessions, not a real group, so
        // adding while it's selected has to resolve to a real group — stamping its sentinel id
        // onto the project would leave the tab in no group at all.
        if (_groups.Count > 0 && _activeGroupId != null)
            project.GroupId = _activeGroupId == ActiveProjectsGroupId
                ? GroupBehindActiveProjectsTab()
                : _activeGroupId;

        return AddProjectCore(project, layoutXml);
    }

    /// <summary>
    /// Registers a project that already exists as a <see cref="ProjectInfo"/> and builds its tab
    /// and panels. Split out of <see cref="AddProjectFromPath"/> so a remote project — whose
    /// folder is on another machine and therefore fails every local existence check — can reuse
    /// the same tab construction rather than duplicating it.
    /// </summary>
    private ProjectInfo? AddProjectCore(ProjectInfo project, string? layoutXml)
    {
        _projects.Add(project);
        _log.Info($"AddProjectFromPath: created ProjectInfo for '{project.FolderName}'");

        // Create the tab button
        var tabButton = CreateProjectTabButton(project);
        _projectTabButtons[project] = tabButton;

        // Add tab button to toolbar (before the + button, which is always last)
        var addBtnIdx = ToolbarPanel.Children.IndexOf(_toolbarAddButton);
        if (addBtnIdx >= 0)
            ToolbarPanel.Children.Insert(addBtnIdx, tabButton);
        else
            ToolbarPanel.Children.Add(tabButton);
        UpdateProjectStripVisibility();
        // Reopening the first project after closing them all brings the strip back.
        UpdateGroupStripVisibility();

        // Create docking layout for this project
        var (content, chatControl, gitControl, descControl, todoControl) = CreateProjectDockingLayout(project, layoutXml);
        _projectContents[project] = content;
        _projectChatControls[project] = chatControl;
        _projectGitControls[project] = gitControl;
        _projectDescriptionControls[project] = descControl;
        _projectTodoListControls[project] = todoControl;
        chatControl.SessionStateChanged += state => UpdateTabIcon(project, state);
        // A session starting or ending changes which tabs a connected client should see.
        chatControl.SessionStateChanged += _ => ActiveProjectsChanged?.Invoke();
        // A scheduled message changes the tab's icon/tooltip even when the session
        // state itself doesn't move (e.g. Idle → Idle-with-schedule), so refresh on it.
        chatControl.ScheduleChanged += () => UpdateTabIcon(project, chatControl.CurrentState);
        // Background work (subagents / background tasks) can start or finish while the
        // session sits at Idle, without a state change — refresh the diamond directly so
        // it pulses "still busy" rather than showing a solid-green "available".
        chatControl.SessionBackgroundWorkChanged += () => RefreshBackgroundWorkVisual(project);

        // Switch to the new project
        SwitchToProject(project);

        SetWorkspaceDirty();
        _log.Info("AddProjectFromPath: complete");
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

    private string? FindProjectLogo(string folderPath)
        => FindAllProjectLogos(folderPath).FirstOrDefault();

    /// <summary>
    /// Resolves the project icon: reads from .agentdock/settings.json,
    /// auto-discovers if not set, persists the result, and creates the UI element.
    /// </summary>
    private UIElement ResolveProjectIcon(string folderPath)
    {
        var settings = _projectSettings.Load(folderPath);

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

            _projectSettings.Save(folderPath, settings);
        }

        return CreateIconElement(settings.Icon, folderPath, settings.IconColor);
    }

    /// <summary>
    /// Creates a UIElement for the given icon value (built-in name or file path).
    /// </summary>
    private UIElement CreateIconElement(string icon, string folderPath,
        string? iconColor = null)
    {
        // Check if it's a built-in icon name
        var builtIn = BuiltInIcons.Find(icon);
        if (builtIn != null)
        {
            var foreground = iconColor != null
                ? ParseHexBrush(iconColor) ?? _theme.GetBrush("TabIconNoSessionForeground")
                : _theme.GetBrush("TabIconNoSessionForeground");

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
                _log.Warn($"Failed to load project icon '{filePath}': {ex.Message}");
            }
        }

        // Fallback to default folder icon
        var fallback = BuiltInIcons.Default;
        return new TextBlock
        {
            Text = fallback.Glyph,
            FontFamily = new FontFamily(fallback.FontFamily),
            FontSize = 14,
            Foreground = _theme.GetBrush("TabIconNoSessionForeground"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
    }

    /// <summary>
    /// Parses a hex colour string (#RRGGBB) into a SolidColorBrush, or null if invalid.
    /// </summary>
    private SolidColorBrush? ParseHexBrush(string hex)
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

    /// <summary>
    /// Brush for the tab's own segment of the strip's accent rail, along the edge facing
    /// the content. Inactive tabs paint it in the accent so the strip rail reads as one
    /// unbroken line; the current tab leaves it null, so the rail stops either side of it
    /// and the tab reads as joined to everything below.
    /// </summary>
    public static readonly DependencyProperty TabRailBrushProperty =
        DependencyProperty.RegisterAttached(
            "TabRailBrush", typeof(Brush), typeof(MainWindow), new PropertyMetadata(null));

    public static void SetTabRailBrush(DependencyObject target, Brush? value)
        => target.SetValue(TabRailBrushProperty, value);

    public static Brush? GetTabRailBrush(DependencyObject target)
        => (Brush?)target.GetValue(TabRailBrushProperty);

    /// <summary>
    /// Which edge that rail segment sits on — always the one facing the content, so it
    /// lines up with the strip rail. Bottom for group tabs and a top-docked project
    /// strip; the other three positions are set in <see cref="UpdateProjectFrame"/>.
    /// </summary>
    public static readonly DependencyProperty TabRailThicknessProperty =
        DependencyProperty.RegisterAttached(
            "TabRailThickness", typeof(Thickness), typeof(MainWindow),
            new PropertyMetadata(new Thickness(0, 0, 0, 1)));

    public static void SetTabRailThickness(DependencyObject target, Thickness value)
        => target.SetValue(TabRailThicknessProperty, value);

    public static Thickness GetTabRailThickness(DependencyObject target)
        => (Thickness)target.GetValue(TabRailThicknessProperty);

    /// <summary>
    /// Builds the flat tab <see cref="ControlTemplate"/> shared by project and group tabs.
    /// The "Bd" border carries the TemplateBound background and outline
    /// (BorderBrush/Thickness) plus a top-only corner radius, so every tab — active or
    /// not — reads as a tab with curved top corners. Neighbouring tabs are separated by
    /// those outlines meeting, so no extra divider is drawn. Over the top of that sits
    /// "TabRail": the tab's own slice of the strip's accent rail, which the tab paints
    /// only while it isn't the current one.
    /// </summary>
    private ControlTemplate CreateTabControlTemplate()
    {
        var template = new ControlTemplate(typeof(Button));

        var root = new FrameworkElementFactory(typeof(Grid));

        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(TabCornerRadius, TabCornerRadius, 0, 0));

        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentPresenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(PaddingProperty));
        borderFactory.AppendChild(contentPresenter);
        root.AppendChild(borderFactory);

        // The strip's rail is drawn behind the tabs, so an opaque tab hides the slice
        // running under it. This edge-only border hands that slice back — except on the
        // current tab, where the brush is null and the gap is the whole point.
        var rail = new FrameworkElementFactory(typeof(Border), "TabRail");
        rail.SetValue(UIElement.IsHitTestVisibleProperty, false);
        rail.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        rail.SetBinding(Border.BorderBrushProperty, new Binding
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
            Path = new PropertyPath(TabRailBrushProperty)
        });
        rail.SetBinding(Border.BorderThicknessProperty, new Binding
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
            Path = new PropertyPath(TabRailThicknessProperty)
        });
        root.AppendChild(rail);

        template.VisualTree = root;
        return template;
    }

    /// <summary>
    /// Project and group tabs are outlined on three sides (left/top/right) rather than
    /// underlined: the strip's accent rail supplies the fourth side, so the active tab's
    /// accent outline closes into a box, while inactive tabs keep the same thickness in
    /// the divider brush and separate from their neighbours.
    /// </summary>
    private readonly Thickness TabOutlineThickness = new(1, 1, 1, 0);

    /// <summary>Outline brush for a tab that isn't the current one, in either strip.</summary>
    private Brush InactiveTabOutlineBrush() => _theme.GetBrush("ToolbarBorderBrush");

    /// <summary>
    /// The current project tab takes the strip's own background, so tab and strip read as
    /// one surface carrying the project accent — the same relationship the group strip has
    /// with the project strip below it.
    /// </summary>
    private Brush ActiveProjectTabBrush() => _theme.GetBrush("ToolbarBackground");

    /// <summary>
    /// Project tabs that aren't current sit a shade darker than the strip, so they read as
    /// recessed rather than bleeding into the strip (and the content area) around them.
    /// </summary>
    private Brush InactiveProjectTabBrush() => _theme.GetBrush("ProjectTabInactiveBackground");

    private Button CreateProjectTabButton(ProjectInfo project)
    {
        var iconElement = ResolveProjectIcon(project.FolderPath);

        // Status diamond area (right of name): diamond + error badge
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
            Foreground = _theme.GetBrush("TabIconInactiveDiamondForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
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
        statusGrid.Children.Add(statusBadge);

        _projectTabIcons[project] = statusGrid;

        // Flat tab with curved top corners, outlined on three sides: the divider brush
        // when inactive, the project accent when active (see SetTabButtonActive).
        var tabTemplate = CreateTabControlTemplate();

        var button = new Button
        {
            MinWidth = 44,
            Height = 32,
            Margin = new Thickness(0),
            Padding = new Thickness(12, 0, 12, 0),
            Background = InactiveProjectTabBrush(),
            BorderBrush = InactiveTabOutlineBrush(),
            BorderThickness = TabOutlineThickness,
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
                        Text = project.DisplayName,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = _theme.GetBrush("TabButtonForeground")
                    },
                    statusGrid
                }
            }
        };

        // Carries the strip's accent rail across its own content-facing edge while it
        // isn't the current tab; SetTabButtonActive drops it when it becomes current.
        SetTabRailBrush(button, _theme.GetBrush("ProjectTabActiveBorderBrush"));
        SetTabRailThickness(button, ProjectTabRailThickness());

        button.Click += (_, _) =>
        {
            if (!_tabDragging)
                SwitchToProject(project);
        };

        button.MouseEnter += (_, _) =>
        {
            if (project != _activeProject)
                button.Background = MakeTabHoverBrush();
        };
        button.MouseLeave += (_, _) =>
        {
            if (project != _activeProject)
                button.Background = InactiveProjectTabBrush();
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

        // Right-click context menu — mirrors the File Explorer toolbar actions plus
        // group/close management.
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Project Settings…", () => OpenProjectSettings(project)));
        menu.Items.Add(new Separator());
        if (FileExplorerControl.AvailableTools.Contains("VS Code"))
            menu.Items.Add(CreateMenuItem("Open in VS Code", () => LaunchEditor("code", project)));
        if (FileExplorerControl.AvailableTools.Contains("Cursor"))
            menu.Items.Add(CreateMenuItem("Open in Cursor", () => LaunchEditor("cursor", project)));
        menu.Items.Add(CreateMenuItem("Open in Explorer", () => OpenInExplorer(project)));
        menu.Items.Add(CreateMenuItem("Open in Console", () => OpenInConsole(project)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Add to new group", () => AddProjectToNewGroup(project)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Close Project", () => CloseProject(project)));
        button.ContextMenu = menu;

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
            // Inset from the strip's 32px height, so its closed rounded border clears
            // the project accent rail along the strip's content-facing edge.
            Width = 26,
            Height = 26,
            Margin = new Thickness(6, 3, 3, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = _theme.GetBrush("AddButtonBorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Add Project (Ctrl+N)",
            Template = template,
            Content = new TextBlock
            {
                Text = "+",
                FontSize = 18,
                FontWeight = FontWeights.Light,
                Foreground = _theme.GetBrush("AddButtonForeground"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        button.Click += (_, _) => AddProject();

        button.MouseEnter += (_, _) =>
        {
            button.BorderBrush = _theme.GetBrush("TabButtonHoverBorderBrush");
        };
        button.MouseLeave += (_, _) =>
        {
            button.BorderBrush = _theme.GetBrush("AddButtonBorderBrush");
        };

        return button;
    }

    // --- Tab drag-and-drop (panel-level handlers) ---

    /// <summary>
    /// Finds the insertion index among tab buttons for the given mouse position.
    /// Returns the index in _projects where the dragged tab should be inserted.
    /// Hidden (group-filtered) tabs are skipped so reordering operates only on what's visible.
    /// </summary>
    private int GetTabInsertionIndex(DragEventArgs e)
    {
        var isHorizontal = ToolbarPanel.Orientation == Orientation.Horizontal;
        ProjectInfo? lastVisibleBefore = null;

        for (int i = 0; i < ToolbarPanel.Children.Count; i++)
        {
            if (ToolbarPanel.Children[i] is not FrameworkElement child)
                continue;
            if (child == _toolbarAddButton || child == _tabDragIndicator || child.Tag is not ProjectInfo proj)
                continue;
            if (child.Visibility != Visibility.Visible)
                continue;

            var pos = e.GetPosition(child);
            var midpoint = isHorizontal ? child.RenderSize.Width / 2 : child.RenderSize.Height / 2;
            var coord = isHorizontal ? pos.X : pos.Y;

            if (coord < midpoint)
                return _projects.IndexOf(proj);

            lastVisibleBefore = proj;
        }

        // After all visible tabs — insert immediately after the last one so hidden tabs
        // keep their relative position in the underlying list.
        return lastVisibleBefore != null
            ? _projects.IndexOf(lastVisibleBefore) + 1
            : _projects.Count;
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
                Background = _theme.GetBrush("TabButtonActiveBorderBrush"),
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

    // --- Group (meta tab) reordering via drag-and-drop ---

    private List<ProjectGroup> OrderedGroups() => _groups.OrderBy(g => g.Order).ToList();

    /// <summary>
    /// Finds the insertion index (into the visually-ordered group list) for the given
    /// mouse position over the meta tab strip.
    /// </summary>
    private int GetGroupInsertionIndex(DragEventArgs e)
    {
        var ordered = OrderedGroups();
        ProjectGroup? lastBefore = null;

        foreach (var item in MetaTabPanel.Children)
        {
            if (item is not FrameworkElement child || child == _groupDragIndicator ||
                child.Tag is not ProjectGroup g)
                continue;

            var pos = e.GetPosition(child);
            if (pos.X < child.RenderSize.Width / 2)
                return ordered.IndexOf(g);

            lastBefore = g;
        }

        return lastBefore != null ? ordered.IndexOf(lastBefore) + 1 : ordered.Count;
    }

    private void MetaTabPanel_DragOver(object sender, DragEventArgs e)
    {
        // Only reorder gestures (GroupTab); project-into-group drops are handled per-button.
        if (!e.Data.GetDataPresent("GroupTab"))
            return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ShowGroupDragIndicator(GetGroupInsertionIndex(e));
    }

    private void MetaTabPanel_Drop(object sender, DragEventArgs e)
    {
        RemoveGroupDragIndicator();

        if (e.Data.GetData("GroupTab") is not ProjectGroup source)
            return;

        var ordered = OrderedGroups();
        var sourceIdx = ordered.IndexOf(source);
        var targetIdx = GetGroupInsertionIndex(e);
        if (sourceIdx < 0 || targetIdx < 0)
            return;

        if (targetIdx > sourceIdx)
            targetIdx--;
        if (targetIdx == sourceIdx)
            return;

        ordered.RemoveAt(sourceIdx);
        ordered.Insert(targetIdx, source);

        // Renumber Order to match the new visual sequence (persisted with the workspace).
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Order = i;

        RefreshMetaTabBar();
        SetWorkspaceDirty();
        e.Handled = true;
    }

    private void ShowGroupDragIndicator(int groupIndex)
    {
        _groupDragIndicator ??= new Border
        {
            Background = _theme.GetBrush("TabButtonActiveBorderBrush"),
            IsHitTestVisible = false,
            Width = 3,
            Height = 28,
            Margin = new Thickness(-1, 2, -1, 2)
        };

        MetaTabPanel.Children.Remove(_groupDragIndicator);
        var uiIdx = Math.Min(groupIndex, MetaTabPanel.Children.Count);
        MetaTabPanel.Children.Insert(uiIdx, _groupDragIndicator);
    }

    private void RemoveGroupDragIndicator()
    {
        if (_groupDragIndicator != null)
        {
            MetaTabPanel.Children.Remove(_groupDragIndicator);
            _groupDragIndicator = null;
        }
    }

    private MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // --- Tab groups (meta tabs) ---

    /// <summary>
    /// Right-click "Add to new group" action. On first use, splits all existing tabs
    /// into an implicit "Ungrouped" group plus a new group containing the right-clicked tab.
    /// On subsequent uses, just creates the next numbered group.
    /// </summary>
    private void AddProjectToNewGroup(ProjectInfo project)
    {
        // First-time: also create the "Ungrouped" bucket for everything else
        if (_groups.Count == 0)
        {
            var ungrouped = new ProjectGroup
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Ungrouped",
                Order = 0
            };
            _groups.Add(ungrouped);
            foreach (var p in _projects)
                p.GroupId = ungrouped.Id;
        }

        // Create the new group with an auto-name
        var newGroup = new ProjectGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = NextGroupName(),
            Order = _groups.Count == 0 ? 1 : _groups.Max(g => g.Order) + 1
        };
        _groups.Add(newGroup);
        project.GroupId = newGroup.Id;

        _activeGroupId = newGroup.Id;
        RefreshMetaTabBar();
        RefreshProjectTabVisibility();

        // Ensure the active project is one that's visible in the new group
        if (_activeProject != project)
            SwitchToProject(project);

        SetWorkspaceDirty();
    }

    private string NextGroupName()
    {
        // Find the smallest unused "Group N"
        var existing = new HashSet<string>(_groups.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; n < 1000; n++)
        {
            var candidate = $"Group {n}";
            if (!existing.Contains(candidate))
                return candidate;
        }
        return "Group";
    }

    /// <summary>
    /// Rebuilds <see cref="MetaTabPanel"/> from <see cref="_groups"/>.
    /// Hides the meta bar when fewer than two groups exist.
    /// </summary>
    private void RefreshMetaTabBar()
    {
        MetaTabPanel.Children.Clear();

        // Stop and drop any group pulse timers; the bar (and its diamonds) is rebuilt below.
        foreach (var timer in _groupIconTimers.Values)
            timer.Stop();
        _groupIconTimers.Clear();
        _groupTabIcons.Clear();

        // A remote session always shows its group tab, even though it is the only group: the
        // name is how the user knows which machine they are driving, which matters more here
        // than the tidiness of hiding a single-group bar.
        if (_groups.Count < 2 && !IsRemoteSession)
        {
            ActiveGroupHost.Content = null;
            _activeGroupButton = null;
            UpdateGroupStripVisibility();
            return;
        }

        UpdateGroupStripVisibility();

        foreach (var group in _groups.OrderBy(g => g.Order))
            MetaTabPanel.Children.Add(CreateMetaTabElement(group));

        // Trailing "+" to create a new empty group (mirrors the project-tab add button).
        MetaTabPanel.Children.Add(CreateMetaTabAddButton());

        // Apply the aggregate child indicator to each freshly-built group diamond.
        foreach (var group in _groups)
            RefreshGroupIndicator(group.Id);

        // The dynamic "Active Projects" group sits apart on the right.
        RenderActiveGroupButton();
    }

    /// <summary>
    /// Shows the group strip only when there are at least two groups and at least one
    /// open project. With every project closed the workspace falls back to the empty
    /// state, where a group strip (and the frame it anchors) would have nothing to own.
    /// </summary>
    private void UpdateGroupStripVisibility()
    {
        MetaTabBorder.Visibility = (_groups.Count >= 2 || IsRemoteSession) && _projects.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateGroupFrame();
        // The project strip's top gap only applies while the group strip is showing.
        UpdateProjectFrame();
    }

    /// <summary>
    /// Keeps the accent frame in step with the group strip. When the strip is showing,
    /// the rail along its bottom edge is continued down the left of the workspace,
    /// across the bottom and back up the right, so the whole visible area reads as
    /// belonging to the active group. With no strip there's nothing to frame.
    /// </summary>
    private void UpdateGroupFrame()
    {
        var framed = MetaTabBorder.Visibility == Visibility.Visible;
        GroupFrameBorder.BorderThickness = framed ? new Thickness(1, 0, 1, 1) : new Thickness(0);
        ToolbarBorder.BorderThickness = ToolbarRuleThickness();
    }

    /// <summary>
    /// The project strip's own plain rule. The edge facing the content area is left to
    /// the project accent rail (see <see cref="UpdateProjectFrame"/>), and a top-docked
    /// strip drops its outer rule when the group strip's accent rail already sits
    /// against it. Every other dock position butts against the window edge.
    /// </summary>
    private Thickness ToolbarRuleThickness()
    {
        if (_currentToolbarPosition != "Top")
            return new Thickness(0);

        var railAbove = MetaTabBorder.Visibility == Visibility.Visible;
        return new Thickness(0, railAbove ? 0 : 1, 0, 0);
    }

    /// <summary>
    /// Shows the project tab strip only while projects are open, and keeps the project
    /// accent frame in step with it.
    /// </summary>
    private void UpdateProjectStripVisibility()
    {
        ToolbarBorder.Visibility = _projects.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProjectFrame();
    }

    /// <summary>Vertical breathing room between the group tabs and the project tabs.</summary>
    private const double StripGap = 4;

    /// <summary>
    /// The edge a project tab draws its rail slice on: the same one the strip's rail
    /// runs along, which is whichever side of the strip faces the content area.
    /// </summary>
    private Thickness ProjectTabRailThickness() => _currentToolbarPosition switch
    {
        "Bottom" => new Thickness(0, 1, 0, 0),
        "Left" => new Thickness(0, 0, 1, 0),
        "Right" => new Thickness(1, 0, 0, 0),
        _ => new Thickness(0, 0, 0, 1),
    };

    /// <summary>
    /// Routes the project accent along the tab strip's content-facing edge and around
    /// the remaining three sides of the content area, so the active project reads as
    /// owning everything below its tab — the same trick the group strip uses one level
    /// out, in a different colour. Zero thickness whenever the strip is hidden.
    /// </summary>
    private void UpdateProjectFrame()
    {
        var framed = ToolbarBorder.Visibility == Visibility.Visible;

        // Rail hugs the strip edge that faces the content; the frame covers the rest.
        var (railWidth, railHeight, railH, railV, frame) = _currentToolbarPosition switch
        {
            "Bottom" => (double.NaN, 1.0, HorizontalAlignment.Stretch, VerticalAlignment.Top,
                         new Thickness(1, 1, 1, 0)),
            "Left" => (1.0, double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch,
                       new Thickness(0, 1, 1, 1)),
            "Right" => (1.0, double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch,
                        new Thickness(1, 1, 0, 1)),
            _ => (double.NaN, 1.0, HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
                  new Thickness(1, 0, 1, 1)),
        };

        ProjectBarAccentLine.Width = railWidth;
        ProjectBarAccentLine.Height = railHeight;
        ProjectBarAccentLine.HorizontalAlignment = railH;
        ProjectBarAccentLine.VerticalAlignment = railV;
        ProjectFrameBorder.BorderThickness = framed ? frame : new Thickness(0);

        // Each tab paints its own slice of that rail, so move those onto the new edge too.
        var tabRail = ProjectTabRailThickness();
        foreach (var child in ToolbarPanel.Children)
        {
            if (child is Button tab && tab.Tag is ProjectInfo)
                SetTabRailThickness(tab, tabRail);
        }

        // Breathing room under the group strip: every dock position but Bottom puts the
        // project tabs directly beneath the group tabs, where the two rows of tabs would
        // otherwise meet edge to edge. The gap shows the project strip's background, so
        // the current tab still reads as continuous with the strip.
        var underGroupStrip = MetaTabBorder.Visibility == Visibility.Visible
                              && _currentToolbarPosition != "Bottom";
        ToolbarPanel.Margin = new Thickness(0, underGroupStrip ? StripGap : 0, 0, 0);
    }

    /// <summary>
    /// Hides project tabs that aren't in the active group when grouping is in use.
    /// Shows every tab otherwise. The dynamic Active Projects group shows the
    /// most-recently-active projects with a live session (plus the current tab, so
    /// it never vanishes under the user while they're viewing it).
    /// </summary>
    private void RefreshProjectTabVisibility()
    {
        if (_activeGroupId == ActiveProjectsGroupId)
        {
            var active = GetActiveProjects();
            foreach (var (project, button) in _projectTabButtons)
            {
                button.Visibility = active.Contains(project) || project == _activeProject
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            return;
        }

        var filtering = _groups.Count >= 2 && _activeGroupId != null;

        foreach (var (project, button) in _projectTabButtons)
        {
            button.Visibility = !filtering || project.GroupId == _activeGroupId
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    // --- Dynamic "Active Projects" group ---

    private Button? _activeGroupButton;

    /// <summary>True when the project has a live agent session (started, not exited).</summary>
    private bool IsProjectActive(ProjectInfo project) =>
        _projectChatControls.TryGetValue(project, out var chat)
        && chat.CurrentState is not (ClaudeSessionState.NotStarted or ClaudeSessionState.Exited);

    /// <summary>
    /// The projects shown in the dynamic Active Projects group: those with a live
    /// session, most-recently-active first, capped at <see cref="_activeProjectsLimit"/>.
    /// </summary>
    private List<ProjectInfo> GetActiveProjects() =>
        _projects
            .Where(IsProjectActive)
            .OrderByDescending(p => _projectLastActivity.TryGetValue(p, out var t) ? t : DateTime.MinValue)
            .Take(_activeProjectsLimit)
            .ToList();

    /// <summary>
    /// The real group standing in for the Active Projects meta tab when a project has to be
    /// filed somewhere: the group of the project currently on screen, falling back to the first
    /// group. <see cref="ActiveProjectsGroupId"/> is a view, not a group, and must never reach
    /// <see cref="ProjectInfo.GroupId"/>.
    /// </summary>
    private string? GroupBehindActiveProjectsTab() =>
        _activeProject?.GroupId is { } id && _groups.Any(g => g.Id == id)
            ? id
            : _groups.OrderBy(g => g.Order).FirstOrDefault()?.Id;

    /// <summary>
    /// Rebuilds the Active Projects button into its host, or clears it when grouping
    /// isn't in use or the setting is off.
    /// </summary>
    private void RenderActiveGroupButton()
    {
        if (_groups.Count < 2 || !_showActiveProjectsGroup)
        {
            ActiveGroupHost.Content = null;
            _activeGroupButton = null;
            _groupTabIcons.Remove(ActiveProjectsGroupId);
            return;
        }

        _activeGroupButton = CreateActiveGroupElement();
        ActiveGroupHost.Content = _activeGroupButton;
        RefreshActiveGroup();
    }

    private Button CreateActiveGroupElement()
    {
        var template = CreateTabControlTemplate();
        var accent = _theme.GetBrush("ActiveGroupAccentForeground");

        var builtIn = BuiltInIcons.Find("bolt") ?? BuiltInIcons.Default;
        var iconElement = new TextBlock
        {
            Text = builtIn.Glyph,
            FontFamily = new FontFamily(builtIn.FontFamily),
            FontSize = 14,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };

        var label = new TextBlock
        {
            Text = "Active",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = accent
        };

        // Aggregate status diamond, reusing the group-indicator machinery (keyed by the sentinel).
        var statusGrid = new Grid
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var diamondIcon = new TextBlock
        {
            Text = "◇",
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 14,
            Foreground = _theme.GetBrush("TabIconInactiveDiamondForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusBadge = new TextBlock
        {
            Text = "",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };
        statusGrid.Children.Add(diamondIcon);
        statusGrid.Children.Add(statusBadge);
        _groupTabIcons[ActiveProjectsGroupId] = statusGrid;

        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { iconElement, label, statusGrid }
        };

        var isActive = _activeGroupId == ActiveProjectsGroupId;

        var button = new Button
        {
            MinWidth = 44,
            Height = 32,
            Margin = new Thickness(0),
            Padding = new Thickness(12, 0, 12, 0),
            Background = isActive
                ? _theme.GetBrush("GroupTabActiveBackground")
                : _theme.GetBrush("TabButtonInactiveBackground"),
            // Same three-sided outline as the real group tabs. Its magenta bolt/label
            // are what set it apart; an underline here would break the accent rail that
            // runs along the strip.
            BorderBrush = isActive
                ? _theme.GetBrush("TabButtonActiveBorderBrush")
                : InactiveTabOutlineBrush(),
            BorderThickness = TabOutlineThickness,
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Template = template,
            Content = contentPanel,
            ToolTip = "Active Projects — projects with a live agent session, most recent first"
        };

        SetTabRailBrush(button, isActive ? null : _theme.GetBrush("TabButtonActiveBorderBrush"));

        button.Click += (_, _) =>
        {
            // Re-clicking while already selected re-prunes any tab that has since gone idle-away.
            if (_activeGroupId == ActiveProjectsGroupId)
            {
                RefreshProjectTabVisibility();
                return;
            }
            if (GetActiveProjects().Count == 0)
                return; // disabled/empty — nothing to show
            SetActiveGroup(ActiveProjectsGroupId);
        };
        button.MouseEnter += (_, _) =>
        {
            if (_activeGroupId != ActiveProjectsGroupId && button.IsEnabled)
                button.Background = MakeTabHoverBrush();
        };
        button.MouseLeave += (_, _) =>
        {
            if (_activeGroupId != ActiveProjectsGroupId)
                button.Background = _theme.GetBrush("TabButtonInactiveBackground");
        };

        return button;
    }

    /// <summary>
    /// Refreshes the Active Projects button: its aggregate diamond, its enabled/dimmed
    /// state (disabled when empty and not selected), its selected highlight, and — when
    /// it's the current view — which project tabs are visible.
    /// </summary>
    private void RefreshActiveGroup()
    {
        if (_activeGroupButton == null)
            return;

        var active = GetActiveProjects();
        var isSelected = _activeGroupId == ActiveProjectsGroupId;

        _activeGroupButton.IsEnabled = active.Count > 0 || isSelected;
        _activeGroupButton.Opacity = _activeGroupButton.IsEnabled ? 1.0 : 0.5;
        _activeGroupButton.Background = isSelected
            ? _theme.GetBrush("GroupTabActiveBackground")
            : _theme.GetBrush("TabButtonInactiveBackground");
        _activeGroupButton.BorderBrush = isSelected
            ? _theme.GetBrush("TabButtonActiveBorderBrush")
            : InactiveTabOutlineBrush();
        SetTabRailBrush(_activeGroupButton,
            isSelected ? null : _theme.GetBrush("TabButtonActiveBorderBrush"));

        if (_groupTabIcons.TryGetValue(ActiveProjectsGroupId, out var grid))
        {
            var indicator = active.Count == 0
                ? TabIndicator.Inactive
                : active.Select(GetProjectIndicator).Max();
            ApplyGroupIndicator(ActiveProjectsGroupId, grid, indicator);
        }

        if (isSelected)
            RefreshProjectTabVisibility();
    }

    private Button CreateMetaTabElement(ProjectGroup group)
    {
        var isActive = group.Id == _activeGroupId;

        // Flat template, matching the project tab visual language.
        var template = CreateTabControlTemplate();

        var iconElement = CreateGroupIconElement(group);

        var label = new TextBlock
        {
            Text = group.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _theme.GetBrush("TabButtonForeground"),
        };

        // Group status diamond (aggregate of the group's child projects), built like
        // the project-tab diamond so the two strips read identically.
        var statusGrid = new Grid
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var diamondIcon = new TextBlock
        {
            Text = "◇", // ◇ outline diamond — no active session in the group
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 14,
            Foreground = _theme.GetBrush("TabIconInactiveDiamondForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusBadge = new TextBlock
        {
            Text = "",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };
        statusGrid.Children.Add(diamondIcon);
        statusGrid.Children.Add(statusBadge);
        _groupTabIcons[group.Id] = statusGrid;

        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { iconElement, label, statusGrid }
        };

        var button = new Button
        {
            Tag = group,
            MinWidth = 44,
            Height = 32,
            Margin = new Thickness(0),
            Padding = new Thickness(12, 0, 12, 0),
            Background = isActive
                ? _theme.GetBrush("GroupTabActiveBackground")
                : _theme.GetBrush("TabButtonInactiveBackground"),
            BorderBrush = isActive
                ? _theme.GetBrush("TabButtonActiveBorderBrush")
                : InactiveTabOutlineBrush(),
            BorderThickness = TabOutlineThickness,
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Template = template,
            Content = contentPanel,
            AllowDrop = true,
            ToolTip = "Click to switch group · click again to rename · drag to reorder · right-click for options"
        };

        // Only the tabs that aren't current carry the strip's rail across their bottom
        // edge; the current one leaves the gap, so it reads as holding the workspace.
        SetTabRailBrush(button, isActive ? null : _theme.GetBrush("TabButtonActiveBorderBrush"));

        bool IsRenaming() => contentPanel.Children.OfType<TextBox>().Any();

        button.Click += (_, _) =>
        {
            // A drag just completed, or the rename box is showing — ignore the click.
            if (_groupDragging || IsRenaming())
                return;
            if (group.Id == _activeGroupId)
                StartMetaTabRename(group, contentPanel, label);
            else
                SetActiveGroup(group.Id);
        };

        button.MouseEnter += (_, _) =>
        {
            if (group.Id != _activeGroupId && !IsRenaming())
                button.Background = MakeTabHoverBrush();
        };
        button.MouseLeave += (_, _) =>
        {
            if (group.Id != _activeGroupId && !IsRenaming())
                button.Background = _theme.GetBrush("TabButtonInactiveBackground");
        };

        // Drag initiation for reordering groups (DragOver/Drop handled at MetaTabPanel level)
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _groupDragStartPoint = e.GetPosition(null);
            _groupDragSource = button;
            _groupDragging = false;
        };
        button.PreviewMouseMove += (_, e) =>
        {
            if (_groupDragSource != button || e.LeftButton != MouseButtonState.Pressed || IsRenaming())
                return;

            var pos = e.GetPosition(null);
            var diff = pos - _groupDragStartPoint;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _groupDragging = true;
                DragDrop.DoDragDrop(button, new DataObject("GroupTab", group), DragDropEffects.Move);
                _groupDragSource = null;
                RemoveGroupDragIndicator();
            }
        };

        // Right-click context menu
        var settingsItem = CreateMenuItem("Group Settings…", () => OpenGroupSettings(group));
        var renameItem = CreateMenuItem("Rename", () => StartMetaTabRename(group, contentPanel, label));
        var newGroupItem = CreateMenuItem("New group", AddNewEmptyGroup);
        var deleteItem = CreateMenuItem("Delete group", () => DeleteGroup(group.Id));
        button.ContextMenu = new ContextMenu
        {
            Items = { settingsItem, renameItem, new Separator(), newGroupItem, new Separator(), deleteItem }
        };
        button.ContextMenuOpening += (_, _) =>
        {
            // Only empty groups can be deleted (matches the toolbar/drag behaviour)
            deleteItem.IsEnabled = !_projects.Any(p => p.GroupId == group.Id);
        };

        // Drag-drop target: dropping a project tab here moves it to this group.
        button.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent("ProjectTab"))
            {
                e.Effects = DragDropEffects.Move;
                button.Background = MakeTabHoverBrush();
                e.Handled = true;
            }
            else if (!e.Data.GetDataPresent("GroupTab"))
            {
                // GroupTab drags are reorder gestures handled at the panel level — let them bubble.
                e.Effects = DragDropEffects.None;
            }
        };
        button.DragLeave += (_, _) =>
        {
            if (group.Id == _activeGroupId)
                button.Background = _theme.GetBrush("GroupTabActiveBackground");
            else
                button.Background = _theme.GetBrush("TabButtonInactiveBackground");
        };
        button.Drop += (_, e) =>
        {
            // Reset hover-tinted background regardless of outcome
            button.Background = group.Id == _activeGroupId
                ? _theme.GetBrush("GroupTabActiveBackground")
                : _theme.GetBrush("TabButtonInactiveBackground");

            if (e.Data.GetData("ProjectTab") is ProjectInfo dropped)
            {
                MoveProjectToGroup(dropped, group.Id);
                e.Handled = true;
            }
            // GroupTab drops bubble to MetaTabPanel_Drop for reordering.
        };

        return button;
    }

    /// <summary>
    /// Builds the trailing "+" button on the meta tab strip that creates a new empty
    /// group. Visual language matches the project-tab add button (<see cref="CreateToolbarAddButton"/>).
    /// </summary>
    private Button CreateMetaTabAddButton()
    {
        // Custom template: border only, no default button chrome.
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
            // Inset from the strip's 32px height and centred, so its closed rounded
            // border clears the accent rail along the strip's bottom edge instead of
            // overdrawing it.
            Width = 26,
            Height = 26,
            Margin = new Thickness(6, 3, 0, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = _theme.GetBrush("AddButtonBorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Add Group",
            Template = template,
            Content = new TextBlock
            {
                Text = "+",
                FontSize = 18,
                FontWeight = FontWeights.Light,
                Foreground = _theme.GetBrush("AddButtonForeground"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        button.Click += (_, _) => AddNewEmptyGroup();

        button.MouseEnter += (_, _) =>
        {
            button.BorderBrush = _theme.GetBrush("TabButtonHoverBorderBrush");
        };
        button.MouseLeave += (_, _) =>
        {
            button.BorderBrush = _theme.GetBrush("AddButtonBorderBrush");
        };

        return button;
    }

    /// <summary>
    /// Creates the icon element for a group. Groups have no folder, so only built-in
    /// icons are supported (defaulting to the folder glyph).
    /// </summary>
    private UIElement CreateGroupIconElement(ProjectGroup group)
    {
        var builtIn = BuiltInIcons.Find(group.Icon ?? "folder") ?? BuiltInIcons.Default;
        var foreground = group.IconColor != null
            ? ParseHexBrush(group.IconColor) ?? _theme.GetBrush("TabIconNoSessionForeground")
            : _theme.GetBrush("TabIconNoSessionForeground");

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

    private void StartMetaTabRename(ProjectGroup group, Panel contentPanel, TextBlock label)
    {
        var labelIndex = contentPanel.Children.IndexOf(label);
        if (labelIndex < 0)
            return; // already renaming or label not present

        var textbox = new TextBox
        {
            Text = group.Name,
            FontSize = 12,
            MinWidth = Math.Max(60, label.ActualWidth + 16),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-4, 0, -4, 0),
            Padding = new Thickness(2, 0, 2, 0),
            Background = _theme.GetBrush("ContentBackground"),
            Foreground = _theme.GetBrush("TabButtonForeground"),
            BorderThickness = new Thickness(1),
            BorderBrush = _theme.GetBrush("TabButtonActiveBorderBrush")
        };

        var committed = false;
        void Commit(bool save)
        {
            if (committed) return;
            committed = true;
            if (save && !string.IsNullOrWhiteSpace(textbox.Text))
            {
                var newName = textbox.Text.Trim();
                if (!string.Equals(newName, group.Name, StringComparison.Ordinal))
                {
                    group.Name = newName;
                    SetWorkspaceDirty();
                }
            }
            label.Text = group.Name;
            // Swap the textbox back out for the label, in place.
            var idx = contentPanel.Children.IndexOf(textbox);
            if (idx >= 0)
            {
                contentPanel.Children.RemoveAt(idx);
                contentPanel.Children.Insert(idx, label);
            }
        }

        textbox.LostFocus += (_, _) => Commit(true);
        textbox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Commit(false); e.Handled = true; }
        };

        // Swap the label out for the textbox, in place (keeps icon + status diamond).
        contentPanel.Children.RemoveAt(labelIndex);
        contentPanel.Children.Insert(labelIndex, textbox);

        // Defer focus until after the visual tree has placed the textbox
        Dispatcher.BeginInvoke(() =>
        {
            textbox.Focus();
            Keyboard.Focus(textbox);
            textbox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void SetActiveGroup(string groupId)
    {
        if (_activeGroupId == groupId)
            return;

        _activeGroupId = groupId;
        RefreshMetaTabBar();
        RefreshProjectTabVisibility();

        // Dynamic Active Projects group: pick the most attention-worthy active project
        // when the current one isn't in the active set.
        if (groupId == ActiveProjectsGroupId)
        {
            var active = GetActiveProjects();
            if (_activeProject == null || !active.Contains(_activeProject))
            {
                var first = active.OrderByDescending(GetProjectIndicator).FirstOrDefault();
                if (first != null)
                {
                    SwitchToProject(first);
                }
                else
                {
                    _activeProject = null;
                    ProjectContentHost.Content = null;
                    ProjectContentHost.Visibility = Visibility.Collapsed;
                    EmptyStatePanel.Visibility = Visibility.Visible;
                    UpdateTitleBar();
                }
            }
            return;
        }

        // If the active project isn't in the new group, switch to one that is.
        // Prefer the project most in need of attention, matching the priority the
        // group diamond already shows (Error > Question > NewResponse > Idle >
        // Working > Inactive). OrderByDescending is stable, so ties fall back to
        // the first project in the group.
        if (_activeProject == null || _activeProject.GroupId != groupId)
        {
            var firstInGroup = _projects
                .Where(p => p.GroupId == groupId)
                .OrderByDescending(GetProjectIndicator)
                .FirstOrDefault();
            if (firstInGroup != null)
                SwitchToProject(firstInGroup);
            else
            {
                // Empty group — show empty state but keep workspace open
                _activeProject = null;
                ProjectContentHost.Content = null;
                ProjectContentHost.Visibility = Visibility.Collapsed;
                EmptyStatePanel.Visibility = Visibility.Visible;
                UpdateTitleBar();
            }
        }
        // Pure group navigation — like project switching, doesn't dirty the workspace
    }

    private void MoveProjectToGroup(ProjectInfo project, string groupId)
    {
        if (project.GroupId == groupId)
            return;
        if (_groups.All(g => g.Id != groupId))
            return;

        var oldGroupId = project.GroupId;
        project.GroupId = groupId;
        RefreshProjectTabVisibility();

        // Both the source and destination group diamonds may change.
        RefreshGroupIndicator(oldGroupId);
        RefreshGroupIndicator(groupId);

        // If we just moved the active project out of the active group, show the next visible one.
        // When the source group is now empty, follow the project to its new group so the user
        // isn't left staring at an empty meta tab.
        if (project == _activeProject && groupId != _activeGroupId)
        {
            var next = _projects.FirstOrDefault(p => p.GroupId == _activeGroupId);
            if (next != null)
            {
                SwitchToProject(next);
            }
            else
            {
                _activeGroupId = groupId;
                RefreshMetaTabBar();
                RefreshProjectTabVisibility();
            }
        }

        SetWorkspaceDirty();
    }

    private void DeleteGroup(string groupId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return;
        if (_projects.Any(p => p.GroupId == groupId))
            return; // Only empty groups can be deleted

        _groups.Remove(group);

        // If only one group remains, dissolve all grouping (back to pristine no-group state)
        if (_groups.Count <= 1)
        {
            _groups.Clear();
            _activeGroupId = null;
            foreach (var p in _projects)
                p.GroupId = null;
        }
        else if (_activeGroupId == groupId)
        {
            _activeGroupId = _groups.OrderBy(g => g.Order).First().Id;
        }

        RefreshMetaTabBar();
        RefreshProjectTabVisibility();

        // Ensure something is showing in the content area
        if (_activeGroupId != null &&
            (_activeProject == null || _activeProject.GroupId != _activeGroupId))
        {
            var next = _projects.FirstOrDefault(p => p.GroupId == _activeGroupId);
            if (next != null) SwitchToProject(next);
        }

        SetWorkspaceDirty();
    }

    /// <summary>
    /// Opens the Group Settings dialog and applies the chosen name / icon / colour.
    /// </summary>
    private void OpenGroupSettings(ProjectGroup group)
    {
        var result = GroupSettingsDialog.Show(this, group);
        if (result == null)
            return;

        if (!string.IsNullOrWhiteSpace(result.Name))
            group.Name = result.Name.Trim();
        group.Icon = result.Icon;
        group.IconColor = result.IconColor;

        RefreshMetaTabBar();
        SetWorkspaceDirty();
    }

    /// <summary>
    /// Creates a new empty group from the group context menu and switches to it.
    /// (Only reachable when grouping is already active, so no "Ungrouped" bucket is needed.)
    /// </summary>
    private void AddNewEmptyGroup()
    {
        var newGroup = new ProjectGroup
        {
            Id = Guid.NewGuid().ToString(),
            Name = NextGroupName(),
            Order = _groups.Count == 0 ? 0 : _groups.Max(g => g.Order) + 1
        };
        _groups.Add(newGroup);
        RefreshMetaTabBar();
        SetActiveGroup(newGroup.Id);
        SetWorkspaceDirty();
    }

    /// <summary>
    /// The group strip stays a horizontal bar pinned to the top of the workspace for every
    /// toolbar position — it frames the whole area below it, wherever the project tabs sit.
    /// </summary>
    private void ApplyMetaTabLayoutForToolbarPosition(string position)
    {
        DockPanel.SetDock(MetaTabBorder, Dock.Top);
        MetaTabPanel.Orientation = Orientation.Horizontal;
    }

    private (DockingManager, AiChatControl, GitStatusControl, ProjectDescriptionControl, TodoListControl) CreateProjectDockingLayout(ProjectInfo project, string? layoutXml = null)
    {
        _log.Info("CreateDockingLayout: starting");
        var dockingManager = new DockingManager
        {
            Theme = _theme.BaseVariant == ThemeBaseVariant.Dark
                ? new Vs2013DarkTheme()
                : new Vs2013LightTheme()
        };

        // A remote project's files, git and settings all live on the other machine, so the
        // panels are switched into remote mode instead of being pointed at a local path that
        // does not exist here.
        var remoteId = GetRemoteProjectId(project);
        var isRemote = remoteId != null;

        // Create controls
        var fileExplorerControl = new FileExplorerControl(_projectSettings, _theme);
        if (isRemote)
            fileExplorerControl.EnterRemoteMode(_remoteClient, remoteId!, project.FolderPath);
        else
            fileExplorerControl.LoadDirectory(project.FolderPath);

        var gitStatusControl = new GitStatusControl(_log, _perf, _theme);
        if (isRemote)
            gitStatusControl.EnterRemoteMode();
        else
            gitStatusControl.LoadRepository(project.FolderPath);

        var filePreviewControl = new FilePreviewControl(_markdown, _theme);
        _projectPreviewControls[project] = filePreviewControl;
        _projectExplorerControls[project] = fileExplorerControl;
        if (isRemote)
            filePreviewControl.EnterRemoteMode(_remoteClient, remoteId!, project.FolderPath);

        var descriptionControl = new ProjectDescriptionControl(_projectSettings);
        if (isRemote)
            descriptionControl.EnterRemoteMode();
        else
            descriptionControl.LoadProject(project.FolderPath);

        var todoListControl = new TodoListControl(_projectSettings);
        if (isRemote)
            todoListControl.EnterRemoteMode(_remoteClient, remoteId!);
        else
            todoListControl.LoadProject(project.FolderPath);

        // Open settings dialog from description panel's settings icon
        descriptionControl.OpenSettingsRequested += () => OpenProjectSettings(project);

        // Wire settings change from file explorer settings dialog
        fileExplorerControl.ProjectSettingsChanged += () => ApplyProjectSettingsChanged(project);

        // Wire file explorer clicks to preview panel
        fileExplorerControl.FileSelected += filePath =>
        {
            _log.Info($"FileSelected: {filePath}");
            filePreviewControl.ShowFile(filePath);
        };

        // Wire git status diff clicks to preview panel
        gitStatusControl.DiffRequested += (filePath, diffContent) =>
        {
            _log.Info($"DiffRequested: {filePath}");
            var absolutePath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(project.FolderPath, filePath);
            filePreviewControl.ShowDiff(absolutePath, diffContent);
        };

        // From the diff preview, reveal the underlying file in the explorer; the
        // existing FileSelected wiring then swaps the preview from diff to full file.
        filePreviewControl.RevealInExplorerRequested += path =>
        {
            _log.Info($"RevealInExplorerRequested: {path}");
            fileExplorerControl.RevealAndSelect(path);
        };

        // Refresh file explorer when file system changes (reuses git status watcher)
        gitStatusControl.FileSystemChanged += () => fileExplorerControl.Refresh();

        var aiChatControl = new AiChatControl(
            _log, _perf, _theme, _projectSettings, _accounts,
            _sessionFactory, _claudeEnvironment, _markdown, _images);
        if (isRemote)
        {
            aiChatControl.EnterRemoteMode(_remoteClient, remoteId!, project.FolderPath);
            aiChatControl.SetRemoteDisplayRoot(project.FolderPath);
        }
        else
        {
            aiChatControl.Initialize(project.FolderPath);
        }

        // Refresh git status when Claude finishes working (transitions to Idle)
        aiChatControl.SessionStateChanged += state =>
        {
            if (state == ClaudeSessionState.Idle)
                gitStatusControl.RefreshStatus();
        };

        // Clicking a file-path reference in the chat reveals + selects the file
        // in the explorer; the existing FileSelected handler shows the preview.
        aiChatControl.FileReferenceClicked += path =>
        {
            _log.Info($"FileReferenceClicked: {path}");
            fileExplorerControl.RevealAndSelect(path);
        };

        // Update AI Chat panel title when model is reported, when stats change, or when
        // the session status changes — the chat pane has no status strip of its own.
        aiChatControl.SessionModelChanged += _ => UpdateAiChatTitle(project, aiChatControl);
        aiChatControl.SessionStatusChanged += () => UpdateAiChatTitle(project, aiChatControl);
        aiChatControl.SessionStatsChanged += _ =>
        {
            UpdateAiChatTitle(project, aiChatControl);
            UpdateTotalCost();
            // A real session round-trip consumed plan usage — the title-bar
            // indicator's cached /api/oauth/usage data is now stale. The timer
            // will pick this up on its next tick.
            _usageDirty = true;
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
            [FileExplorerId] = $"{project.DisplayName} — File Explorer",
            [GitStatusId] = "Git Status",
            [FilePreviewId] = "File Preview",
            [AiChatId] = "AI Chat",
            [ProjectDescriptionId] = "Project Description",
            [TodoListId] = "Todo List"
        };

        if (layoutXml != null)
        {
            // Restore layout from saved XML
            _log.Info("CreateDockingLayout: restoring saved layout");
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
                _log.Warn($"CreateDockingLayout: failed to restore layout, falling back to default — {ex.Message}");
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
        // AvalonDock can swap the LayoutRoot (LayoutChanged), so we move the single
        // Updated handler to the new root each time. The previous version added a
        // NEW handler on every LayoutChanged without removing the old one, so
        // handlers multiplied for the life of the app and every layout tick fired
        // all of them. Track the currently-subscribed root and detach before
        // re-attaching so there is ever only one subscription.
        LayoutRoot? subscribedLayout = null;
        EventHandler updatedHandler = (_, _) => SetWorkspaceDirty();
        void SubscribeLayoutUpdated(LayoutRoot? layout)
        {
            if (ReferenceEquals(subscribedLayout, layout)) return;
            if (subscribedLayout != null) subscribedLayout.Updated -= updatedHandler;
            subscribedLayout = layout;
            if (layout != null) layout.Updated += updatedHandler;
        }
        SubscribeLayoutUpdated(dockingManager.Layout);
        dockingManager.LayoutChanged += (_, _) =>
        {
            SubscribeLayoutUpdated(dockingManager.Layout);
            SetWorkspaceDirty();
        };

        _log.Info("CreateDockingLayout: complete");
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
            Title = $"{project.DisplayName} — File Explorer",
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

    private UIElement CreatePanelPlaceholder(string title, string subtitle)
    {
        return new Border
        {
            Background = _theme.GetBrush("PlaceholderBackground"),
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
                        Foreground = _theme.GetBrush("PlaceholderTitleForeground"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 4)
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        FontSize = 12,
                        Foreground = _theme.GetBrush("PlaceholderSubtitleForeground"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };
    }

    private void SwitchToProject(ProjectInfo project)
    {
        using var perfScope = _perf.Time("SwitchToProject");
        _log.Info($"SwitchToProject: '{project.FolderName}' — entry (prev='{_activeProject?.FolderName ?? "(none)"}', projects={_projects.Count}, groups={_groups.Count})");
        if (_activeProject == project)
        {
            _log.Info("SwitchToProject: already active — no-op");
            return;
        }

        // If groups are in use and this project lives in a different group,
        // switch the active group so its tab is actually visible. Exception: while
        // viewing the dynamic Active Projects group, stay there if the project is one
        // of its members (don't jump back to the project's real group).
        if (_groups.Count >= 2 && project.GroupId != null && project.GroupId != _activeGroupId)
        {
            var stayInActiveGroup = _activeGroupId == ActiveProjectsGroupId && GetActiveProjects().Contains(project);
            if (!stayInActiveGroup)
            {
                _log.Info($"SwitchToProject: switching active group '{_activeGroupId}' -> '{project.GroupId}'");
                _activeGroupId = project.GroupId;
                RefreshMetaTabBar();
                RefreshProjectTabVisibility();
            }
        }

        // Update tab button styles
        if (_activeProject != null && _projectTabButtons.TryGetValue(_activeProject, out var prevButton))
            SetTabButtonActive(prevButton, false);

        // Suspend the outgoing tab's git work — only the active tab watches files
        // and refreshes status (the diamond keeps updating via session state).
        if (_activeProject != null && _projectGitControls.TryGetValue(_activeProject, out var prevGit))
            prevGit.Deactivate();

        _activeProject = project;

        if (_projectTabButtons.TryGetValue(project, out var newButton))
            SetTabButtonActive(newButton, true);

        // Activate the incoming tab's git work: one refresh + start watching.
        if (_projectGitControls.TryGetValue(project, out var newGit))
            newGit.Activate();

        // User is now viewing this tab — clear any attention flash on its diamond.
        StopAttentionPulseIfAny(project);

        // Show the project's content
        if (_projectContents.TryGetValue(project, out var content))
        {
            _log.Info($"SwitchToProject: swapping ProjectContentHost.Content -> '{project.FolderName}'");
            ProjectContentHost.Content = content;
            ProjectContentHost.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            _log.Info("SwitchToProject: content swap returned");
        }
        else
        {
            _log.Warn($"SwitchToProject: no content registered for '{project.FolderName}' — skipping content swap");
        }

        // Update title bar
        UpdateTitleBar();

        // Focus the AI chat input if the session is idle
        if (_projectChatControls.TryGetValue(project, out var chatControl))
            chatControl.FocusInput();

        // While viewing the Active Projects group, re-prune the strip so a tab that
        // lingered only because it was the previous view now collapses if it's no
        // longer in the active set.
        if (_activeGroupId == ActiveProjectsGroupId)
            RefreshProjectTabVisibility();

        _log.Info($"SwitchToProject: '{project.FolderName}' — complete");
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
            Title = $"Agent Dock — {workspaceName}{dirtyMarker} — {_activeProject.DisplayName}";
            TitleBarText.Text = $"{workspaceName}{dirtyMarker} — {_activeProject.DisplayName}";
        }
        else
        {
            Title = $"Agent Dock — {_activeProject.DisplayName}{dirtyMarker}";
            TitleBarText.Text = $"{_activeProject.DisplayName}{dirtyMarker}";
        }
    }

    private void UpdateTaskbarIcon()
    {
        var accentBrush = _theme.GetBrush("TaskbarAccentColor");
        Icon = _taskbarIcon.CreateThemedIcon(accentBrush.Color);
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

    private void UpdateAiChatTitle(ProjectInfo project, AiChatControl ctrl)
    {
        if (!_projectDockingManagers.TryGetValue(project, out var dm))
            return;

        var anchorable = dm.Layout.Descendents().OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == AiChatId);
        if (anchorable == null)
            return;

        var prefix = FormatModelName(ctrl.Model) ?? "AI Chat";

        // Show which login this session runs as, when accounts are configured.
        if (!string.IsNullOrEmpty(ctrl.AccountLabel))
            prefix += $" · {ctrl.AccountLabel}";

        // Session status (Idle / spinner+Working / ⚠ danger mode) lives here rather than
        // in a strip inside the pane. It goes before the cost so it survives first when
        // a narrow pane truncates the title.
        if (!string.IsNullOrEmpty(ctrl.StatusLabel))
            prefix += $" · {ctrl.StatusLabel}";

        var stats = ctrl.Stats;
        if (stats.TotalCostUsd > 0 || stats.TotalTokens > 0)
            anchorable.Title = $"{prefix} — ${stats.TotalCostUsd:F4} · {SessionStats.FormatTokens(stats.TotalTokens)} tokens";
        else
            anchorable.Title = prefix;
    }

    /// <summary>
    /// Pretty-prints a raw Claude model id (e.g. "claude-sonnet-4-5-20250929") as "Sonnet 4.5".
    /// Returns null if <paramref name="model"/> is null/empty.
    /// </summary>
    private string? FormatModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        // Strip "claude-" prefix
        var s = model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? model["claude-".Length..]
            : model;

        // Drop trailing 8-digit date suffix (e.g. "-20250929")
        var parts = s.Split('-');
        if (parts.Length > 1 && parts[^1].Length == 8 && parts[^1].All(char.IsDigit))
            parts = parts[..^1];

        if (parts.Length == 0)
            return model;

        // Title-case the family (first segment); join version segments with dots.
        var family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..].ToLowerInvariant();
        if (parts.Length == 1)
            return family;

        var version = string.Join('.', parts[1..]);
        return $"{family} {version}";
    }

    /// <summary>
    /// Paints a project tab as current or not. The current tab takes the strip's own
    /// background and a three-sided accent outline, and drops its slice of the accent
    /// rail so the outline opens straight into the content area it owns; the rest sit a
    /// shade darker and keep their slice, so the rail reads as unbroken around them.
    /// </summary>
    private void SetTabButtonActive(Button button, bool active)
    {
        if (active)
        {
            button.Background = ActiveProjectTabBrush();
            button.BorderBrush = _theme.GetBrush("ProjectTabActiveBorderBrush");
            SetTabRailBrush(button, null);
        }
        else
        {
            button.Background = InactiveProjectTabBrush();
            button.BorderBrush = InactiveTabOutlineBrush();
            SetTabRailBrush(button, _theme.GetBrush("ProjectTabActiveBorderBrush"));
        }
    }

    /// <summary>
    /// Subtle hover background for inactive tabs, derived from the active background
    /// so it matches every theme without requiring per-theme brush additions.
    /// </summary>
    private SolidColorBrush MakeTabHoverBrush()
    {
        var activeBrush = _theme.GetBrush("TabButtonActiveBackground");
        var c = activeBrush.Color;
        return new SolidColorBrush(Color.FromArgb(96, c.R, c.G, c.B));
    }

    private void CloseActiveProject()
    {
        if (_activeProject != null)
            CloseProject(_activeProject);
    }

    private void CloseProject(ProjectInfo project)
    {
        var closedGroupId = project.GroupId;

        // Stop icon animation timer
        if (_tabIconTimers.TryGetValue(project, out var timer))
        {
            timer.Stop();
            _tabIconTimers.Remove(project);
        }

        // Stop git watcher AND detach its theme handler (Cleanup), else the static
        // _theme.ThemeChanged event keeps the control + its visuals alive.
        if (_projectGitControls.TryGetValue(project, out var gitControl))
        {
            gitControl.Cleanup();
            _projectGitControls.Remove(project);
        }

        // Detach the preview control's theme handler so it can be GC'd.
        if (_projectPreviewControls.TryGetValue(project, out var previewControl))
        {
            previewControl.Cleanup();
            _projectPreviewControls.Remove(project);
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
        _previousTabStates.Remove(project);
        _projectDockingManagers.Remove(project);
        _projectDescriptionControls.Remove(project);
        _projectTodoListControls.Remove(project);
        _projectNewResponse.Remove(project);
        _projectLastActivity.Remove(project);

        // Remove content
        _projectContents.Remove(project);

        // Remove from list
        _projects.Remove(project);

        // The closed project's group diamond may need to drop priority.
        RefreshGroupIndicator(closedGroupId);

        // Closing may have removed a member of the Active Projects set.
        RefreshActiveGroup();

        SetWorkspaceDirty();

        // If this was the active project, switch to another or show empty state.
        // When groups are active, prefer a project in the same active group.
        if (_activeProject == project)
        {
            _activeProject = null;

            var filtering = _groups.Count >= 2 && _activeGroupId != null;
            ProjectInfo? next = filtering
                ? _projects.LastOrDefault(p => p.GroupId == _activeGroupId)
                : _projects.LastOrDefault();

            if (next != null)
            {
                SwitchToProject(next);
            }
            else if (_projects.Count > 0)
            {
                // Active group is empty but other projects exist — leave empty content
                ProjectContentHost.Content = null;
                ProjectContentHost.Visibility = Visibility.Collapsed;
                EmptyStatePanel.Visibility = Visibility.Visible;
                UpdateTitleBar();
            }
            else
            {
                ProjectContentHost.Content = null;
                ProjectContentHost.Visibility = Visibility.Collapsed;
                EmptyStatePanel.Visibility = Visibility.Visible;
                UpdateProjectStripVisibility();
                UpdateGroupStripVisibility();
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
                gitControl.Cleanup();
                _projectGitControls.Remove(project);
            }

            if (_projectPreviewControls.TryGetValue(project, out var previewControl))
            {
                previewControl.Cleanup();
                _projectPreviewControls.Remove(project);
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
            _previousTabStates.Remove(project);
            _projectDockingManagers.Remove(project);
            _projectDescriptionControls.Remove(project);
            _projectTodoListControls.Remove(project);
            _projectContents.Remove(project);
        }

        _projectPreviewControls.Clear();
        _previousTabStates.Clear();
        _projectLastActivity.Clear();
        _projects.Clear();
        _activeProject = null;
        _groups.Clear();
        _activeGroupId = null;
        _showActiveProjectsGroup = true;
        _activeProjectsLimit = WorkspaceFile.DefaultActiveProjectsLimit;
        _activeGroupButton = null;
        _groupTabIcons.Remove(ActiveProjectsGroupId);
        ActiveGroupHost.Content = null;
        MetaTabPanel.Children.Clear();
        UpdateGroupStripVisibility();
        ProjectContentHost.Content = null;
        ProjectContentHost.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Visible;
        UpdateProjectStripVisibility();
    }

    private void OpenInExplorer(ProjectInfo project)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = project.FolderPath,
            UseShellExecute = true
        });
    }

    private void OpenInConsole(ProjectInfo project)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                WorkingDirectory = project.FolderPath,
                UseShellExecute = true
            });
        }
        catch { /* failed to open */ }
    }

    /// <summary>Launches an external editor (e.g. "code", "cursor") on the project folder.</summary>
    private void LaunchEditor(string command, ProjectInfo project)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = $"\"{project.FolderPath}\"",
                UseShellExecute = true
            });
        }
        catch { /* tool not available */ }
    }

    /// <summary>
    /// Shows the Project Settings dialog for the given project and applies the result.
    /// Shared by the description-panel icon, the File Explorer button, and the tab menu.
    /// </summary>
    private void OpenProjectSettings(ProjectInfo project)
    {
        var result = ProjectSettingsDialog.Show(this, project.FolderPath, _projectSettings);
        if (result == null)
            return;

        _projectSettings.Update(project.FolderPath, s =>
        {
            s.Name = result.Name;
            s.Icon = result.Icon;
            s.IconColor = result.IconColor;
            s.Description = result.Description;
            s.SoundOnSessionStart = result.SoundOnSessionStart;
            s.SoundOnAgentWaiting = result.SoundOnAgentWaiting;
            s.SoundOnSessionEnd = result.SoundOnSessionEnd;
        });
        ApplyProjectSettingsChanged(project);
    }

    /// <summary>
    /// Refreshes every UI surface that reflects a project's settings (tab icon + name,
    /// file-explorer panel title, window title, description panel) after a settings change.
    /// </summary>
    private void ApplyProjectSettingsChanged(ProjectInfo project)
    {
        var settings = _projectSettings.Load(project.FolderPath);

        // Update in-memory display name and refresh every UI surface that shows it
        project.CustomName = settings.Name;

        if (_projectTabButtons.TryGetValue(project, out var tabBtn) &&
            tabBtn.Content is StackPanel sp && sp.Children.Count > 0)
        {
            sp.Children.RemoveAt(0);
            sp.Children.Insert(0, CreateIconElement(
                settings.Icon ?? "folder", project.FolderPath, settings.IconColor));

            // TextBlock is at index 1 (between icon at 0 and status grid at 2)
            if (sp.Children.Count > 1 && sp.Children[1] is TextBlock tabText)
                tabText.Text = project.DisplayName;
        }

        // Update file explorer panel title in the docking layout
        if (_projectDockingManagers.TryGetValue(project, out var dm))
        {
            var fileExplorerPanel = dm.Layout.Descendents().OfType<LayoutAnchorable>()
                .FirstOrDefault(a => a.ContentId == FileExplorerId);
            if (fileExplorerPanel != null)
                fileExplorerPanel.Title = $"{project.DisplayName} — File Explorer";
        }

        // If this is the active project, refresh the window title bar
        if (project == _activeProject)
            UpdateTitleBar();

        if (_projectDescriptionControls.TryGetValue(project, out var desc))
            desc.SetDescription(settings.Description);
    }

    // --- Tab Icon Updates ---

    private void UpdateTabIcon(ProjectInfo project, ClaudeSessionState state)
    {
        // Every state (or schedule) change is recent activity — drives the Active
        // Projects group's most-recently-active ordering.
        _projectLastActivity[project] = DateTime.UtcNow;

        if (!_projectTabIcons.TryGetValue(project, out var statusGrid))
            return;

        // Play sounds on state transitions (gated by per-project settings)
        _previousTabStates.TryGetValue(project, out var prevState);
        _previousTabStates[project] = state;

        var soundSettings = _projectSettings.Load(project.FolderPath);
        switch (state)
        {
            case ClaudeSessionState.Initializing when soundSettings.SoundOnSessionStart:
                _sound.PlayDeviceConnect();
                break;
            case ClaudeSessionState.Idle
                when soundSettings.SoundOnAgentWaiting
                     && prevState is ClaudeSessionState.Working or ClaudeSessionState.WaitingForPermission:
                _sound.PlayMessageNudge();
                break;
            // A question (AskUserQuestion) or tool-permission prompt parks the turn here,
            // which is just as much "waiting for input" as returning to Idle — without this
            // the prompt appears silently and only chimes once the whole turn finishes.
            // Its own sound, so a blocked turn is audibly distinct from a finished one.
            case ClaudeSessionState.WaitingForPermission when soundSettings.SoundOnAgentWaiting:
                _sound.PlayQuestionPrompt();
                break;
            case ClaudeSessionState.Exited when soundSettings.SoundOnSessionEnd:
                _sound.PlayDeviceDisconnect();
                break;
        }

        var diamondIcon = (TextBlock)statusGrid.Children[0];
        var statusBadge = (TextBlock)statusGrid.Children[1];

        // Stop any existing pulse timer for this project
        if (_tabIconTimers.TryGetValue(project, out var existingTimer))
        {
            existingTimer.Stop();
            _tabIconTimers.Remove(project);
        }

        // Reset
        statusBadge.Visibility = Visibility.Collapsed;
        diamondIcon.Visibility = Visibility.Visible;
        _projectNewResponse.Remove(project); // recomputed below for the Idle/just-finished case

        // A parked scheduled message replaces the quiet (idle / no-session) diamond with
        // a clock glyph + tooltip. Active states (working/permission/error) and an unseen
        // completion still take precedence \u2014 the schedule is the calmest signal.
        var scheduledUtc = _projectChatControls.TryGetValue(project, out var schedChat)
            ? schedChat.ScheduledFireTimeUtc
            : null;

        switch (state)
        {
            case ClaudeSessionState.NotStarted:
            case ClaudeSessionState.Exited:
                if (scheduledUtc != null)
                {
                    ApplyScheduledVisual(diamondIcon);
                    break;
                }
                diamondIcon.Text = "\u25C7"; //◇ outline diamond
                diamondIcon.Foreground = _theme.GetBrush("TabIconInactiveDiamondForeground");
                break;

            case ClaudeSessionState.Initializing:
                diamondIcon.Text = "\u25C6"; // ◆ filled diamond
                diamondIcon.Foreground = _theme.GetBrush("TabIconWaitingForeground");
                break;

            case ClaudeSessionState.Idle:
                diamondIcon.Text = "\u25C6"; // ◆
                diamondIcon.Foreground = _theme.GetBrush("TabIconIdleForeground");
                // Flash the green diamond for either of two reasons: a background tab
                // just finished a turn (an unseen completion, cleared on switch-to, see
                // SwitchToProject), or the session is still doing background work after
                // its turn returned to Idle (stays until the work drains, even on the
                // active tab). Solid green means truly idle and available.
                var justFinishedUnseen = project != _activeProject &&
                    prevState is ClaudeSessionState.Working or ClaudeSessionState.WaitingForPermission;
                if (justFinishedUnseen)
                    _projectNewResponse.Add(project);
                var hasBackgroundWork = _projectChatControls.TryGetValue(project, out var idleChat)
                    && idleChat.HasBackgroundWork;
                if (justFinishedUnseen || hasBackgroundWork)
                {
                    StartAttentionPulse(project, diamondIcon);
                }
                else if (scheduledUtc != null)
                {
                    ApplyScheduledVisual(diamondIcon);
                }
                break;

            case ClaudeSessionState.Working:
                diamondIcon.Text = "\u25C6"; // ◆
                diamondIcon.Foreground = _theme.GetBrush("TabIconWorkingForeground");
                StartDiamondPulse(project, diamondIcon);
                break;

            case ClaudeSessionState.WaitingForPermission:
                diamondIcon.Text = "\u25C6"; // ◆
                diamondIcon.Foreground = _theme.GetBrush("TabIconWaitingForeground");
                break;

            case ClaudeSessionState.Error:
                diamondIcon.Text = "\u25C6"; // ◆
                diamondIcon.Foreground = _theme.GetBrush("TabIconErrorForeground");
                statusBadge.Text = "!";
                statusBadge.FontWeight = FontWeights.Bold;
                statusBadge.Foreground = _theme.GetBrush("TabIconErrorForeground");
                statusBadge.Visibility = Visibility.Visible;
                break;
        }

        UpdateTabScheduleTooltip(project, scheduledUtc);

        // Roll the child's new state up into its group's aggregate diamond.
        RefreshGroupIndicator(project.GroupId);

        // A session going live/idle/exited may add or drop it from the Active Projects set.
        RefreshActiveGroup();
    }

    /// <summary>
    /// Renders the "message scheduled" indicator: a clock glyph in the scheduled
    /// accent colour, replacing the diamond. Used on both project and group tabs.
    /// </summary>
    private void ApplyScheduledVisual(TextBlock icon)
    {
        icon.Text = "◷"; // ◷ circle-with-quadrant — reads as a clock/timer face
        icon.Foreground = _theme.GetBrush("TabIconScheduledForeground");
    }

    /// <summary>
    /// Appends the scheduled fire time to the tab's tooltip while a message is parked,
    /// reverting to just the folder path once it fires or is cancelled.
    /// </summary>
    private void UpdateTabScheduleTooltip(ProjectInfo project, DateTime? scheduledUtc)
    {
        if (!_projectTabButtons.TryGetValue(project, out var button))
            return;

        var tip = project.FolderPath;
        if (scheduledUtc is { } utc)
            tip += $"\nMessage scheduled for {utc.ToLocalTime():g}";
        if (_projectChatControls.TryGetValue(project, out var chat) &&
            chat.CurrentState == ClaudeSessionState.Idle && chat.HasBackgroundWork)
            tip += "\nStill working in the background…";

        button.ToolTip = tip;
    }

    private void StartDiamondPulse(ProjectInfo project, TextBlock diamondIcon)
    {
        var bright = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            bright = !bright;
            // Blink via Visibility (Hidden keeps layout) instead of Opacity.
            diamondIcon.Visibility = bright ? Visibility.Visible : Visibility.Hidden;
        };
        timer.Start();
        _tabIconTimers[project] = timer;
    }

    /// <summary>
    /// Subtle flash on the Idle (green) diamond so the user notices a completion
    /// on a background tab. Slower and shallower than the Working pulse.
    /// Cleared by <see cref="SwitchToProject"/> once the user views the tab.
    /// </summary>
    private void StartAttentionPulse(ProjectInfo project, TextBlock diamondIcon)
    {
        var bright = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            bright = !bright;
            diamondIcon.Visibility = bright ? Visibility.Visible : Visibility.Hidden;
        };
        timer.Start();
        _tabIconTimers[project] = timer;
    }

    /// <summary>
    /// Stops the attention flash on an Idle tab when the user has now viewed it.
    /// No-op if the tab is flashing for a different reason (e.g. Working pulse).
    /// </summary>
    private void StopAttentionPulseIfAny(ProjectInfo project)
    {
        if (!_projectChatControls.TryGetValue(project, out var chat) ||
            chat.CurrentState != ClaudeSessionState.Idle)
            return;

        if (!_tabIconTimers.TryGetValue(project, out var timer))
            return;

        // The completion has now been seen — drop the group's flashing-green state.
        _projectNewResponse.Remove(project);

        // ...but if the session is still doing background work, the diamond must keep
        // pulsing for THAT reason. "Seen" only clears the unseen-completion state, not
        // the still-working one, so leave the flash running.
        if (chat.HasBackgroundWork)
        {
            RefreshGroupIndicator(project.GroupId);
            return;
        }

        timer.Stop();
        _tabIconTimers.Remove(project);

        if (_projectTabIcons.TryGetValue(project, out var grid) &&
            grid.Children.Count > 0 &&
            grid.Children[0] is TextBlock diamond)
        {
            diamond.Visibility = Visibility.Visible;
            // A message may have been scheduled while this tab was flashing; with the
            // completion now seen, the calm scheduled glyph takes over from the green.
            if (chat.ScheduledFireTimeUtc != null)
                ApplyScheduledVisual(diamond);
        }

        RefreshGroupIndicator(project.GroupId);
    }

    /// <summary>
    /// Re-evaluates an Idle session's diamond when its background-work status changes
    /// (subagents / background shells starting or draining). A turn can return to Idle
    /// while that work keeps running, in which case a solid-green "available" diamond is
    /// misleading — so we pulse it (same flash as an unseen completion) until the work
    /// drains, then settle back to solid green. No-op unless the session is Idle; every
    /// other state already owns the diamond.
    /// </summary>
    private void RefreshBackgroundWorkVisual(ProjectInfo project)
    {
        if (!_projectChatControls.TryGetValue(project, out var chat) ||
            chat.CurrentState != ClaudeSessionState.Idle)
            return;
        if (!_projectTabIcons.TryGetValue(project, out var statusGrid) ||
            statusGrid.Children.Count == 0 ||
            statusGrid.Children[0] is not TextBlock diamondIcon)
            return;

        // While Idle, any running icon timer is an attention pulse (Working uses its own
        // state). Pulse if either an unseen completion is waiting or background work is live.
        var shouldPulse = _projectNewResponse.Contains(project) || chat.HasBackgroundWork;
        var pulsing = _tabIconTimers.ContainsKey(project);

        if (shouldPulse && !pulsing)
        {
            StartAttentionPulse(project, diamondIcon);
        }
        else if (!shouldPulse && pulsing)
        {
            if (_tabIconTimers.TryGetValue(project, out var timer))
            {
                timer.Stop();
                _tabIconTimers.Remove(project);
            }
            diamondIcon.Visibility = Visibility.Visible;
            if (chat.ScheduledFireTimeUtc != null)
                ApplyScheduledVisual(diamondIcon);
        }

        UpdateTabScheduleTooltip(project, chat.ScheduledFireTimeUtc);
        RefreshGroupIndicator(project.GroupId);
    }

    // --- Group-level status indicator (aggregate of child projects) ---

    /// <summary>
    /// A tab's visible status, ordered by priority. At the group level the highest
    /// priority among the group's projects wins.
    /// </summary>
    private enum TabIndicator
    {
        Inactive = 0,          // hollow purple ◇ — no or ended session
        Scheduled = 1,         // clock ◷ — a message is parked to send later (lowest signal)
        Working = 2,           // flashing blue ◆
        Idle = 3,              // solid green ◆ — ready, completion already seen
        BackgroundWorking = 4, // flashing green ◆ — turn idle but subagents/background tasks still running
        NewResponse = 5,       // flashing green ◆ — completed turn the user hasn't viewed
        Question = 6,          // solid orange ◆ — waiting for a permission answer
        Error = 7,             // red ◆ + "!" — session error
    }

    private TabIndicator GetProjectIndicator(ProjectInfo project)
    {
        if (!_projectChatControls.TryGetValue(project, out var chat))
            return TabIndicator.Inactive;

        var scheduled = chat.ScheduledFireTimeUtc != null;
        return chat.CurrentState switch
        {
            ClaudeSessionState.Error => TabIndicator.Error,
            ClaudeSessionState.WaitingForPermission => TabIndicator.Question,
            ClaudeSessionState.Idle =>
                _projectNewResponse.Contains(project) ? TabIndicator.NewResponse
                : chat.HasBackgroundWork ? TabIndicator.BackgroundWorking
                : scheduled ? TabIndicator.Scheduled
                : TabIndicator.Idle,
            ClaudeSessionState.Working => TabIndicator.Working,
            ClaudeSessionState.Initializing => TabIndicator.Working,
            _ => scheduled ? TabIndicator.Scheduled : TabIndicator.Inactive, // NotStarted, Exited
        };
    }

    /// <summary>
    /// Recomputes a group's diamond from the highest-priority indicator among its
    /// projects. No-op when the meta bar is hidden or the group has no diamond yet.
    /// </summary>
    private void RefreshGroupIndicator(string? groupId)
    {
        if (groupId == null || !_groupTabIcons.TryGetValue(groupId, out var statusGrid))
            return;

        var children = _projects.Where(p => p.GroupId == groupId).ToList();
        var indicator = children.Count == 0
            ? TabIndicator.Inactive
            : children.Select(GetProjectIndicator).Max();

        ApplyGroupIndicator(groupId, statusGrid, indicator);
    }

    private void ApplyGroupIndicator(string groupId, Grid statusGrid, TabIndicator indicator)
    {
        var diamond = (TextBlock)statusGrid.Children[0];
        var badge = (TextBlock)statusGrid.Children[1];

        if (_groupIconTimers.TryGetValue(groupId, out var existing))
        {
            existing.Stop();
            _groupIconTimers.Remove(groupId);
        }

        badge.Visibility = Visibility.Collapsed;
        diamond.Visibility = Visibility.Visible;

        switch (indicator)
        {
            case TabIndicator.Inactive:
                diamond.Text = "◇";
                diamond.Foreground = _theme.GetBrush("TabIconInactiveDiamondForeground");
                break;

            case TabIndicator.Scheduled:
                ApplyScheduledVisual(diamond); // clock ◷ — no pulse, lowest-priority signal
                break;

            case TabIndicator.Working:
                diamond.Text = "◆";
                diamond.Foreground = _theme.GetBrush("TabIconWorkingForeground");
                StartGroupPulse(groupId, diamond, 500); // flashing blue
                break;

            case TabIndicator.Idle:
                diamond.Text = "◆";
                diamond.Foreground = _theme.GetBrush("TabIconIdleForeground");
                break;

            case TabIndicator.BackgroundWorking:
                diamond.Text = "◆";
                diamond.Foreground = _theme.GetBrush("TabIconIdleForeground");
                StartGroupPulse(groupId, diamond, 700); // flashing green — still busy in the background
                break;

            case TabIndicator.NewResponse:
                diamond.Text = "◆";
                diamond.Foreground = _theme.GetBrush("TabIconIdleForeground");
                StartGroupPulse(groupId, diamond, 700); // flashing green
                break;

            case TabIndicator.Question:
                diamond.Text = "◆";
                diamond.Foreground = _theme.GetBrush("TabIconWaitingForeground");
                break;

            case TabIndicator.Error:
                diamond.Text = "◆";
                diamond.Foreground = _theme.GetBrush("TabIconErrorForeground");
                badge.Text = "!";
                badge.FontWeight = FontWeights.Bold;
                badge.Foreground = _theme.GetBrush("TabIconErrorForeground");
                badge.Visibility = Visibility.Visible;
                break;
        }
    }

    private void StartGroupPulse(string groupId, TextBlock diamond, int intervalMs)
    {
        var bright = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
        timer.Tick += (_, _) =>
        {
            bright = !bright;
            diamond.Visibility = bright ? Visibility.Visible : Visibility.Hidden;
        };
        timer.Start();
        _groupIconTimers[groupId] = timer;
    }

    // --- Workspace Save/Load ---

    private void SetWorkspaceDirty()
    {
        // Counts every call, including the early-return no-ops below. A climbing
        // per-interval count in the PERF health log means Layout.Updated handlers
        // are accumulating (the per-project subscription leak in CreateDockingLayout).
        _perf.NoteWorkspaceDirty();
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

        _log.Info($"SaveWorkspace: saving to '{_currentWorkspacePath}'");

        var workspace = new WorkspaceFile
        {
            Theme = _theme.CurrentTheme.Id,
            ToolbarPosition = _currentToolbarPosition,
            ActiveProjectPath = _activeProject?.FolderPath,
            // The dynamic group's sentinel isn't a real group — don't persist it as the
            // active group (its sessions won't survive restart anyway).
            ActiveGroupId = _activeGroupId == ActiveProjectsGroupId ? null : _activeGroupId,
            ShowActiveProjectsGroup = _showActiveProjectsGroup,
            ActiveProjectsLimit = _activeProjectsLimit,
            Groups = _groups.Select(g => new ProjectGroup
            {
                Id = g.Id,
                Name = g.Name,
                Order = g.Order,
                Icon = g.Icon,
                IconColor = g.IconColor
            }).ToList()
        };

        foreach (var project in _projects)
        {
            var wp = new WorkspaceProject
            {
                FolderPath = project.FolderPath,
                GroupId = project.GroupId
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
                    _log.Warn($"SaveWorkspace: failed to serialize layout for '{project.FolderName}' — {ex.Message}");
                }
            }

            workspace.Projects.Add(wp);
        }

        try
        {
            _workspaceStore.Save(_currentWorkspacePath, workspace);
            _workspaceDirty = false;
            UpdateTitleBar();
            PopulateRecentWorkspacesMenu();
            _log.Info("SaveWorkspace: complete");
        }
        catch (Exception ex)
        {
            _log.Error($"SaveWorkspace: failed — {ex.Message}");
            ThemedMessageBox.Show(
                this,
                $"Failed to save workspace:\n\n{ex.Message}",
                "Save Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task OpenWorkspaceFile(string filePath)
    {
        _log.Info($"OpenWorkspaceFile: '{filePath}'");

        // A workspace open runs across dispatcher yields, so guard against a second
        // one starting (double-click, menu item during the spinner) mid-restore.
        if (_workspaceLoading)
        {
            _log.Warn("OpenWorkspaceFile: another workspace open is already in progress");
            return;
        }

        WorkspaceFile? workspace;
        try
        {
            workspace = _workspaceStore.Load(filePath);
        }
        catch (Exception ex)
        {
            _log.Error($"OpenWorkspaceFile: failed to load — {ex.Message}");
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

        _workspaceLoading = true;
        ShowBusyOverlay(
            $"Opening {Path.GetFileNameWithoutExtension(filePath)}…",
            "Preparing workspace");
        try
        {
            await RestoreWorkspaceAsync(workspace, filePath);
        }
        finally
        {
            // Restore never leaves dirty tracking off, even if it faulted part-way
            _suppressDirty = false;
            HideBusyOverlay();
            _workspaceLoading = false;
        }
    }

    /// <summary>
    /// Restores a loaded workspace into the window. Yields to the dispatcher between
    /// projects so the busy overlay paints and its spinner keeps turning.
    /// </summary>
    private async Task RestoreWorkspaceAsync(WorkspaceFile workspace, string filePath)
    {
        // Let the overlay paint before the (blocking) teardown of the old projects
        await Dispatcher.Yield(DispatcherPriority.Background);

        // Close all current projects
        CloseAllProjects();

        // Suppress dirty tracking while restoring
        _suppressDirty = true;

        // Apply theme
        _theme.ApplyTheme(_themeRegistry.Resolve(workspace.Theme));

        // Apply toolbar position
        if (!string.IsNullOrEmpty(workspace.ToolbarPosition))
        {
            SetToolbarPosition(workspace.ToolbarPosition);
            _appSettings.SetString("ToolbarPosition", workspace.ToolbarPosition);
        }

        // Restore groups before adding projects so AddProjectFromPath doesn't auto-assign GroupId
        _groups.Clear();
        _activeGroupId = null;
        _showActiveProjectsGroup = workspace.ShowActiveProjectsGroup;
        _activeProjectsLimit = WorkspaceFile.ClampActiveProjectsLimit(workspace.ActiveProjectsLimit);
        if (workspace.Groups != null && workspace.Groups.Count > 0)
        {
            foreach (var g in workspace.Groups)
                _groups.Add(new ProjectGroup
                {
                    Id = g.Id,
                    Name = g.Name,
                    Order = g.Order,
                    Icon = g.Icon,
                    IconColor = g.IconColor
                });
        }

        // Restore projects (group assignment is applied below from the workspace file)
        ProjectInfo? activeProject = null;
        var pathToGroup = workspace.Projects.ToDictionary(
            p => p.FolderPath,
            p => p.GroupId,
            StringComparer.OrdinalIgnoreCase);

        var loaded = 0;
        foreach (var wp in workspace.Projects)
        {
            loaded++;
            UpdateBusyOverlay(
                Path.GetFileName(wp.FolderPath.TrimEnd(Path.DirectorySeparatorChar)),
                loaded,
                workspace.Projects.Count);
            await Dispatcher.Yield(DispatcherPriority.Background);

            var project = AddProjectFromPath(wp.FolderPath, wp.DockingLayout);
            if (project != null)
            {
                // Overwrite any default group assignment with the serialized one. An id that
                // matches no group — a deleted group, or the Active Projects sentinel that
                // earlier builds could persist — would hide the tab under every group tab, so
                // adopt those orphans into the first group instead.
                var savedGroupId = pathToGroup.TryGetValue(wp.FolderPath, out var gid) ? gid : null;
                project.GroupId = _groups.Count == 0
                    ? null
                    : savedGroupId != null && _groups.Any(g => g.Id == savedGroupId)
                        ? savedGroupId
                        : _groups.OrderBy(g => g.Order).First().Id;
                if (string.Equals(wp.FolderPath, workspace.ActiveProjectPath, StringComparison.OrdinalIgnoreCase))
                    activeProject = project;
            }
        }

        UpdateBusyOverlay("Restoring layout", workspace.Projects.Count, workspace.Projects.Count);
        await Dispatcher.Yield(DispatcherPriority.Background);

        // Determine active group (prefer saved, fall back to first group, or null if none)
        _activeGroupId = workspace.ActiveGroupId != null && _groups.Any(g => g.Id == workspace.ActiveGroupId)
            ? workspace.ActiveGroupId
            : _groups.OrderBy(g => g.Order).FirstOrDefault()?.Id;

        RefreshMetaTabBar();
        RefreshProjectTabVisibility();

        // Switch to the active project (if it's hidden by group filter, switch to its group)
        if (activeProject != null)
        {
            if (_groups.Count >= 2 && activeProject.GroupId != null && activeProject.GroupId != _activeGroupId)
                _activeGroupId = activeProject.GroupId;
            RefreshMetaTabBar();
            RefreshProjectTabVisibility();
            SwitchToProject(activeProject);
        }
        else if (_activeGroupId != null)
        {
            // Active project missing — pick the first project in the active group
            var first = _projects.FirstOrDefault(p => p.GroupId == _activeGroupId);
            if (first != null)
                SwitchToProject(first);
        }

        _suppressDirty = false;
        _currentWorkspacePath = filePath;
        _workspaceDirty = false;
        UpdateTitleBar();
        PopulateRecentWorkspacesMenu();
        _log.Info("OpenWorkspaceFile: complete");
    }

    // --- Busy Overlay ---

    /// <summary>
    /// Shows the modal busy overlay and starts the spinner. The card fades in over a
    /// short delay so work that finishes quickly never flashes a spinner at the user.
    /// </summary>
    private void ShowBusyOverlay(string title, string? detail = null)
    {
        BusyTitleText.Text = title;
        UpdateBusyOverlay(detail);

        BusyOverlay.Visibility = Visibility.Visible;
        BusyOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(180),
            Duration = new Duration(TimeSpan.FromMilliseconds(120))
        });

        BusySpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    /// <summary>
    /// Updates the overlay's detail line and, when <paramref name="total"/> is set, the
    /// "n of m" counter and progress bar. The card is a fixed size with reserved line
    /// heights, so nothing here can make it resize — text just trims.
    /// </summary>
    private void UpdateBusyOverlay(string? detail, int current = 0, int total = 0)
    {
        BusyDetailText.Text = detail ?? "";

        // A single-project workspace gets no counter or bar — there's nothing to track.
        if (total > 1)
        {
            BusyCountText.Text = $"Project {Math.Min(current, total)} of {total}";
            BusyProgressBar.Value = Math.Clamp((double)current / total * 100, 0, 100);
            BusyProgressBar.Visibility = Visibility.Visible;
        }
        else
        {
            BusyCountText.Text = "";
            BusyProgressBar.Value = 0;
            // Hidden, not collapsed: the bar keeps its slot so the card height is stable.
            BusyProgressBar.Visibility = Visibility.Hidden;
        }
    }

    private void HideBusyOverlay()
    {
        BusySpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        BusyOverlay.BeginAnimation(OpacityProperty, null);
        BusyOverlay.Opacity = 0;
        BusyOverlay.Visibility = Visibility.Collapsed;
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

        var recent = _workspaceStore.GetRecentWorkspaces();
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
                item.Click += async (_, _) =>
                {
                    if (!PromptSaveIfDirty())
                        return;
                    await OpenWorkspaceFile(capturedPath);
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

        var recent = _workspaceStore.GetRecentWorkspaces();
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
            button.Click += async (_, _) => await OpenWorkspaceFile(capturedPath);

            EmptyStateRecentList.Children.Add(button);
        }
    }

    // --- Toolbar Position ---

    private void SetToolbarPosition(string position)
    {
        if (position == _currentToolbarPosition)
            return;

        Dock dock;
        Orientation orientation;

        switch (position)
        {
            case "Top":
                dock = Dock.Top;
                orientation = Orientation.Horizontal;
                break;
            case "Bottom":
                dock = Dock.Bottom;
                orientation = Orientation.Horizontal;
                break;
            case "Left":
                dock = Dock.Left;
                orientation = Orientation.Vertical;
                break;
            case "Right":
                dock = Dock.Right;
                orientation = Orientation.Vertical;
                break;
            default:
                return;
        }

        DockPanel.SetDock(ToolbarBorder, dock);
        ToolbarPanel.Orientation = orientation;
        ApplyMetaTabLayoutForToolbarPosition(position);
        _currentToolbarPosition = position;
        // May drop the toolbar's outer rule when the group strip is already showing;
        // re-routes the project accent rail onto the strip's new content-facing edge.
        UpdateGroupFrame();
        UpdateProjectFrame();

        // Force DockPanel layout recalculation
        var children = new UIElement[WorkspaceInnerPanel.Children.Count];
        for (int i = 0; i < WorkspaceInnerPanel.Children.Count; i++)
            children[i] = WorkspaceInnerPanel.Children[i];

        WorkspaceInnerPanel.Children.Clear();
        foreach (var child in children)
            WorkspaceInnerPanel.Children.Add(child);
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

        _theme.ThemeChanged -= OnThemeChanged;

        // Stop all icon animation timers
        foreach (var timer in _tabIconTimers.Values)
            timer.Stop();
        _tabIconTimers.Clear();

        // Stop all file system watchers + detach theme handlers
        foreach (var gitControl in _projectGitControls.Values)
            gitControl.Cleanup();
        _projectGitControls.Clear();

        foreach (var previewControl in _projectPreviewControls.Values)
            previewControl.Cleanup();
        _projectPreviewControls.Clear();

        // Kill all Claude sessions gracefully
        foreach (var chatControl in _projectChatControls.Values)
            chatControl.Shutdown();

        _projectChatControls.Clear();
        _projectDescriptionControls.Clear();
    }

    // --- Theme ---

    private void OnThemeChanged(ThemeDescriptor theme)
    {
        using var perfScope = _perf.Time("OnThemeChanged");
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
                    var settings = _projectSettings.Load(project.FolderPath);
                    sp.Children.RemoveAt(0);
                    sp.Children.Insert(0, CreateIconElement(
                        settings.Icon ?? "folder", project.FolderPath,
                        settings.IconColor));
                }

                // Update tab text foreground (child 1)
                if (sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                    tb.Foreground = _theme.GetBrush("TabButtonForeground");
            }
        }

        // Re-apply tab icon foregrounds for no-session state
        foreach (var (project, iconGrid) in _projectTabIcons)
        {
            if (_projectChatControls.TryGetValue(project, out var chat))
                UpdateTabIcon(project, chat.CurrentState);
        }

        // Update toolbar + button accent
        _toolbarAddButton.BorderBrush = _theme.GetBrush("AddButtonBorderBrush");

        // Rebuild meta tab strip so its colors pick up the new theme
        RefreshMetaTabBar();

        // Update taskbar icon with new theme accent bar
        UpdateTaskbarIcon();
    }

    // --- Claude Code plan usage (/status Usage) ---

    private DispatcherTimer? _usageTimer;
    private DateTime? _lastUsageFetchTime;

    /// <summary>
    /// Plan-usage state for one login shown in the title-bar indicator. One entry is
    /// the machine default (~/.claude); the rest are signed-in named accounts. The
    /// last successful summary is cached separately so the popup can fall back to it
    /// when a refresh fails (e.g. 429, offline).
    /// </summary>
    private sealed class UsageAccount
    {
        public string? Id;                          // null = machine default
        public string Label = "";                   // "Default" or the account's name
        public string? ConfigDir;                   // null = ~/.claude
        public UsageFetchResult? LastResult;
        public UsageSummary? LastSuccess;
        public DateTime? RetryAfterUtc;             // set on HTTP 429 — don't re-fetch until this passes
    }

    private List<UsageAccount> _usageAccounts = new();

    /// <summary>
    /// True when a session has consumed API usage since the last fetch, meaning
    /// the /api/oauth/usage response would be stale. When false, the timer tick
    /// just re-renders the countdown text without hitting the endpoint —
    /// avoiding 429s while AgentDock is idle.
    /// </summary>
    private bool _usageDirty;

    private void InitializeUsageIndicator()
    {
        // Initial fetch primes the indicator; subsequent fetches are demand-driven.
        _ = RefreshUsageAsync();

        // Tick at 90s. On each tick: fetch only if a session has been active
        // since the last fetch; otherwise just re-render the countdown text.
        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _usageTimer.Tick += async (_, _) =>
        {
            if (_usageDirty)
                await RefreshUsageAsync();
            else
                UpdateUsageTitleText();
        };
        _usageTimer.Start();

        Closed += (_, _) => _usageTimer?.Stop();
    }

    /// <summary>
    /// Rebuilds the list of logins to show usage for: the machine default plus every
    /// signed-in named account. Cached results are carried over by id so a rebuild
    /// (e.g. after adding an account) doesn't blank existing brackets. When no named
    /// accounts exist, only the default is tracked and the display stays single-line.
    /// </summary>
    private void RebuildUsageAccounts()
    {
        UsageAccount Carry(string? id, string label, string? configDir)
        {
            var existing = _usageAccounts.FirstOrDefault(a => a.Id == id);
            if (existing != null)
            {
                existing.Label = label;
                existing.ConfigDir = configDir;
                return existing;
            }
            return new UsageAccount { Id = id, Label = label, ConfigDir = configDir };
        }

        var named = _accounts.Load();

        var updated = new List<UsageAccount>();

        // Machine default (~/.claude). Shown only while no named accounts are
        // configured, so single-account users keep the original detailed line and
        // its "sign in to Claude" states. The moment the user configures even one
        // named account, the indicator switches to tracking only those accounts —
        // the default is hidden to avoid a confusing extra bracket that duplicates
        // (or competes with) the accounts the user explicitly set up.
        if (named.Count == 0)
            updated.Add(Carry(null, "Default", null));

        foreach (var a in named)
        {
            if (!_accounts.IsLoggedIn(a.Id))
                continue;
            updated.Add(Carry(a.Id, a.Name, _accounts.ConfigDirFor(a.Id)));
        }

        _usageAccounts = updated;
    }

    private async Task RefreshUsageAsync()
    {
        // Clear dirty on attempt (not success). If we got 429/error, don't retry
        // immediately — wait for the next real session message to mark it dirty again.
        _usageDirty = false;

        RebuildUsageAccounts();

        // The usage endpoint rate-limits by source, so fetch accounts one at a time with
        // a small stagger rather than bursting them concurrently — a simultaneous burst
        // trips a 429 on one of the in-flight requests. Accounts still inside a prior
        // 429's Retry-After window are skipped (their cached result keeps showing).
        var now = DateTime.UtcNow;
        var firstFetch = true;
        foreach (var acct in _usageAccounts)
        {
            if (acct.RetryAfterUtc is { } until && until > now)
                continue;

            if (!firstFetch)
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            firstFetch = false;

            var result = await _usage.FetchAsync(acct.ConfigDir);
            acct.LastResult = result;

            if (result.Status == UsageFetchStatus.Success && result.Summary != null)
            {
                acct.LastSuccess = result.Summary;
                acct.RetryAfterUtc = null;
            }
            else if (result.Status == UsageFetchStatus.RateLimited)
            {
                // Back off this account until the server's Retry-After (default 60s).
                acct.RetryAfterUtc = DateTime.UtcNow + (result.RetryAfter ?? TimeSpan.FromSeconds(60));
            }
        }

        _lastUsageFetchTime = DateTime.Now;

        UpdateUsageTitleText();
        if (UsagePopup.IsOpen)
            RenderUsageDetails();
    }

    private void UpdateUsageTitleText()
    {
        var accts = _usageAccounts;
        if (accts.Count == 0)
        {
            UsageText.Text = "Session: —";
            return;
        }

        // Single-account users (default login only) keep the original detailed text.
        if (accts.Count == 1 && accts[0].Id == null)
        {
            var result = accts[0].LastResult;
            UsageText.Text = result == null ? "Session: —" : result.Status switch
            {
                UsageFetchStatus.AuthMissing => "Session: sign in to Claude",
                UsageFetchStatus.AuthExpired => "Session: auth expired",
                UsageFetchStatus.NetworkError => "Session: offline",
                UsageFetchStatus.RateLimited => "Session: rate limited",
                UsageFetchStatus.ServerError => "Session: error",
                UsageFetchStatus.Success => FormatSessionHeader(result.Summary),
                // Stale token: nothing is wrong with the login, we just can't read a
                // fresh figure. Keep showing the last one rather than raising an alarm.
                UsageFetchStatus.TokenStale => FormatSessionHeader(accts[0].LastSuccess),
                _ => "Session: —",
            };
            return;
        }

        // Multiple logins: one compact 5-hour bracket each, e.g. "[Default 57%] [Work 23%]".
        // Each bracket is tinted with the account's accent colour so it lines up with
        // that account's group box in the popup and is easy to pick out at a glance.
        UsageText.Inlines.Clear();
        for (var i = 0; i < accts.Count; i++)
        {
            if (i > 0)
                UsageText.Inlines.Add(new Run(" "));
            UsageText.Inlines.Add(new Run($"[{accts[i].Label} {FormatCompactPct(accts[i])}]")
            {
                Foreground = AccountAccentBrush(i),
            });
        }
    }

    // Distinct accent colours assigned to accounts by position so each account's
    // group box, progress bars and title-bar bracket share one easy-to-spot colour.
    // Mid-saturation hues chosen to stay legible on both the light and dark themes.
    private readonly Color[] AccountAccentColors =
    {
        Color.FromRgb(0x4C, 0x9A, 0xFF), // blue
        Color.FromRgb(0x36, 0xB3, 0x7E), // green
        Color.FromRgb(0xF2, 0x99, 0x2A), // amber
        Color.FromRgb(0xE5, 0x5C, 0x4C), // red
        Color.FromRgb(0x9F, 0x7A, 0xEA), // purple
        Color.FromRgb(0x00, 0xB8, 0xD9), // teal
        Color.FromRgb(0xE0, 0x62, 0xA0), // pink
        Color.FromRgb(0xC9, 0x8A, 0x3B), // bronze
    };

    private Color AccountAccentColor(int index) =>
        AccountAccentColors[index % AccountAccentColors.Length];

    /// <summary>Solid accent brush for the account at <paramref name="index"/> (frozen, reusable).</summary>
    private Brush AccountAccentBrush(int index)
    {
        var brush = new SolidColorBrush(AccountAccentColor(index));
        brush.Freeze();
        return brush;
    }

    /// <summary>Faint wash of the account's accent colour, for the group-box fill.</summary>
    private Brush AccountTintBrush(int index)
    {
        var c = AccountAccentColor(index);
        var brush = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>Compact 5-hour figure for one login: its percentage, cached value on a
    /// transient failure, or a short status word when there's nothing to show.</summary>
    private string FormatCompactPct(UsageAccount acct)
    {
        var summary = acct.LastResult?.Status == UsageFetchStatus.Success
            ? acct.LastResult.Summary
            : acct.LastSuccess;

        var util = summary?.FiveHour?.Utilization;
        if (util != null)
            return $"{Math.Round(util.Value):0}%";

        return acct.LastResult?.Status switch
        {
            UsageFetchStatus.AuthMissing => "sign in",
            UsageFetchStatus.AuthExpired => "auth",
            UsageFetchStatus.NetworkError => "offline",
            UsageFetchStatus.RateLimited => "wait",
            // TokenStale falls through to "—": the cached percentage above is preferred
            // when there is one, and a healthy account shouldn't be flagged when there isn't.
            _ => "—",
        };
    }

    private string FormatSessionHeader(UsageSummary? summary)
    {
        var fh = summary?.FiveHour;
        if (fh?.Utilization == null || fh.ResetsAt == null)
            return "Session not started";

        var pct = Math.Round(fh.Utilization.Value);
        var reset = FormatTimeUntilReset(fh.ResetsAt.Value);
        return $"5h: {pct:0}% · resets in {reset}";
    }

    private string FormatTimeUntilReset(DateTimeOffset resetsAt)
    {
        var delta = resetsAt - DateTimeOffset.Now;
        if (delta <= TimeSpan.Zero)
            return "now";
        if (delta.TotalDays >= 1)
            return $"{(int)delta.TotalDays}d {delta.Hours}h";
        if (delta.TotalHours >= 1)
            return $"{(int)delta.TotalHours}h {delta.Minutes}m";
        return $"{Math.Max(1, (int)delta.TotalMinutes)}m";
    }

    private async void UsageButton_Click(object sender, RoutedEventArgs e)
    {
        // Show popup immediately with cached data. Only hit the network if the last
        // fetch is stale — opening the popup shouldn't re-poll the (rate-limited)
        // endpoint each time. The 90s timer and the explicit Refresh button cover
        // fresh pulls.
        RenderUsageDetails();
        UsagePopup.IsOpen = true;

        if (_lastUsageFetchTime is DateTime t && DateTime.Now - t < TimeSpan.FromSeconds(60))
            return;

        await RefreshUsageAsync();
    }

    private async void UsageRefresh_Click(object sender, RoutedEventArgs e)
    {
        UsageRefreshButton.IsEnabled = false;
        UsageRefreshButton.Content = "Refreshing…";
        try
        {
            await RefreshUsageAsync();
        }
        finally
        {
            UsageRefreshButton.IsEnabled = true;
            UsageRefreshButton.Content = "Refresh";
        }
    }

    private void RenderUsageDetails()
    {
        UsageDetailPanel.Children.Clear();

        if (_usageAccounts.Count == 0)
        {
            UsageDetailPanel.Children.Add(new TextBlock
            {
                Text = "Loading…",
                FontSize = 11,
                Foreground = (Brush)FindResource("TitleBarForeground"),
            });
            UsageFooterText.Text = "";
            return;
        }

        // Wrap each login in its own accent-coloured group box only when more than
        // one login is tracked; a single default login keeps the original flat layout.
        var multi = _usageAccounts.Count > 1 || _usageAccounts[0].Id != null;
        var anyCached = false;

        for (var i = 0; i < _usageAccounts.Count; i++)
        {
            var acct = _usageAccounts[i];

            if (multi)
            {
                var accent = AccountAccentBrush(i);

                var body = new StackPanel();
                if (RenderAccountDetail(acct, body, accent))
                    anyCached = true;

                UsageDetailPanel.Children.Add(new GroupBox
                {
                    Style = (Style)FindResource("UsageAccountGroupBox"),
                    BorderBrush = accent,
                    Background = AccountTintBrush(i),
                    Margin = new Thickness(0, i == 0 ? 2 : 14, 0, 0),
                    Header = new TextBlock
                    {
                        Text = acct.Label,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Foreground = accent,
                    },
                    Content = body,
                });
            }
            else if (RenderAccountDetail(acct, UsageDetailPanel, null))
            {
                anyCached = true;
            }
        }

        var footer = _lastUsageFetchTime is DateTime time ? $"Last updated: {time:HH:mm:ss}" : "";
        if (anyCached)
            footer = string.IsNullOrEmpty(footer)
                ? "Showing cached data after a failed refresh"
                : "Cached data shown for some logins · " + footer;
        UsageFooterText.Text = footer;
    }

    /// <summary>
    /// Renders one login's usage rows into <paramref name="target"/>. When
    /// <paramref name="barBrush"/> is non-null, the progress bars are tinted with it so
    /// they match the account's group box. Returns true if it fell back to cached data
    /// because the latest fetch for this login failed.
    /// </summary>
    private bool RenderAccountDetail(UsageAccount acct, Panel target, Brush? barBrush)
    {
        var fg = (Brush)FindResource("TitleBarForeground");
        var result = acct.LastResult;

        if (result == null)
        {
            target.Children.Add(new TextBlock { Text = "Loading…", FontSize = 11, Foreground = fg });
            return false;
        }

        // On failure, fall back to this login's last successful summary (if any),
        // shown beneath an error banner.
        var isError = result.Status != UsageFetchStatus.Success || result.Summary == null;
        var summary = isError ? acct.LastSuccess : result.Summary;
        var usedCache = isError && summary != null;

        if (isError)
        {
            var msg = result.Status switch
            {
                UsageFetchStatus.AuthMissing => "Not signed in. Log in to this account from Claude Accounts.",
                UsageFetchStatus.AuthExpired => "OAuth token was rejected. Log in to this account again.",
                UsageFetchStatus.NetworkError => "Could not reach api.anthropic.com. Check your connection.",
                UsageFetchStatus.RateLimited => "Usage rate-limited (HTTP 429). Showing cached data; will retry shortly.",
                // Not a fault: the CLI renews this token the next time you send a message.
                UsageFetchStatus.TokenStale =>
                    "Usage figure is stale — the stored token has lapsed and refreshes on next use. This account still works.",
                _ => $"Could not fetch usage ({result.ErrorMessage})",
            };
            target.Children.Add(new TextBlock
            {
                Text = msg,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = fg,
                Margin = new Thickness(0, 0, 0, summary != null ? 10 : 0),
            });
        }

        if (summary == null)
            return false;

        AddUsageRow(target, "5-hour session", summary.FiveHour, barBrush, isPrimary: true,
            windowDuration: TimeSpan.FromHours(5));
        AddUsageRow(target, "7-day weekly", summary.SevenDay, barBrush);

        // Per-model breakdown (show only if populated)
        if (summary.SevenDayOpus?.Utilization != null)
            AddUsageRow(target, "7-day Opus", summary.SevenDayOpus, barBrush);
        if (summary.SevenDaySonnet?.Utilization != null)
            AddUsageRow(target, "7-day Sonnet", summary.SevenDaySonnet, barBrush);

        // Extra usage (if on)
        if (summary.ExtraUsage?.IsEnabled == true && (summary.ExtraUsage.UsedCredits ?? 0) > 0)
        {
            var currency = summary.ExtraUsage.Currency ?? "";
            var credits = summary.ExtraUsage.UsedCredits ?? 0;
            target.Children.Add(new TextBlock
            {
                Text = $"Extra usage: {credits:0} {currency} credits used",
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = fg,
            });
        }

        return usedCache;
    }

    private void AddUsageRow(Panel target, string label, UsageWindow? window, Brush? barBrush = null, bool isPrimary = false, TimeSpan? windowDuration = null)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = isPrimary ? 12 : 11,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource("TitleBarForeground"),
        };
        Grid.SetColumn(labelBlock, 0);
        header.Children.Add(labelBlock);

        var resetText = window?.ResetsAt != null
            ? $"resets in {FormatTimeUntilReset(window.ResetsAt.Value)}"
            : "";
        var resetBlock = new TextBlock
        {
            Text = resetText,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TitleBarForeground"),
        };
        Grid.SetColumn(resetBlock, 1);
        header.Children.Add(resetBlock);

        container.Children.Add(header);

        // Progress bar
        var pct = window?.Utilization ?? 0;
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Min(pct, 100),
            Height = 6,
            Margin = new Thickness(0, 3, 0, 0),
        };
        if (barBrush != null)
            bar.Foreground = barBrush;
        container.Children.Add(bar);

        // A thinner "time" bar sitting just below the token bar: how far we are
        // through the fixed-length window (e.g. 3h into a 5h window = ~60%). If the
        // time bar is ahead of the token bar we're pacing fine; if the token bar is
        // well ahead, we're burning through the budget too fast.
        if (windowDuration is TimeSpan duration && duration > TimeSpan.Zero && window?.ResetsAt != null)
        {
            var remaining = window.ResetsAt.Value - DateTimeOffset.Now;
            var elapsed = duration - remaining;
            var timePct = Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, 0, 1) * 100;

            var timeBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = timePct,
                Height = 3,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x90, 0x2B)),
                ToolTip = $"Time elapsed in this window: {timePct:0}%",
            };
            container.Children.Add(timeBar);
        }

        var pctBlock = new TextBlock
        {
            Text = window?.Utilization != null ? $"{pct:0}%" : "—",
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)FindResource("TitleBarForeground"),
        };
        container.Children.Add(pctBlock);

        target.Children.Add(container);
    }
}
