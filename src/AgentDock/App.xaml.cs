using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AgentDock.Services;
using AgentDock.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentDock;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogService? _log;

    public static string? StartupWorkspacePath { get; private set; }
    public static List<string> StartupProjectFolders { get; } = [];
    public static string? StartupLogsFolder { get; private set; }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    private const int AttachParentProcess = -1;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Disable the per-user foreground-lock timeout so the shell can always
        // bring our window to the front (taskbar click on a minimized window).
        // Without this, a long-running instance whose foreground privilege has
        // expired plays the default-beep ding and refuses to restore from the
        // taskbar. SPIF flags = 0 means in-memory only, not persisted to the
        // registry — the setting resets next reboot.
        SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, 0);

        // Parse command-line arguments before initializing the logger,
        // so we know the logs folder and session context.
        var result = ParseArguments(e.Args);

        if (result == ParseResult.Exit)
        {
            Shutdown(0);
            return;
        }

        if (result == ParseResult.Error)
        {
            Shutdown(1);
            return;
        }

        // Determine session context for log file name
        string? sessionContext = null;
        if (StartupWorkspacePath != null)
            sessionContext = Path.GetFileNameWithoutExtension(StartupWorkspacePath);
        else if (StartupProjectFolders.Count > 0)
            sessionContext = Path.GetFileName(StartupProjectFolders[0]);

        // Composition root: build the container, then resolve the services startup needs.
        // Nothing below reaches for a service statically.
        _services = ServiceRegistration.BuildServiceProvider();

        _log = _services.GetRequiredService<ILogService>();
        _log.Init(StartupLogsFolder, sessionContext);
        _log.Info("Application starting");

        // Performance instrumentation: log the rendering/machine environment once
        // (catches software-rendering fallback) and start the UI-thread stall +
        // health monitors. See PerfDiagnostics.
        var perf = _services.GetRequiredService<IPerfDiagnostics>();
        perf.LogEnvironment();
        perf.Start();

        if (StartupWorkspacePath != null)
            _log.Info($"Startup workspace: {StartupWorkspacePath}");
        foreach (var folder in StartupProjectFolders)
            _log.Info($"Startup project folder: {folder}");

        _services.GetRequiredService<IThemeService>().Initialize();

        // Catch unhandled exceptions on the UI thread
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Fires before DispatcherUnhandledException — gives us a logging
        // toehold even if the main handler is bypassed or throws itself.
        Dispatcher.UnhandledExceptionFilter += OnDispatcherUnhandledExceptionFilter;

        // Catch unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Catch unobserved task exceptions
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Log every throw — even ones that get caught downstream. This is the
        // only handler that sees an exception before runtime escalation paths
        // (FailFast, StackOverflow) can terminate the process without firing
        // the unhandled handlers above.
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

        // Log normal process exit so we can distinguish clean shutdown from
        // crashes in the log file.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // MainWindow is constructed here rather than by StartupUri so its dependencies
        // are injected. ActivatorUtilities resolves the constructor from the container,
        // so the window declares what it needs instead of reaching for globals.
        var mainWindow = ActivatorUtilities.CreateInstance<MainWindow>(_services);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private enum ParseResult { Continue, Exit, Error }

    /// <summary>
    /// Parses command-line arguments.
    /// Returns Exit for --help/--version, Error for invalid input, Continue otherwise.
    /// </summary>
    private static ParseResult ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg.ToLowerInvariant())
            {
                case "--help" or "-h" or "-?" or "/?" or "/help":
                    ShowHelp();
                    return ParseResult.Exit;

                case "--version" or "-v":
                    ShowVersion();
                    return ParseResult.Exit;

                case "--logs" or "-l":
                    if (i + 1 < args.Length)
                    {
                        var path = args[++i];
                        if (!Directory.Exists(path))
                        {
                            WriteConsoleError($"Error: logs folder not found: {path}");
                            return ParseResult.Error;
                        }
                        StartupLogsFolder = Path.GetFullPath(path);
                    }
                    else
                    {
                        WriteConsoleError("Error: --logs requires a folder path argument.");
                        return ParseResult.Error;
                    }
                    break;

                case "--workspace" or "-w":
                    if (i + 1 < args.Length)
                    {
                        var path = args[++i];
                        if (File.Exists(path))
                        {
                            StartupWorkspacePath = Path.GetFullPath(path);
                        }
                        else
                        {
                            WriteConsoleError($"Error: workspace file not found: {path}");
                            return ParseResult.Error;
                        }
                    }
                    else
                    {
                        WriteConsoleError("Error: --workspace requires a file path argument.");
                        return ParseResult.Error;
                    }
                    break;

                case "--folder" or "-f":
                    if (i + 1 < args.Length)
                    {
                        var path = args[++i];
                        if (Directory.Exists(path))
                        {
                            StartupProjectFolders.Add(Path.GetFullPath(path));
                        }
                        else
                        {
                            WriteConsoleError($"Error: folder not found: {path}");
                            return ParseResult.Error;
                        }
                    }
                    else
                    {
                        WriteConsoleError("Error: --folder requires a folder path argument.");
                        return ParseResult.Error;
                    }
                    break;

                default:
                    // Bare argument: treat as workspace file if .agentdock, or folder
                    if (arg.EndsWith(".agentdock", StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(arg))
                        {
                            StartupWorkspacePath = Path.GetFullPath(arg);
                        }
                        else
                        {
                            WriteConsoleError($"Error: workspace file not found: {arg}");
                            return ParseResult.Error;
                        }
                    }
                    else if (Directory.Exists(arg))
                    {
                        StartupProjectFolders.Add(Path.GetFullPath(arg));
                    }
                    else
                    {
                        WriteConsoleError($"Error: unknown argument or path not found: {arg}");
                        return ParseResult.Error;
                    }
                    break;
            }
        }

        return ParseResult.Continue;
    }

    private static void ShowHelp()
    {
        WriteConsole("""
            Agent Dock — Manage multiple Claude Code AI sessions

            Usage:
              AgentDock.exe [options] [workspace.agentdock] [folder ...]

            Arguments:
              workspace.agentdock       Open a workspace file directly
              folder                    Open one or more project folders

            Options:
              -w, --workspace <file>    Open a workspace file (.agentdock)
              -f, --folder <folder>     Open a project folder (can be repeated)
              -l, --logs <folder>       Override the logs folder (must exist)
              -h, --help                Show this help message and exit
              -v, --version             Show version information and exit

            Examples:
              AgentDock.exe                                   Launch with no projects
              AgentDock.exe mywork.agentdock                  Open a saved workspace
              AgentDock.exe C:\Projects\MyApp                 Open a project folder
              AgentDock.exe -f ProjectA -f ProjectB           Open multiple projects
              AgentDock.exe -w mywork.agentdock               Open workspace (explicit)
              AgentDock.exe -l C:\MyLogs                      Use custom logs folder
            """);
    }

    public static string Version
    {
        get
        {
            var raw = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0";
            // Strip the +commithash suffix that .NET SDK appends to InformationalVersion
            var plusIndex = raw.IndexOf('+');
            return plusIndex >= 0 ? raw[..plusIndex] : raw;
        }
    }

    private static void ShowVersion()
    {
        WriteConsole($"Agent Dock version {Version}");
    }

    private static void WriteConsole(string message)
    {
        AttachConsole(AttachParentProcess);
        Console.WriteLine(message);
    }

    private static void WriteConsoleError(string message)
    {
        AttachConsole(AttachParentProcess);
        Console.Error.WriteLine(message);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("UNHANDLED UI EXCEPTION", e.Exception);
        e.Handled = true; // Prevent crash so we can read the log

        var logRef = _log?.LogFilePath != null
            ? $"\n\nSee {_log?.LogFilePath} for details."
            : "";

        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}{logRef}",
            "Agent Dock Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _log?.Error("UNHANDLED BACKGROUND EXCEPTION", ex);
        else
            _log?.Error($"UNHANDLED BACKGROUND EXCEPTION: {e.ExceptionObject}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("UNOBSERVED TASK EXCEPTION", e.Exception);
        e.SetObserved();
    }

    private void OnDispatcherUnhandledExceptionFilter(object sender, DispatcherUnhandledExceptionFilterEventArgs e)
    {
        // Logs before DispatcherUnhandledException runs, so we still capture
        // the throw if the main handler is somehow bypassed.
        _log?.Error("DISPATCHER EXCEPTION (pre-filter)", e.Exception);
    }

    private void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        // Avoid recursive logging if the logger itself throws.
        if (_inFirstChanceHandler) return;

        var ex = e.Exception;

        // OperationCanceledException / TaskCanceledException are routine flow
        // control (cancellation tokens, dispatcher shutdown) — skip to avoid
        // drowning the log.
        if (ex is OperationCanceledException) return;

        _inFirstChanceHandler = true;
        try
        {
            // First-chance fires for caught exceptions too — log at WARN with
            // type + message + first stack frame only, to keep noise down.
            // The full stack will be in the unhandled handler if it escapes.
            var firstFrame = ex.StackTrace?.Split('\n', 2)[0]?.Trim() ?? "(no stack)";
            _log?.Warn($"FIRST-CHANCE {ex.GetType().Name}: {ex.Message} | {firstFrame}");
        }
        catch
        {
            // Logging must never break the process.
        }
        finally
        {
            _inFirstChanceHandler = false;
        }
    }

    [ThreadStatic]
    private static bool _inFirstChanceHandler;

    private void OnProcessExit(object? sender, EventArgs e)
    {
        _log?.Info("Process exiting (clean shutdown)");
    }
}
