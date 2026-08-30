namespace AgentDock.Services.Abstractions;

/// <summary>Session file logger.</summary>
public interface ILogService
{
    string? LogFilePath { get; }
    void Init(string? logsFolder = null, string? sessionContext = null);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception ex);
}

/// <summary>
/// System notification sounds. An instance service because the remote-session work needs
/// to decide *which machine* a session notification is heard on — a server driving a
/// remote client should be able to stay silent.
/// </summary>
public interface ISoundService
{
    void PlayDeviceConnect();
    void PlayDeviceDisconnect();
    void PlayMessageNudge();
    void PlayQuestionPrompt();
}

/// <summary>Builds the composited taskbar icon (app logo + themed accent bar).</summary>
public interface ITaskbarIconService
{
    System.Windows.Media.ImageSource CreateThemedIcon(System.Windows.Media.Color barColor);
}

/// <summary>UI-thread stall and process-health instrumentation.</summary>
public interface IPerfDiagnostics
{
    bool Enabled { get; set; }
    int LiveSessions { get; }
    int WorkingSessions { get; }

    void SessionCreated();
    void SessionDisposed();
    void WorkingSessionDelta(int delta);
    void NoteWorkspaceDirty();
    void MarkdownBuildDelta(int delta);
    void GitOpStart();
    void GitOpEnd(string command, double ms, int threadId);

    void Start();
    void Stop();
    void LogEnvironment();
    void LogHealth();

    /// <summary>
    /// Times a synchronous operation, logging a PERF line past the threshold. Returns
    /// <see cref="IDisposable"/> rather than the concrete timer struct so the abstraction
    /// does not leak an implementation type; the struct boxes, which is irrelevant at the
    /// call rates involved (all callers are coarse-grained operations).
    /// </summary>
    IDisposable Time(string name, double thresholdMs = 50);
}
