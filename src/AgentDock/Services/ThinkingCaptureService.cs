using System.IO;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <inheritdoc cref="IThinkingCaptureService"/>
public sealed class ThinkingCaptureService : IThinkingCaptureService
{
    private string? _logsFolder;

    public bool Enabled { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    public void Init(string? logsFolder) => _logsFolder = logsFolder;

    public IThinkingCaptureWriter? OpenCapture(string workingDirectory)
    {
        if (!Enabled) return null;

        try
        {
            var baseFolder = _logsFolder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            var folder = Path.Combine(baseFolder, "thinking-capture");
            Directory.CreateDirectory(folder);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var safeName = SanitizeFileName(Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            var fileName = string.IsNullOrEmpty(safeName) ? $"{timestamp}.jsonl" : $"{timestamp}_{safeName}.jsonl";

            return new ThinkingCaptureWriter(Path.Combine(folder, fileName));
        }
        catch
        {
            // A broken capture file must never take down the session it's observing.
            return null;
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

internal sealed class ThinkingCaptureWriter(string path) : IThinkingCaptureWriter
{
    private readonly object _lock = new();
    private bool _disposed;

    public void WriteLine(string rawJsonLine)
    {
        lock (_lock)
        {
            if (_disposed) return;

            try
            {
                // WriteThrough mirrors LogService — bypasses the OS write cache so a line
                // survives even if the process is killed moments later.
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, FileOptions.WriteThrough);
                var bytes = System.Text.Encoding.UTF8.GetBytes(rawJsonLine + "\n");
                fs.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // Swallow — capture is a debugging aid, never a reason to break the session.
            }
        }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; }
    }
}
