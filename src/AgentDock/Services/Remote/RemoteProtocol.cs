namespace AgentDock.Services.Remote;

/// <summary>
/// Wire protocol constants. <see cref="Version"/> is checked before anything else in the
/// handshake and a mismatch is refused outright — no compatibility shims. A 0.11 server will
/// be talked to by 0.12 clients, and guessing is worse than a clear error.
/// </summary>
public static class RemoteProtocol
{
    /// <summary>Bumped on any breaking change to frame shapes or handshake order.</summary>
    public const int Version = 1;

    /// <summary>Magic bytes so a wrong-service connection fails fast and legibly.</summary>
    public static readonly byte[] Magic = "AGDK"u8.ToArray();

    public const int DefaultPort = 7420;

    /// <summary>Largest single frame we will read. Guards against a hostile length prefix.</summary>
    public const int MaxFrameBytes = 32 * 1024 * 1024;

    /// <summary>Largest file body served to a client in one response.</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>Largest image attachment accepted from a client.</summary>
    public const int MaxAttachmentBytes = 16 * 1024 * 1024;

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// No traffic for this long means the peer is gone. Deliberately longer than three
    /// heartbeat intervals so a stalled UI thread or a GC pause is not mistaken for a drop.
    /// </summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(35);

    /// <summary>Window over which text-append ops are coalesced before going on the wire.</summary>
    public static readonly TimeSpan DeltaCoalesceWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>How many deltas per project the server retains for gap-filling a reconnect.</summary>
    public const int ReplayBufferSize = 512;
}

/// <summary>Why a connection ended. Five different messages to a user, one enum on the wire.</summary>
public enum DisconnectReason
{
    Unknown = 0,
    ClientRequested,
    ServerStopped,
    Kicked,
    CodeRegenerated,
    ProtocolMismatch,
    AuthFailed,
    AlreadyInUse,
    ConnectionLost,
    ServerError,
}

public static class DisconnectReasonText
{
    public static string Describe(DisconnectReason reason) => reason switch
    {
        DisconnectReason.ClientRequested => "Disconnected.",
        DisconnectReason.ServerStopped => "The server stopped hosting.",
        DisconnectReason.Kicked => "The server took back control.",
        DisconnectReason.CodeRegenerated => "The server regenerated its pairing code.",
        DisconnectReason.ProtocolMismatch => "Version mismatch — update both machines to the same Agent Dock version.",
        DisconnectReason.AuthFailed => "The pairing code was rejected.",
        DisconnectReason.AlreadyInUse => "Another client is already connected to that server.",
        DisconnectReason.ConnectionLost => "Connection lost.",
        DisconnectReason.ServerError => "The server reported an error.",
        _ => "Disconnected.",
    };
}
