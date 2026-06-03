using System.Diagnostics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentDock.Services;

/// <summary>
/// Always-on, low-overhead performance instrumentation for the UI thread.
///
/// Replaces the earlier single-sample keystroke probe (which under-sampled the
/// exact bursts it was meant to catch). This logs:
///   • PERF env     — render tier (GPU vs software), GPU caps, OS/CPU, session
///                     name (catches RDP/VM software-rendering fallback at startup).
///   • PERF ui-stall — whenever the UI thread is blocked longer than a threshold,
///                     with a snapshot of what was running (working sessions, git
///                     calls in flight, markdown builds in flight, last git cmd).
///   • PERF health  — a periodic sample of working set, GC heap, handle/thread
///                     count, live/working session counts and the workspace-dirty
///                     fan-out (surfaces the Layout.Updated handler accumulation).
///   • PERF op / git-slow — per-operation timings above a threshold.
///
/// Everything is gated on <see cref="Enabled"/> and only writes a log line when a
/// threshold is crossed, so the steady-state cost is a few timer ticks per second.
/// All counters are updated with interlocked ops so background threads (the git
/// caller, the session read loop) can poke them safely.
/// </summary>
public static class PerfDiagnostics
{
    /// <summary>Master switch. Defaults on; set false to silence all PERF output.</summary>
    public static bool Enabled { get; set; } = true;

    // --- Cross-thread live counters ---
    private static int _liveSessions;
    private static int _workingSessions;
    private static int _gitOpsInFlight;
    private static int _markdownBuildsInFlight;
    private static long _workspaceDirtyCalls;
    private static string _lastGit = "(none)";

    public static int LiveSessions => Volatile.Read(ref _liveSessions);
    public static int WorkingSessions => Volatile.Read(ref _workingSessions);

    public static void SessionCreated() => Interlocked.Increment(ref _liveSessions);
    public static void SessionDisposed() => Interlocked.Decrement(ref _liveSessions);

    /// <summary>Adjusts the global "sessions currently Working" count (across all tabs).</summary>
    public static void WorkingSessionDelta(int delta) => Interlocked.Add(ref _workingSessions, delta);

    /// <summary>Counts every SetWorkspaceDirty invocation, including no-op early
    /// returns — a climbing per-interval count means Layout.Updated handlers are
    /// accumulating (the known per-project subscription leak).</summary>
    public static void NoteWorkspaceDirty() => Interlocked.Increment(ref _workspaceDirtyCalls);

    public static void MarkdownBuildDelta(int delta) => Interlocked.Add(ref _markdownBuildsInFlight, delta);

    // --- Git op instrumentation (called from GitService, often off the UI thread) ---
    public static void GitOpStart() => Interlocked.Increment(ref _gitOpsInFlight);

    public static void GitOpEnd(string command, double ms, int threadId)
    {
        Interlocked.Decrement(ref _gitOpsInFlight);
        Volatile.Write(ref _lastGit, $"'{command}' {ms:F0}ms@T{threadId:D2}");
        if (Enabled && ms >= GitSlowThresholdMs)
            Log.Warn($"PERF git-slow {ms:F0}ms onThread=T{threadId:D2} cmd='{command}' | {Snapshot()}");
    }

    // --- Timers (UI thread) ---
    private static DispatcherTimer? _stallTimer;
    private static DispatcherTimer? _healthTimer;
    private static readonly Stopwatch StallStopwatch = new();

    private const int StallIntervalMs = 250;
    private const int StallThresholdMs = 150;   // overrun beyond the interval that counts as a stall
    private const int HealthIntervalSec = 30;
    private const int GitSlowThresholdMs = 200;

    /// <summary>
    /// Starts the UI-thread stall monitor and the periodic health sampler. Call
    /// once from the UI thread after the Dispatcher is running. Safe to call twice.
    /// </summary>
    public static void Start()
    {
        if (!Enabled || _stallTimer != null) return;

        // UI-thread stall monitor. A DispatcherTimer scheduled at Input priority
        // ticks every StallIntervalMs. If the UI thread is blocked (e.g. inside a
        // synchronous `git status`, or a multi-hundred-ms markdown build), the tick
        // can't be serviced until the thread returns to the dispatcher loop, so the
        // measured gap exceeds the interval. The overrun is, to first order, how
        // long a user keystroke/click would have been starved at that moment.
        StallStopwatch.Restart();
        _stallTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(StallIntervalMs)
        };
        _stallTimer.Tick += (_, _) =>
        {
            var elapsed = StallStopwatch.Elapsed.TotalMilliseconds;
            StallStopwatch.Restart();
            var overrun = elapsed - StallIntervalMs;
            if (overrun >= StallThresholdMs)
                Log.Warn($"PERF ui-stall {overrun:F0}ms (gap {elapsed:F0}ms) | {Snapshot()}");
        };
        _stallTimer.Start();

