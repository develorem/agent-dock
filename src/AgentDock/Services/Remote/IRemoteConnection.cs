namespace AgentDock.Services.Remote;

/// <summary>
/// One duplex, authenticated frame channel to a remote client — the shape
/// <see cref="RemoteServerService"/> needs regardless of what carries the bytes underneath.
///
/// <see cref="FramedConnection"/> (native TCP+TLS) and <see cref="WebSocketConnection"/> (browser)
/// both implement this directly rather than through an adapter: every member here already exists
/// on both with a matching signature, so there is nothing an adapter would add except an extra
/// object to keep in sync.
/// </summary>
public interface IRemoteConnection
{
    /// <summary>Raised for every frame after the handshake, excluding ping/pong and bye, which are
    /// handled internally by the implementation.</summary>
    event Action<RemoteFrame>? FrameReceived;

    /// <summary>Raised exactly once, whatever the cause.</summary>
    event Action<DisconnectReason, string?>? Closed;

    /// <summary>A short label for logging — which peer, over which transport.</summary>
    string Label { get; }

    /// <summary>Starts the read/write/heartbeat loops. Call once, after the handshake.</summary>
    void Start();

    /// <summary>Queues a frame. Returns false once the connection is closing.</summary>
    bool Send(RemoteFrame frame);

    /// <summary>Queues a frame, waiting if the outbound buffer is full (slow-client backpressure).</summary>
    ValueTask<bool> SendAsync(RemoteFrame frame);

    /// <summary>Sends one frame synchronously, bypassing the queue. Handshake only.</summary>
    Task SendDirectAsync(RemoteFrame frame, CancellationToken ct);

    /// <summary>Reads one frame synchronously. Handshake only.</summary>
    Task<RemoteFrame?> ReceiveDirectAsync(CancellationToken ct);

    /// <summary>Tears the connection down and raises <see cref="Closed"/>.</summary>
    void Close(DisconnectReason reason, string? message = null, bool sendBye = true);
}
