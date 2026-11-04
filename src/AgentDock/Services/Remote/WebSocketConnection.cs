using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services.Remote;

/// <summary>
/// One duplex frame channel over a browser <see cref="WebSocket"/> — the web-client counterpart
/// to <see cref="FramedConnection"/>.
///
/// WebSocket already frames messages, so there is no 4-byte length prefix here: one
/// <see cref="RemoteFrame"/> in, one complete text message out, and vice versa on read. Liveness
/// reuses the exact same app-level <see cref="PingFrame"/>/<see cref="PongFrame"/> records and
/// <see cref="RemoteProtocol.HeartbeatInterval"/>/<see cref="RemoteProtocol.HeartbeatTimeout"/>
/// constants <see cref="FramedConnection"/> uses — a WebSocket's own ping/pong control frames
/// are not exposed to application code by <see cref="System.Net.HttpListener"/>'s WebSocket, so
/// matching the app-level scheme keeps both transports detecting a dead peer the same way and in
/// the same time.
/// </summary>
public sealed class WebSocketConnection : IRemoteConnection
{
    private readonly WebSocket _socket;
    private readonly ILogService _log;
    private readonly string _label;

    private readonly Channel<RemoteFrame> _outbound =
        Channel.CreateBounded<RemoteFrame>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _readTask;
    private Task? _writeTask;
    private Task? _heartbeatTask;

    private long _lastReceivedTicks = DateTime.UtcNow.Ticks;
    private int _closed;

    public event Action<RemoteFrame>? FrameReceived;

    /// <summary>Raised exactly once, whatever the cause.</summary>
    public event Action<DisconnectReason, string?>? Closed;

    public string Label => _label;

    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _heartbeatTimeout;

    /// <param name="heartbeatInterval">Defaults to <see cref="RemoteProtocol.HeartbeatInterval"/>.
    /// Overridable so tests don't have to wait out the real ~35s timeout.</param>
    /// <param name="heartbeatTimeout">Defaults to <see cref="RemoteProtocol.HeartbeatTimeout"/>.</param>
    public WebSocketConnection(
        WebSocket socket,
        ILogService log,
        string label,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? heartbeatTimeout = null)
    {
        _socket = socket;
        _log = log;
        _label = label;
        _heartbeatInterval = heartbeatInterval ?? RemoteProtocol.HeartbeatInterval;
        _heartbeatTimeout = heartbeatTimeout ?? RemoteProtocol.HeartbeatTimeout;
    }

    public void Start()
    {
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        _writeTask = Task.Run(() => WriteLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    public bool Send(RemoteFrame frame)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        return _outbound.Writer.TryWrite(frame);
    }

    public async ValueTask<bool> SendAsync(RemoteFrame frame)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        try
        {
            await _outbound.Writer.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (ChannelClosedException) { return false; }
    }

    /// <summary>
    /// Sends one frame as a complete WebSocket text message, bypassing the queue. Used only for
    /// the rare case a caller needs handshake-style strict ordering before <see cref="Start"/>.
    /// </summary>
    public async Task SendDirectAsync(RemoteFrame frame, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, FramedConnection.JsonOptions);
        await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Reads one complete message synchronously. Same handshake-only caveat as above.</summary>
    public async Task<RemoteFrame?> ReceiveDirectAsync(CancellationToken ct)
    {
        var payload = await ReadMessagePayloadAsync(ct).ConfigureAwait(false);
        return payload == null ? null : Deserialize(payload);
    }

    public void Close(DisconnectReason reason, string? message = null, bool sendBye = true)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        if (sendBye)
        {
            try
            {
                var bye = JsonSerializer.SerializeToUtf8Bytes<RemoteFrame>(
                    new DisconnectMsg(reason, message), FramedConnection.JsonOptions);
                _socket.SendAsync(bye, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // The peer may already be gone — nothing useful to do.
            }
        }

        _outbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try
        {
            // CloseOutputAsync, not CloseAsync: it sends the close frame and returns without
            // waiting for the peer's handshake reply. A full CloseAsync would block here
            // indefinitely for exactly the peer we are most likely closing on — one that has
            // gone dark (heartbeat timeout, tombstoned phone) and will never reply at all.
            _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, message, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch { }
        finally { _socket.Dispose(); }

        _log.Info($"Remote[{_label}]: closed — {reason} {message}");
        Closed?.Invoke(reason, message);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var payload = await ReadMessagePayloadAsync(ct).ConfigureAwait(false);
                if (payload == null)
                {
                    Close(DisconnectReason.ConnectionLost, "Peer closed the connection", sendBye: false);
                    return;
                }

                Volatile.Write(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);

                RemoteFrame? frame;
                try
                {
                    frame = Deserialize(payload);
                }
                catch (Exception ex)
                {
                    _log.Error($"Remote[{_label}]: undecodable frame", ex);
                    Close(DisconnectReason.ProtocolMismatch, "Received an undecodable frame");
                    return;
                }

                if (frame is PingFrame ping)
                {
                    Send(new PongFrame(ping.TicksUtc));
                    continue;
                }

                if (frame is PongFrame) continue;

                if (frame is DisconnectMsg bye)
                {
                    Close(bye.Reason, bye.Message, sendBye: false);
                    return;
                }

                if (frame != null)
                    FrameReceived?.Invoke(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Close(DisconnectReason.ConnectionLost, ex.Message, sendBye: false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Remote[{_label}]: read loop ended — {ex.Message}");
            Close(DisconnectReason.ConnectionLost, ex.Message, sendBye: false);
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(frame, FramedConnection.JsonOptions);
                await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warn($"Remote[{_label}]: write loop ended — {ex.Message}");
            Close(DisconnectReason.ConnectionLost, ex.Message, sendBye: false);
        }
    }

    /// <summary>
    /// Same algorithm as <see cref="FramedConnection"/>'s heartbeat, against the same constants,
    /// so a tombstoned phone's slot frees itself on the same ~35s timeline a dropped native
    /// client would.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, ct).ConfigureAwait(false);

                var silence = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastReceivedTicks), DateTimeKind.Utc);
                if (silence > _heartbeatTimeout)
                {
                    _log.Warn($"Remote[{_label}]: no traffic for {silence.TotalSeconds:F0}s — treating peer as gone");
                    Close(DisconnectReason.ConnectionLost, "Heartbeat timed out", sendBye: false);
                    return;
                }

                Send(new PingFrame(DateTime.UtcNow.Ticks));
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Reads one WebSocket message in full, looping until <c>EndOfMessage</c> — a message can
    /// arrive across several frames even though the application sees it as one JSON payload.
    /// Returns null when the peer closed the socket.
    /// </summary>
    private async Task<byte[]?> ReadMessagePayloadAsync(CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (stream.Length + result.Count > RemoteProtocol.MaxFrameBytes)
                throw new InvalidDataException($"Message exceeded {RemoteProtocol.MaxFrameBytes} bytes");

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return stream.ToArray();
    }

    private static RemoteFrame? Deserialize(byte[] payload)
        => JsonSerializer.Deserialize<RemoteFrame>(payload, FramedConnection.JsonOptions);
}
