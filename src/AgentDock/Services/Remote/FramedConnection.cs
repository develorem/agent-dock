using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services.Remote;

/// <summary>
/// One duplex frame channel over a stream (in practice an <c>SslStream</c>).
///
/// Framing is a 4-byte big-endian length followed by UTF-8 JSON. Both ends are Agent Dock, so
/// WebSocket framing would buy nothing but a dependency; this keeps binary payloads (file
/// bodies, image attachments, icon bytes) cheap and the reader trivially bounded.
///
/// Responsibilities kept here on purpose:
/// <list type="bullet">
/// <item>a single writer task, so frames never interleave on the wire</item>
/// <item>heartbeats in both directions, because a dropped VPN or a sleeping laptop otherwise
/// leaves both ends believing they are still connected (half-open TCP)</item>
/// <item>exactly one terminal <see cref="Closed"/> notification carrying a reason</item>
/// </list>
/// Coalescing of chat text deltas is deliberately *not* here — it belongs to the producer that
/// knows which ops may be merged and which must never be.
/// </summary>
public sealed class FramedConnection : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Frames are machine-to-machine; indentation would just cost bandwidth.
        WriteIndented = false,
    };

    private readonly Stream _stream;
    private readonly ILogService _log;
    private readonly string _label;
    private readonly Channel<RemoteFrame> _outbound =
        Channel.CreateBounded<RemoteFrame>(new BoundedChannelOptions(4096)
        {
            // Never drop: a lost structural op (tool call, turn complete, permission request)
            // corrupts the client's view. A slow link slows the producer instead.
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

    public FramedConnection(Stream stream, ILogService log, string label)
    {
        _stream = stream;
        _log = log;
        _label = label;
    }

    public void Start()
    {
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        _writeTask = Task.Run(() => WriteLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Queues a frame. Returns false once the connection is closing, so callers can stop
    /// producing rather than pile work into a dead channel.
    /// </summary>
    public bool Send(RemoteFrame frame)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        return _outbound.Writer.TryWrite(frame);
    }

    /// <summary>Queues a frame, waiting if the outbound buffer is full (slow client backpressure).</summary>
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
    /// Sends one frame synchronously on the calling task, bypassing the queue. Used only for
    /// the handshake, which must complete in strict order before the loops start.
    /// </summary>
    public async Task SendDirectAsync(RemoteFrame frame, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await _stream.WriteAsync(header, ct).ConfigureAwait(false);
        await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame synchronously on the calling task. Handshake only.</summary>
    public async Task<RemoteFrame?> ReceiveDirectAsync(CancellationToken ct)
    {
        var payload = await ReadFramePayloadAsync(ct).ConfigureAwait(false);
        if (payload == null) return null;
        return Deserialize(payload);
    }

    public void Close(DisconnectReason reason, string? message = null, bool sendBye = true)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        if (sendBye)
        {
            // Best-effort courtesy frame so the peer can distinguish a deliberate stop from a
            // dropped link. Written directly because the writer loop is about to be cancelled.
            try
            {
                var bye = JsonSerializer.SerializeToUtf8Bytes<RemoteFrame>(
                    new DisconnectMsg(reason, message), JsonOptions);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, bye.Length);
                _stream.Write(header);
                _stream.Write(bye);
                _stream.Flush();
            }
            catch
            {
                // The peer may already be gone — nothing useful to do.
            }
        }

        _outbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { _stream.Dispose(); } catch { }

        _log.Info($"Remote[{_label}]: closed — {reason} {message}");
        Closed?.Invoke(reason, message);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var payload = await ReadFramePayloadAsync(ct).ConfigureAwait(false);
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
                    // A frame we cannot parse means the peer is speaking something else, or a
                    // version slipped through the handshake. Either way, do not guess.
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
                var payload = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
                await _stream.WriteAsync(header, ct).ConfigureAwait(false);
                await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
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
    /// Sends a ping every interval and declares the peer gone after
    /// <see cref="RemoteProtocol.HeartbeatTimeout"/> of silence. This is what makes half-open
    /// TCP detectable; without it a sleeping peer looks connected indefinitely.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(RemoteProtocol.HeartbeatInterval, ct).ConfigureAwait(false);

                var silence = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastReceivedTicks), DateTimeKind.Utc);
                if (silence > RemoteProtocol.HeartbeatTimeout)
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

    private async Task<byte[]?> ReadFramePayloadAsync(CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(header, ct).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > RemoteProtocol.MaxFrameBytes)
            throw new InvalidDataException($"Frame length {length} out of range");

        var payload = new byte[length];
        if (!await ReadExactAsync(payload, ct).ConfigureAwait(false)) return null;
        return payload;
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                .ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static RemoteFrame? Deserialize(byte[] payload)
        => JsonSerializer.Deserialize<RemoteFrame>(payload, JsonOptions);

    /// <summary>Writes the handshake preamble so a wrong-service connection fails immediately.</summary>
    public static async Task WriteMagicAsync(Stream stream, CancellationToken ct)
    {
        await stream.WriteAsync(RemoteProtocol.Magic, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads and validates the handshake preamble.</summary>
    public static async Task<bool> ReadMagicAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[RemoteProtocol.Magic.Length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                .ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return buffer.AsSpan().SequenceEqual(RemoteProtocol.Magic);
    }

    public void Dispose() => Close(DisconnectReason.ClientRequested, null, sendBye: false);

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
