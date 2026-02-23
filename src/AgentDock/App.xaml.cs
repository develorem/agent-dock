using System.IO;
using System.Windows;
using System.Windows.Threading;
using AgentDock.Services;

namespace AgentDock;

public partial class App : Application
{
    public static string? StartupWorkspacePath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Init();
        Log.Info("Application starting");

        ThemeManager.Initialize();

        // Check for .agentdock file passed as argument (e.g., double-click from Explorer)
        if (e.Args.Length > 0
            && File.Exists(e.Args[0])
            && e.Args[0].EndsWith(".agentdock", StringComparison.OrdinalIgnoreCase))
        {
            StartupWorkspacePath = e.Args[0];
            Log.Info($"Startup workspace: {StartupWorkspacePath}");
        }

        // Catch unhandled exceptions on the UI thread
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Catch unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Catch unobserved task exceptions
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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
