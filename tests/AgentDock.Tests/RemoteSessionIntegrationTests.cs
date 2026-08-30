using System.IO;
using System.Net;
using System.Windows.Threading;
using AgentDock.Services.Abstractions;
using AgentDock.Services.Remote;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// End-to-end tests for the remote session handshake, over a real loopback TCP connection with
/// real TLS and a real pairing exchange. Nothing here is mocked except the project provider.
///
/// WHY: the handshake is a five-frame ordered conversation across two state machines. Every unit
/// of it can be individually correct while the sequence deadlocks, admits the wrong peer, or
/// reports the wrong reason for a failure. These tests are the only place that is checked.
///
/// The dispatcher is real and pumped on a dedicated thread, because both services marshal to the
/// UI thread — a test that skipped that would pass while the product hung.
/// </summary>
public class RemoteSessionIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private Dispatcher _dispatcher = null!;
    private Thread _dispatcherThread = null!;
    private string _settingsDir = null!;

    private RemoteServerService _server = null!;
    private RemoteIdentity _serverIdentity = null!;

    public Task InitializeAsync()
    {
        var ready = new TaskCompletionSource();

        _dispatcherThread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.SetResult();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "remote-test-dispatcher",
        };

        _dispatcherThread.SetApartmentState(ApartmentState.STA);
        _dispatcherThread.Start();
        ready.Task.Wait(Timeout);

        // Each test run gets its own settings directory: the server certificate and the client's
        // known-hosts file are persisted, and leaking either between runs would make the
        // fingerprint-pinning behaviour depend on test order.
        _settingsDir = Path.Combine(Path.GetTempPath(), "agentdock-remote-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_settingsDir);

        _serverIdentity = new RemoteIdentity(new NullLog(), new FakeSettings(_settingsDir));
        _server = new RemoteServerService(new NullLog(), _serverIdentity);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        _dispatcher.InvokeShutdown();

        try { Directory.Delete(_settingsDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private static int FreePort()
        => RemoteServerService.FindFreePort(IPAddress.Loopback, Random.Shared.Next(20000, 45000))
           ?? throw new InvalidOperationException("no free port");

    private RemoteClientService NewClient(string? settingsDir = null)
        => new(new NullLog(), new RemoteIdentity(new NullLog(), new FakeSettings(settingsDir ?? _settingsDir)));

    private RemoteServerService.StartResult StartServer(int port)
        => _server.Start(port, RemoteBindScope.LoopbackOnly, new StubProjects(), _dispatcher);

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task ServerStartsAndReportsAPairingCode()
    {
        var result = StartServer(FreePort());

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Code);
        Assert.Equal(PairingCode.Length, result.Code!.Length);
        Assert.True(_server.IsRunning);
        Assert.False(_server.HasClient);
    }

    [Fact]
    public async Task StartingOnATakenPortFailsAndSuggestsAFreeOne()
    {
        var port = FreePort();
        var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        blocker.Start();

        try
        {
            var result = StartServer(port);

            Assert.False(result.Success);
            Assert.Contains("in use", result.Error);
            Assert.False(_server.IsRunning);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task ClientConnectsThenPairsWithTheCorrectCode()
    {
        var port = FreePort();
        var start = StartServer(port);
        var client = NewClient();

        var connect = await client.ConnectAsync("127.0.0.1", port, _dispatcher);

        // Phase one succeeds without pairing: the client knows which machine answered, and its
        // certificate fingerprint, before any secret is sent.
        Assert.True(connect.NeedsCode, connect.Message);
        Assert.Equal(Environment.MachineName, connect.ServerName);
        Assert.False(string.IsNullOrWhiteSpace(connect.Fingerprint));
        Assert.False(connect.FingerprintChanged);

        var snapshot = WaitForFrame<HostSnapshotMsg>(client);

        var paired = await client.SubmitCodeAsync(start.Code!);

        Assert.True(paired.Success, paired.Message);
        Assert.Equal(RemoteClientState.Connected, client.State);

        var host = await snapshot;
        Assert.Equal(Environment.MachineName, host.HostName);

        client.Disconnect();
    }

    [Fact]
    public async Task PairingAcceptsTheCodeAsTheUserWouldTypeIt()
    {
        var port = FreePort();
        var start = StartServer(port);
        var client = NewClient();

        await client.ConnectAsync("127.0.0.1", port, _dispatcher);

        // Formatted with the separator and lower-cased — exactly what gets typed after reading it
        // off the other screen.
        var typed = PairingCode.Format(start.Code!).ToLowerInvariant();
        var paired = await client.SubmitCodeAsync(typed);

        Assert.True(paired.Success, paired.Message);
        client.Disconnect();
    }

    // ---------------------------------------------------------------- rejection paths

    [Fact]
    public async Task WrongCodeIsRejectedAndTheConnectionIsClosed()
    {
        var port = FreePort();
        var start = StartServer(port);
        var client = NewClient();

        await client.ConnectAsync("127.0.0.1", port, _dispatcher);

        var wrong = start.Code! == "AAAAAAAA" ? "BBBBBBBB" : "AAAAAAAA";
        var paired = await client.SubmitCodeAsync(wrong);

        Assert.False(paired.Success);
        Assert.Equal(DisconnectReason.AuthFailed, paired.Reason);
        Assert.Equal(RemoteClientState.Disconnected, client.State);

        // Rejecting must not leave the server thinking someone is attached, or the next genuine
        // attempt would be refused as "already in use".
        Assert.False(_server.HasClient);
    }

    [Fact]
    public async Task RepeatedWrongCodesLockOutFurtherAttempts()
    {
        var port = FreePort();
        StartServer(port);

        // Five failures is the threshold; the sixth attempt should be refused before the
        // challenge, which is what makes an 8-character code sufficient.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var client = NewClient();
            await client.ConnectAsync("127.0.0.1", port, _dispatcher);
            await client.SubmitCodeAsync("ZZZZZZZZ");
            client.Disconnect();
        }

        var locked = NewClient();
        var outcome = await locked.ConnectAsync("127.0.0.1", port, _dispatcher);

        // The lockout is reported during the connect phase, before a code is even asked for.
        Assert.False(outcome.NeedsCode);
        Assert.Contains("Too many failed attempts", outcome.Message);
    }

    [Fact]
    public async Task SecondClientIsRefusedWhileOneIsConnected()
    {
        var port = FreePort();
        var start = StartServer(port);

        var first = NewClient();
        await first.ConnectAsync("127.0.0.1", port, _dispatcher);
        var paired = await first.SubmitCodeAsync(start.Code!);
        Assert.True(paired.Success, paired.Message);

        var second = NewClient(Path.Combine(_settingsDir, "second"));
        var outcome = await second.ConnectAsync("127.0.0.1", port, _dispatcher);

        // The refusal arrives after the hello exchange, so it is reported as a connect failure
        // rather than a pairing failure — and it names the incumbent.
        Assert.False(outcome.Success);

        first.Disconnect();
    }

    // ---------------------------------------------------------------- teardown paths

    [Fact]
    public async Task StoppingTheServerTellsTheClientWhy()
    {
        var port = FreePort();
        var start = StartServer(port);
        var client = NewClient();

        await client.ConnectAsync("127.0.0.1", port, _dispatcher);
        await client.SubmitCodeAsync(start.Code!);

        var disconnected = new TaskCompletionSource<DisconnectReason>();
        client.Disconnected += (reason, _) => disconnected.TrySetResult(reason);

        _server.Stop();

        var reason = await WithTimeout(disconnected.Task);

        // "Server stopped" is a different message to the user than "connection lost", and the
        // client must not try to reconnect to a server that deliberately stopped.
        Assert.Equal(DisconnectReason.ServerStopped, reason);
        Assert.False(_server.IsRunning);
    }

    [Fact]
    public async Task KickingTheClientReportsItAsTakingBackControl()
    {
        var port = FreePort();
        var start = StartServer(port);
        var client = NewClient();

        await client.ConnectAsync("127.0.0.1", port, _dispatcher);
        await client.SubmitCodeAsync(start.Code!);

        var disconnected = new TaskCompletionSource<DisconnectReason>();
        client.Disconnected += (reason, _) => disconnected.TrySetResult(reason);

        _server.KickClient();

        Assert.Equal(DisconnectReason.Kicked, await WithTimeout(disconnected.Task));

        // Kicking keeps the server listening, unlike stopping it.
        Assert.True(_server.IsRunning);
    }

    [Fact]
    public async Task RegeneratingTheCodeDisconnectsTheClientAndInvalidatesTheOldCode()
    {
        var port = FreePort();
        var start = StartServer(port);
        var client = NewClient();

        await client.ConnectAsync("127.0.0.1", port, _dispatcher);
        await client.SubmitCodeAsync(start.Code!);

        var disconnected = new TaskCompletionSource<DisconnectReason>();
        client.Disconnected += (reason, _) => disconnected.TrySetResult(reason);

        var newCode = _server.RegenerateCode();

        Assert.Equal(DisconnectReason.CodeRegenerated, await WithTimeout(disconnected.Task));
        Assert.NotEqual(start.Code, newCode);

        // The old code must be dead, or regenerating would be cosmetic.
        var retry = NewClient();
        await retry.ConnectAsync("127.0.0.1", port, _dispatcher);
        var withOldCode = await retry.SubmitCodeAsync(start.Code!);

        Assert.False(withOldCode.Success);
    }

    [Fact]
    public async Task ConnectingToNothingFailsWithoutHanging()
    {
        var client = NewClient();

        // A port nothing is listening on. The connect must time out and report, not block the UI.
        var outcome = await client.ConnectAsync("127.0.0.1", FreePort(), _dispatcher);

        Assert.False(outcome.Success);
        Assert.False(outcome.NeedsCode);
        Assert.Equal(DisconnectReason.ConnectionLost, outcome.Reason);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<T> WaitForFrame<T>(RemoteClientService client) where T : RemoteFrame
    {
        var tcs = new TaskCompletionSource<T>();
        client.FrameReceived += frame =>
        {
            if (frame is T typed) tcs.TrySetResult(typed);
        };
        return WithTimeout(tcs.Task);
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Timeout));
        if (completed != task) throw new TimeoutException("Timed out waiting for the remote peer.");
        return await task;
    }

    // ---------------------------------------------------------------- test doubles

    private sealed class StubProjects : IRemoteServerProjects
    {
        public event Action? ActiveProjectsChanged;

        // No publishers: the panels are WPF controls, and the handshake is what these tests are
        // about. Replication itself is covered by the protocol round-trip tests.
        public IReadOnlyList<RemoteProjectPublisher> CreatePublishers() => [];

        public void SetAgentInputLocked(bool locked) => LastLockState = locked;

        public bool? LastLockState { get; private set; }

        public void RaiseChanged() => ActiveProjectsChanged?.Invoke();
    }

    private sealed class FakeSettings(string dir) : IAppSettingsStore
    {
        public string SettingsDir { get; } = dir;
        public string SettingsFile => Path.Combine(SettingsDir, "settings.json");

        private readonly Dictionary<string, string> _values = [];

        public string GetString(string key, string defaultValue = "")
            => _values.TryGetValue(key, out var value) ? value : defaultValue;

        public void SetString(string key, string value) => _values[key] = value;
        public List<string> GetStringList(string key) => [];
        public void SetStringList(string key, List<string> values) { }
    }

    private sealed class NullLog : ILogService
    {
        public string? LogFilePath => null;
        public void Init(string? logsFolder = null, string? sessionContext = null) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception ex) { }
    }
}
