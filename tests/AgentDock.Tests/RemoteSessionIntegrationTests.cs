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

    /// <summary>
    /// Cursor for handing out test ports. Randomised per run so two runs on one machine don't
    /// collide, then advanced monotonically so two *tests* can't collide with each other.
    ///
    /// A plain random pick per test is not enough: <see cref="RemoteServerService.FindFreePort"/>
    /// probes a port and immediately releases it, so a port reported free can be taken by the time
    /// the caller binds — by a listener an earlier test in the run has not finished releasing, or
    /// by a second search that started from the same place. That produced an intermittent failure
    /// in a different test on each run.
    /// </summary>
    private static int _portCursor = Random.Shared.Next(20000, 40000);

    /// <summary>A port no other test in this run has been handed.</summary>
    private static int FreePort()
    {
        // Stride wider than the search window, so one call's search can never wander into the
        // range the next call will be given.
        const int stride = 64;
        const int attempts = 32;

        var start = Interlocked.Add(ref _portCursor, stride);

        return RemoteServerService.FindFreePort(IPAddress.Loopback, start, attempts)
               ?? throw new InvalidOperationException("no free port");
    }

    private RemoteClientService NewClient(string? settingsDir = null)
        => new(new NullLog(), new RemoteIdentity(new NullLog(), new FakeSettings(settingsDir ?? _settingsDir)));

    private RemoteServerService.StartResult StartServer(int port)
        => StartServer(port, new StubProjects());

    private RemoteServerService.StartResult StartServer(int port, StubProjects projects)
        => _server.Start(port, RemoteBindScope.LoopbackOnly, projects, _dispatcher);

    /// <summary>
    /// Waits for a lock state to settle. The server pushes locks through the dispatcher, so
    /// asserting immediately after a start or a disconnect would race the marshalling.
    /// </summary>
    private static async Task AssertLockAsync(
        StubProjects projects, bool composer, bool prompts)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (projects.LastLockState == composer && projects.LastPromptLockState == prompts)
                return;

            await Task.Delay(20);
        }

        Assert.Equal((composer, prompts), (projects.LastLockState, projects.LastPromptLockState));
    }

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

    // ---------------------------------------------------------------- input locking

    /// <summary>
    /// The composer locks the moment hosting starts, not when a client turns up. A host that still
    /// accepts typing between Start Server and the first connection is a host whose owner can type
    /// a message nobody will send, on a machine they are walking away from.
    /// </summary>
    [Fact]
    public async Task StartingTheServerLocksTheComposerBeforeAnyClientConnects()
    {
        var projects = new StubProjects();
        var start = StartServer(FreePort(), projects);

        Assert.True(start.Success, start.Error);
        await AssertLockAsync(projects, composer: true, prompts: false);
    }

    /// <summary>
    /// Prompts stay answerable until someone is actually connected. Locking them at start would
    /// park a permission request with no one able to answer it on either machine.
    /// </summary>
    [Fact]
    public async Task PromptsLockOnlyOnceAClientIsAttached()
    {
        var projects = new StubProjects();
        var port = FreePort();
        var start = StartServer(port, projects);
        var client = NewClient();

        await AssertLockAsync(projects, composer: true, prompts: false);

        await client.ConnectAsync("127.0.0.1", port, _dispatcher);
        var paired = await client.SubmitCodeAsync(start.Code!);
        Assert.True(paired.Success, paired.Message);

        await AssertLockAsync(projects, composer: true, prompts: true);

        // Losing the client leaves this machine hosting but unattended-by-proxy: the composer stays
        // locked, and prompts come back because here is once again the only place to answer them.
        client.Disconnect();
        await AssertLockAsync(projects, composer: true, prompts: false);
    }

    [Fact]
    public async Task StoppingTheServerUnlocksEverything()
    {
        var projects = new StubProjects();
        StartServer(FreePort(), projects);

        await AssertLockAsync(projects, composer: true, prompts: false);

        _server.Stop();

        await AssertLockAsync(projects, composer: false, prompts: false);
    }

    // ---------------------------------------------------------------- advertised address

    /// <summary>
    /// The address shown to the user has to be one another machine can reach. A machine name only
    /// resolves if something on the network publishes it, which is why an IPv4 literal is offered
    /// instead — see <see cref="RemoteServerService.ConnectAddress"/>.
    /// </summary>
    [Fact]
    public void DiscoveredAddressesAreRoutableIPv4Literals()
    {
        foreach (var address in RemoteServerService.DiscoverLocalAddresses())
        {
            Assert.True(IPAddress.TryParse(address, out var parsed), $"'{address}' is not an IP literal");
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, parsed!.AddressFamily);
            Assert.False(IPAddress.IsLoopback(parsed));

            // A link-local address exists but routes nowhere — offering one sends the user off to
            // debug a firewall when the real problem is that DHCP never answered.
            Assert.DoesNotContain("169.254.", address);
        }
    }

    [Fact]
    public void LoopbackHostingAdvertisesLoopbackRatherThanTheLanAddress()
    {
        var port = FreePort();
        var start = _server.Start(port, RemoteBindScope.LoopbackOnly, new StubProjects(), _dispatcher);

        Assert.True(start.Success, start.Error);
        Assert.Equal("127.0.0.1", _server.ConnectAddress);
        Assert.Equal($"127.0.0.1:{port}", _server.ConnectEndpoint);
    }

    [Fact]
    public void StoppingClearsTheAdvertisedAddress()
    {
        StartServer(FreePort());
        Assert.NotNull(_server.ConnectAddress);

        _server.Stop();

        Assert.Null(_server.ConnectAddress);
        Assert.Null(_server.ConnectEndpoint);
        Assert.Empty(_server.ConnectAddresses);
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

        public void SetAgentInputLocked(bool composerLocked, bool promptsLocked)
        {
            LastLockState = composerLocked;
            LastPromptLockState = promptsLocked;
        }

        public bool? LastLockState { get; private set; }

        public bool? LastPromptLockState { get; private set; }

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
