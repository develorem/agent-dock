using System.IO;
using System.Text.Json;

namespace AgentDock.Services;

/// <summary>
/// Simple file logger. Reads config from appsettings.json.
/// Call Log.Init() once at app startup.
/// </summary>
public static class Log
{
    private static string? _logFilePath;
    private static readonly object Lock = new();

    public static void Init()
    {
        try
        {
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (!File.Exists(settingsPath))
            {
                // Fallback defaults
                _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs.txt");
                File.WriteAllText(_logFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] [INIT] Log started (no appsettings.json found, using default path)\n");
                return;
            }

            var json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            var logging = doc.RootElement.GetProperty("Logging");

            var rawPath = logging.GetProperty("LogFilePath").GetString() ?? "logs.txt";
            var clearOnStart = logging.GetProperty("ClearOnStart").GetBoolean();

            // Resolve relative paths from the exe directory
            _logFilePath = Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rawPath));

            // Ensure directory exists
            var dir = Path.GetDirectoryName(_logFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (clearOnStart && File.Exists(_logFilePath))
                File.Delete(_logFilePath);

            Write("INIT", $"Log started — file: {_logFilePath}");
        }
        catch (Exception ex)
        {
            // Last resort — try writing next to the exe
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs.txt");
            try { File.WriteAllText(_logFilePath, $"Log init error: {ex}\n"); } catch { }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_logFilePath == null) return;

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}\n";
        lock (Lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line);
            }
            catch
            {
                // Swallow — can't log a logging failure
            }
        }
    }
}
