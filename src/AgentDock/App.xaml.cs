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

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Init();
        Log.Info("Application starting");

        // Parse command-line arguments
        if (!ParseArguments(e.Args))
        {
            Shutdown(0);
            return;
        }

        ThemeManager.Initialize();

        // Catch unhandled exceptions on the UI thread
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Catch unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Catch unobserved task exceptions
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Parses command-line arguments. Returns false if the app should exit (e.g. --help).
    /// </summary>
    private static bool ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg.ToLowerInvariant())
            {
                case "--help" or "-h" or "-?" or "/?" or "/help":
                    ShowHelp();
                    return false;

                case "--version" or "-v":
                    ShowVersion();
                    return false;

                case "--workspace" or "-w":
                    if (i + 1 < args.Length)
                    {
                        var path = args[++i];
                        if (File.Exists(path))
                        {
                            StartupWorkspacePath = path;
                            Log.Info($"Startup workspace: {path}");
                        }
                        else
                        {
                            WriteConsole($"Error: workspace file not found: {path}");
                            return false;
                        }
                    }
                    else
                    {
                        WriteConsole("Error: --workspace requires a file path argument.");
                        return false;
                    }
                    break;

                case "--folder" or "-f":
                    if (i + 1 < args.Length)
                    {
                        var path = args[++i];
                        if (Directory.Exists(path))
                        {
                            StartupProjectFolders.Add(Path.GetFullPath(path));
                            Log.Info($"Startup project folder: {path}");
                        }
                        else
                        {
                            WriteConsole($"Error: folder not found: {path}");
                            return false;
                        }
                    }
                    else
                    {
                        WriteConsole("Error: --folder requires a folder path argument.");
                        return false;
                    }
                    break;

                default:
                    // Bare argument: treat as workspace file if .agentdock, or folder
                    if (arg.EndsWith(".agentdock", StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
                    {
                        StartupWorkspacePath = arg;
                        Log.Info($"Startup workspace: {arg}");
                    }
                    else if (Directory.Exists(arg))
                    {
                        StartupProjectFolders.Add(Path.GetFullPath(arg));
                        Log.Info($"Startup project folder: {arg}");
                    }
                    else
                    {
                        WriteConsole($"Warning: ignoring unknown argument: {arg}");
                    }
                    break;
            }
        }

        return true;
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
              -h, --help                Show this help message and exit
              -v, --version             Show version information and exit

            Examples:
              AgentDock.exe                                   Launch with no projects
              AgentDock.exe mywork.agentdock                  Open a saved workspace
              AgentDock.exe C:\Projects\MyApp                 Open a project folder
              AgentDock.exe -f ProjectA -f ProjectB           Open multiple projects
              AgentDock.exe -w mywork.agentdock               Open workspace (explicit)
            """);
    }

    public static string Version =>
        typeof(App).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    private static void ShowVersion()
    {
        WriteConsole($"Agent Dock version {Version}");
    }

    private static void WriteConsole(string message)
    {
        AttachConsole(AttachParentProcess);
        Console.WriteLine(message);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UNHANDLED UI EXCEPTION", e.Exception);
        e.Handled = true; // Prevent crash so we can read the log
        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}\n\nSee logs.txt for details.",
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
}
