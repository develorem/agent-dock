namespace AgentDock.Services.Abstractions;

/// <summary>
/// Captures the complete raw NDJSON stream a <see cref="ClaudeSession"/> reads from the
/// <c>claude</c> CLI (system/assistant/stream_event/result/control_request — every line,
/// unfiltered) to a file, for offline analysis of the stream-json protocol shape. This is
/// deliberately broader than just "thinking" events: the surrounding context (what triggered
/// a thinking block, what followed it) is what makes the capture useful.
///
/// Enabled automatically in Debug builds (so it's always on when run from Visual Studio) and
/// disabled in Release builds — there is no runtime toggle, since this is a development aid,
/// not a user-facing feature.
/// </summary>
public interface IThinkingCaptureService
{
    bool Enabled { get; }

    /// <summary>
    /// Called once at startup with the same folder the main log file lives in, so capture
    /// files land next to it. Safe to skip (capture falls back to a "logs" folder next to
    /// the exe) — exists mainly so capture respects the <c>--logs</c> override.
    /// </summary>
    void Init(string? logsFolder);

    /// <summary>
    /// Opens a new capture file for one <see cref="ClaudeSession"/>'s lifetime, or returns
    /// null when <see cref="Enabled"/> is false or the file couldn't be created.
    /// </summary>
    IThinkingCaptureWriter? OpenCapture(string workingDirectory);
}

/// <summary>One capture file, spanning every one-shot process a <see cref="ClaudeSession"/> spawns.</summary>
public interface IThinkingCaptureWriter : IDisposable
{
    /// <summary>Appends one raw NDJSON line exactly as read from the CLI's stdout.</summary>
    void WriteLine(string rawJsonLine);
}
