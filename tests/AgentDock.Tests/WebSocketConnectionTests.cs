using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentDock.Services.Abstractions;
using AgentDock.Services.Remote;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Tests <see cref="WebSocketConnection"/> in isolation, over a real loopback
/// <see cref="HttpListener"/> + <see cref="ClientWebSocket"/> — no <see cref="RemoteServerService"/>
/// involved, since this class's only job is to speak the same <see cref="RemoteFrame"/> vocabulary
/// and heartbeat timing as <see cref="FramedConnection"/>, just over a WebSocket instead of a raw
/// TLS stream.
/// </summary>
public class WebSocketConnectionTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private HttpListener? _listener;
    private int _port;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        return Task.CompletedTask;
    }

    private static int FreePort() =>
        RemoteServerService.FindFreePort(IPAddress.Loopback, Random.Shared.Next(20000, 40000), 200)
        ?? throw new InvalidOperationException("No free port found for test.");

    /// <summary>Starts an HttpListener and hands back the server-side WebSocket once a client
    /// connects, alongside the connected ClientWebSocket for the test to drive as the peer.</summary>
    private async Task<(WebSocketConnection Server, ClientWebSocket Client)> ConnectPairAsync(
        TimeSpan? heartbeatInterval = null, TimeSpan? heartbeatTimeout = null)
    {
        _port = FreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        var acceptTask = _listener.GetContextAsync();

        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/"), CancellationToken.None)
            .ConfigureAwait(false);

        var context = await acceptTask.ConfigureAwait(false);
        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);

        var server = new WebSocketConnection(
            wsContext.WebSocket, new NullLog(), "test:server", heartbeatInterval, heartbeatTimeout);
        server.Start();

        return (server, client);
    }

    private static async Task SendRawAsync(ClientWebSocket client, RemoteFrame frame)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, FramedConnection.JsonOptions);
        await client.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<RemoteFrame> ReceiveRawAsync(ClientWebSocket client)
    {
        var buffer = new byte[16 * 1024];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        return JsonSerializer.Deserialize<RemoteFrame>(json, FramedConnection.JsonOptions)!;
    }

    [Fact]
    public async Task RoundTrips_ServerToClient()
    {
        var (server, client) = await ConnectPairAsync();

        server.Send(new AuthChallengeMsg("test-nonce-value"));

        var received = await WithTimeout(ReceiveRawAsync(client));
        var challenge = Assert.IsType<AuthChallengeMsg>(received);
        Assert.Equal("test-nonce-value", challenge.Nonce);

        server.Close(DisconnectReason.ClientRequested, sendBye: false);
        client.Dispose();
    }

    [Fact]
    public async Task RoundTrips_ClientToServer()
    {
        var (server, client) = await ConnectPairAsync();

        RemoteFrame? received = null;
        var tcs = new TaskCompletionSource<RemoteFrame>();
        server.FrameReceived += f => tcs.TrySetResult(f);

        await SendRawAsync(client, new DenyPermissionCmd("proj-1", "req-1", "no thanks"));

        received = await WithTimeout(tcs.Task);
        var deny = Assert.IsType<DenyPermissionCmd>(received);
        Assert.Equal("proj-1", deny.ProjectId);
        Assert.Equal("no thanks", deny.Reason);

        server.Close(DisconnectReason.ClientRequested, sendBye: false);
        client.Dispose();
    }

    [Fact]
    public async Task PingFrame_IsAnsweredWithPong_AndNotForwarded()
    {
        var (server, client) = await ConnectPairAsync();

        var forwarded = false;
        server.FrameReceived += _ => forwarded = true;

        await SendRawAsync(client, new PingFrame(12345));

        var reply = await WithTimeout(ReceiveRawAsync(client));
        var pong = Assert.IsType<PongFrame>(reply);
        Assert.Equal(12345, pong.TicksUtc);
        Assert.False(forwarded);

        server.Close(DisconnectReason.ClientRequested, sendBye: false);
        client.Dispose();
    }

    [Fact]
    public async Task HeartbeatTimeout_ClosesConnection_WhenPeerGoesSilent()
    {
        // Short, test-only timings - the real 10s/35s would make this test impractical to run.
        var (server, client) = await ConnectPairAsync(
            heartbeatInterval: TimeSpan.FromMilliseconds(50),
            heartbeatTimeout: TimeSpan.FromMilliseconds(200));

        var closedTcs = new TaskCompletionSource<DisconnectReason>();
        server.Closed += (reason, _) => closedTcs.TrySetResult(reason);

        // Deliberately send nothing back - simulates a tombstoned phone that never closes
        // cleanly and never answers a ping.
        var reason = await WithTimeout(closedTcs.Task);
        Assert.Equal(DisconnectReason.ConnectionLost, reason);

        client.Dispose();
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(completed == task, "Timed out waiting for the expected frame/event.");
        return await task;
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