        _healthTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(HealthIntervalSec)
        };
        _healthTimer.Tick += (_, _) => LogHealth();
        _healthTimer.Start();

        Log.Info($"PERF monitor started — stall>{StallThresholdMs}ms, health every {HealthIntervalSec}s");
    }

    public static void Stop()
    {
        _stallTimer?.Stop();
        _stallTimer = null;
        _healthTimer?.Stop();
        _healthTimer = null;
    }

    /// <summary>One-time startup snapshot of the rendering + machine environment.</summary>
    public static void LogEnvironment()
    {
        if (!Enabled) return;
        try
        {
            // High word of RenderCapability.Tier is the tier: 0 = no GPU accel
            // (pure software — makes ALL of WPF sluggish), 1 = partial, 2 = full.
            var tier = RenderCapability.Tier >> 16;
            var maxTex = RenderCapability.MaxHardwareTextureSize;
            Log.Info($"PERF env render | tier={tier} (0=software 1=partial 2=full-hw) " +
                     $"maxTexture={maxTex.Width}x{maxTex.Height} " +
                     $"ps3.0={RenderCapability.IsPixelShaderVersionSupported(3, 0)} " +
                     $"ps2.0={RenderCapability.IsPixelShaderVersionSupported(2, 0)}");

            var sessionName = Environment.GetEnvironmentVariable("SESSIONNAME") ?? "(unset)";
            Log.Info($"PERF env machine | os={Environment.OSVersion.VersionString} cpu={Environment.ProcessorCount} " +
                     $"64bit={Environment.Is64BitProcess} clr={Environment.Version} session={sessionName}");
            if (sessionName.StartsWith("RDP", StringComparison.OrdinalIgnoreCase))
                Log.Warn("PERF env — running in an RDP session; WPF may be using software rendering (tier 0).");

            // Tier can change at runtime (GPU reset, RDP connect/disconnect).
            RenderCapability.TierChanged += (_, _) =>
                Log.Warn($"PERF env render tier CHANGED -> {RenderCapability.Tier >> 16}");
        }
        catch (Exception ex)
        {
            Log.Warn($"PERF env probe failed: {ex.Message}");
        }
    }

    public static void LogHealth()
    {
        if (!Enabled) return;
        try
        {
            using var p = Process.GetCurrentProcess();
            var wkset = p.WorkingSet64 / (1024 * 1024);
            var gcHeap = GC.GetTotalMemory(false) / (1024 * 1024);
            var dirty = Interlocked.Exchange(ref _workspaceDirtyCalls, 0);
            Log.Info($"PERF health | wkset={wkset}MB gcHeap={gcHeap}MB handles={p.HandleCount} " +
                     $"threads={p.Threads.Count} | {Snapshot()} | dirtyCalls/{HealthIntervalSec}s={dirty}");
        }
        catch (Exception ex)
        {
            Log.Warn($"PERF health sample failed: {ex.Message}");
        }
    }

    private static string Snapshot()
        => $"sessions={LiveSessions} working={WorkingSessions} " +
           $"gitInFlight={Volatile.Read(ref _gitOpsInFlight)} " +
           $"mdBuilds={Volatile.Read(ref _markdownBuildsInFlight)} lastGit={Volatile.Read(ref _lastGit)}";

    /// <summary>
    /// Times a synchronous operation; logs a PERF line if it runs longer than
    /// <paramref name="thresholdMs"/>. Use with <c>using</c> so all exit paths
    /// (including early returns) are covered:
    /// <code>using var _ = PerfDiagnostics.Time("SwitchToProject");</code>
    /// </summary>
    public static OperationTimer Time(string name, double thresholdMs = 50)
        => new(name, thresholdMs);

    public readonly struct OperationTimer(string name, double thresholdMs) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            if (!Enabled) return;
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if (ms >= thresholdMs)
                Log.Warn($"PERF op '{name}' {ms:F0}ms onThread=T{Thread.CurrentThread.ManagedThreadId:D2} | {Snapshot()}");
        }
    }
}
