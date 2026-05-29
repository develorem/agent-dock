using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AgentDock.Services;

namespace AgentDock;

public partial class App : Application
{
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

        Log.Init(StartupLogsFolder, sessionContext);
        Log.Info("Application starting");

        if (StartupWorkspacePath != null)
            Log.Info($"Startup workspace: {StartupWorkspacePath}");
        foreach (var folder in StartupProjectFolders)
            Log.Info($"Startup project folder: {folder}");

        ThemeManager.Initialize();

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
        Log.Error("UNHANDLED UI EXCEPTION", e.Exception);
        e.Handled = true; // Prevent crash so we can read the log

        var logRef = Log.LogFilePath != null
            ? $"\n\nSee {Log.LogFilePath} for details."
            : "";

        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}{logRef}",
            "Agent Dock Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Error("UNHANDLED BACKGROUND EXCEPTION", ex);
        else
            Log.Error($"UNHANDLED BACKGROUND EXCEPTION: {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("UNOBSERVED TASK EXCEPTION", e.Exception);
        e.SetObserved();
    }

    private static void OnDispatcherUnhandledExceptionFilter(object sender, DispatcherUnhandledExceptionFilterEventArgs e)
    {
        // Logs before DispatcherUnhandledException runs, so we still capture
        // the throw if the main handler is somehow bypassed.
        Log.Error("DISPATCHER EXCEPTION (pre-filter)", e.Exception);
    }

    private static void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        // Avoid recursive logging if Log.Write itself throws.
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
            Log.Warn($"FIRST-CHANCE {ex.GetType().Name}: {ex.Message} | {firstFrame}");
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

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        Log.Info("Process exiting (clean shutdown)");
    }
}
