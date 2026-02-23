using System.IO;

namespace AgentDock.Services;

/// <summary>
/// Simple file logger. Creates a per-session log file in the logs folder.
/// Call Log.Init() once at app startup after parsing arguments.
/// </summary>
public static class Log
{
    private static string? _logFilePath;
    private static readonly object Lock = new();

    /// <summary>
    /// The full path to the current session's log file.
    /// </summary>
    public static string? LogFilePath => _logFilePath;

    /// <summary>
    /// Initializes the logger with a per-session log file.
    /// </summary>
    /// <param name="logsFolder">
    /// Directory to store logs. If null, defaults to a "logs" folder next to the exe.
    /// </param>
    /// <param name="sessionContext">
    /// Optional context string (folder name or workspace name) included in the file name.
    /// </param>
    public static void Init(string? logsFolder = null, string? sessionContext = null)
    {
        try
        {
            logsFolder ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logsFolder);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var safeName = SanitizeFileName(sessionContext);
            var fileName = string.IsNullOrEmpty(safeName)
                ? $"{timestamp}.log"
                : $"{timestamp}_{safeName}.log";

            _logFilePath = Path.Combine(logsFolder, fileName);

            Write("INIT", $"Log started — file: {_logFilePath}");
        }
        catch (Exception ex)
        {
            // Last resort — try writing next to the exe
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent-dock.log");
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

    private static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];

        return new string(sanitized).Trim();
    }
}
