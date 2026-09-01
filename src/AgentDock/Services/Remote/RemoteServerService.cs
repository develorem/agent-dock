using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Threading;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services.Remote;

/// <summary>Supplies the set of project tabs worth replicating. Implemented by the main window.</summary>
public interface IRemoteServerProjects
{
    /// <summary>
    /// One publisher per project with a live agent session — the same definition of "active" the
    /// dynamic Active Projects group already uses, rather than a second one.
    /// </summary>
    IReadOnlyList<RemoteProjectPublisher> CreatePublishers();

    /// <summary>
    /// Locks or unlocks agent input across every project tab.
    ///
    /// <paramref name="composerLocked"/> covers everything that starts work — the composer, send,
    /// the queue and the schedule affordances — and is set for as long as this machine is hosting,
    /// whether or not a client has attached yet: a host that still accepts typing is a host two
    /// people can drive.
    ///
    /// <paramref name="promptsLocked"/> covers the inline permission and question panels, and is
    /// set only once a client is actually attached. Locking those while nobody is connected would
    /// park a blocked turn with no one able to answer it, on either machine.
    /// </summary>
    void SetAgentInputLocked(bool composerLocked, bool promptsLocked);

    /// <summary>Raised when a session starts or ends, so the client's tab set can follow.</summary>
    event Action? ActiveProjectsChanged;
}

public enum RemoteServerState { Stopped, Starting, Listening, ClientConnected }

/// <summary>
/// Which interfaces the listener binds.
///
/// There is deliberately no "LAN only" option. Reaching the server from another machine
/// requires binding <see cref="System.Net.IPAddress.Any"/>; binding one adapter address
/// instead is fragile the moment a VPN attaches or DHCP moves the address, and it would not
/// actually restrict anything a firewall rule does not already restrict better. Pretending
/// otherwise would be security theatre — what limits reachability beyond the local network
/// is the Windows firewall and the router, not this setting.
/// </summary>
public enum RemoteBindScope
{
    /// <summary>Only this machine. Useful for testing two instances on one box.</summary>
    LoopbackOnly,

    /// <summary>All interfaces — required for another machine to connect.</summary>
    AllInterfaces,
}

/// <summary>
/// Hosts this machine's sessions for a remote Agent Dock.
///
/// Security posture, stated plainly because the feature is exactly as powerful as it sounds: a
/// paired client can instruct an agent that reads, writes and executes on this machine. If a
/// session is running in dangerous mode, the client can do anything this account can do. That is
/// the feature working as intended, and it is why the pairing code is rate-limited, the
/// certificate is pinned, file requests are containment-checked, and a connected client is always
/// visible and one click from being removed.
/// </summary>
public sealed class RemoteServerService(
    ILogService log,
    RemoteIdentity identity) : IDisposable
{
    // Keeping the machine awake while hosting. An idle server laptop suspending mid-session is
    // the single most likely way this feature "randomly stops working"; the display is left free
    // to sleep, only the system is held.
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private const int MaxAuthFailures = 5;
    private static readonly TimeSpan AuthLockout = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private X509Certificate2? _certificate;
    private IRemoteServerProjects? _provider;
    private Dispatcher? _dispatcher;

    private FramedConnection? _connection;
    private readonly List<RemoteProjectPublisher> _publishers = [];
    private readonly ConcurrentDictionary<string, RemoteProjectPublisher> _byId = new();

    private string? _sessionToken;
    private int _authFailures;
    private DateTime _lockoutUntil = DateTime.MinValue;

    public RemoteServerState State { get; private set; } = RemoteServerState.Stopped;
    public int Port { get; private set; }
    public string? PairingCodeValue { get; private set; }
    public string HostName => Environment.MachineName;
    public string? ConnectedClientName { get; private set; }
    public string? CertificateFingerprint { get; private set; }
    public string? BoundAddressDescription { get; private set; }

    /// <summary>
    /// The address to type on the other machine, and the reason this is shown in preference to
    /// <see cref="HostName"/>: a Windows machine name only resolves if the client's network hands
    /// out matching NetBIOS/mDNS names, which plenty of home routers, guest VLANs and VPN links
    /// do not. An IPv4 literal always resolves. Null until the server starts.
    /// </summary>
    public string? ConnectAddress { get; private set; }

    /// <summary>Every address the server can be reached on, primary first. Empty when stopped.</summary>
    public IReadOnlyList<string> ConnectAddresses { get; private set; } = [];

    /// <summary>The full <c>address:port</c> pair, ready to paste into Connect to Server.</summary>
    public string? ConnectEndpoint => ConnectAddress == null ? null : $"{ConnectAddress}:{Port}";

    public bool IsRunning => State != RemoteServerState.Stopped;
    public bool HasClient => State == RemoteServerState.ClientConnected;

    /// <summary>Raised on the UI thread whenever anything the status strip shows changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when a pairing attempt fails, so the server's owner can see it happening.</summary>
    public event Action<string>? AuthFailureReported;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Checks a port is actually free by trying to bind it. Querying
    /// <c>IPGlobalProperties.GetActiveTcpListeners</c> instead would race with anything else
    /// starting up, and would report success for a port that is taken a millisecond later.
    /// </summary>
    public static bool IsPortAvailable(IPAddress address, int port)
    {
        try
        {
            var probe = new TcpListener(address, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Finds the next free port at or above <paramref name="startPort"/>.</summary>
    public static int? FindFreePort(IPAddress address, int startPort, int attempts = 50)
    {
        for (var port = startPort; port < startPort + attempts && port <= 65535; port++)
            if (IsPortAvailable(address, port))
                return port;

        return null;
    }

    /// <summary>
    /// The IPv4 addresses another machine can reach this one on, best candidate first.
    ///
    /// Filtered and ordered rather than just listed, because a developer machine typically has
    /// several: Hyper-V and WSL switches, Docker bridges, VPN adapters and disconnected Wi-Fi all
    /// contribute addresses, and offering the wrong one first is how "I typed what it told me and
    /// nothing happened" happens.
    ///
    /// Interfaces that are down, loopback, non-IPv4 or link-local are dropped outright. The rest
    /// are ranked, most significant first: having a **default gateway** (the adapter actually
    /// carrying traffic off this machine), being **real Ethernet or Wi-Fi**, and not being a
    /// **named virtual switch**.
    /// </summary>
    public static List<string> DiscoverLocalAddresses()
    {
        var candidates = new List<(string Address, int Rank)>();

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses
                    .Any(g => g.Address is { } a
                              && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                              && !a.Equals(IPAddress.Any));

                var isPhysical = nic.NetworkInterfaceType
                    is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                    or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet
                    or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211;

                // Virtual switches name themselves; the interface type does not distinguish them.
                var looksVirtual = nic.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                                   || nic.Description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
                                   || nic.Description.Contains("vethernet", StringComparison.OrdinalIgnoreCase)
                                   || nic.Name.Contains("wsl", StringComparison.OrdinalIgnoreCase)
                                   || nic.Description.Contains("docker", StringComparison.OrdinalIgnoreCase);

                var rank = (hasGateway ? 0 : 4) + (isPhysical ? 0 : 2) + (looksVirtual ? 1 : 0);

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;

                    var text = unicast.Address.ToString();

                    // 169.254.x.x means DHCP never answered — the address exists but routes nowhere.
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                    candidates.Add((text, rank));
                }
            }
        }
        catch
        {
            // Address discovery is a convenience on top of a server that is already listening on
            // every interface — a failure here must not stop hosting.
        }

        return candidates
            .OrderBy(c => c.Rank)
            .Select(c => c.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public sealed record StartResult(bool Success, string? Error, int Port, string? Code);

    /// <summary>
    /// Starts hosting. See <see cref="RemoteBindScope"/> for why there is no "LAN only" choice:
    /// binding every interface is what remote access requires, and restricting exposure beyond
    /// the local network is a firewall concern rather than a bind-address one.
    /// </summary>
    public StartResult Start(
        int port,
        RemoteBindScope scope,
        IRemoteServerProjects provider,
        Dispatcher dispatcher)
    {
        lock (_gate)
        {
            if (IsRunning)
                return new StartResult(false, "The server is already running.", Port, PairingCodeValue);

            var address = scope == RemoteBindScope.AllInterfaces ? IPAddress.Any : IPAddress.Loopback;
            if (!IsPortAvailable(address, port))
            {
                var suggestion = FindFreePort(address, port + 1);
                var message = suggestion.HasValue
                    ? $"Port {port} is already in use. Port {suggestion.Value} is free."
                    : $"Port {port} is already in use.";
                return new StartResult(false, message, port, null);
            }

            try
            {
                _certificate = identity.LoadOrCreateServerCertificate();
                CertificateFingerprint = RemoteIdentity.FingerprintOf(_certificate);

                _listener = new TcpListener(address, port);
                _listener.Start();

                _provider = provider;
                _dispatcher = dispatcher;
                Port = port;
                PairingCodeValue = PairingCode.Generate();
                _sessionToken = null;
                _authFailures = 0;
                _lockoutUntil = DateTime.MinValue;
                BoundAddressDescription = scope == RemoteBindScope.AllInterfaces
                    ? "all interfaces"
                    : "this machine only (loopback)";

                if (scope == RemoteBindScope.AllInterfaces)
                {
                    ConnectAddresses = DiscoverLocalAddresses();
                    // Falling back to the machine name is better than showing nothing, and is the
                    // only thing left to offer if every adapter was filtered out.
                    ConnectAddress = ConnectAddresses.FirstOrDefault() ?? HostName;
                }
                else
                {
                    ConnectAddresses = ["127.0.0.1"];
                    ConnectAddress = "127.0.0.1";
                }

                State = RemoteServerState.Listening;

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

                SetThreadExecutionState(EsContinuous | EsSystemRequired);

                provider.ActiveProjectsChanged += OnActiveProjectsChanged;

                // Hosting locks the composer immediately, not on the first client. The user starts
                // server mode because they are about to walk away from this machine; leaving the
                // input live until someone connects means the window they are still looking at
                // accepts typing it will then have to reconcile with a second driver.
                RunOnUi(() => provider.SetAgentInputLocked(true, promptsLocked: false));

                log.Info($"RemoteServer: listening on {address}:{port} ({BoundAddressDescription}), " +
                         $"code {PairingCode.Format(PairingCodeValue)}, fingerprint {CertificateFingerprint}");

                RaiseStateChanged();
                return new StartResult(true, null, port, PairingCodeValue);
            }
            catch (Exception ex)
            {
                log.Error("RemoteServer: failed to start", ex);
                StopInternal(DisconnectReason.ServerError, "Server failed to start");
                return new StartResult(false, ex.Message, port, null);
            }
        }
    }

    /// <summary>
    /// Stops hosting. Any connected client is told why, and the local agent input unlocks. The
    /// Claude sessions themselves keep running — stopping the server is not stopping the work.
    /// </summary>
    public void Stop() => StopInternal(DisconnectReason.ServerStopped, "The server stopped hosting.");

    /// <summary>Removes the connected client but keeps listening.</summary>
    public void KickClient()
    {
        FramedConnection? connection;
        lock (_gate) connection = _connection;

        connection?.Close(DisconnectReason.Kicked, "The server took back control.");
    }

    /// <summary>
    /// Issues a new pairing code. Any existing session token is invalidated, so a connected
    /// client is disconnected and has to pair again — callers should confirm first.
    /// </summary>
    public string RegenerateCode()
    {
        FramedConnection? connection;
        lock (_gate)
        {
            PairingCodeValue = PairingCode.Generate();
            _sessionToken = null;
            _authFailures = 0;
            _lockoutUntil = DateTime.MinValue;
            connection = _connection;
            log.Info("RemoteServer: pairing code regenerated");
        }

        connection?.Close(DisconnectReason.CodeRegenerated, "The pairing code was regenerated.");
        RaiseStateChanged();
        return PairingCodeValue!;
    }

    private void StopInternal(DisconnectReason reason, string message)
    {
        FramedConnection? connection;
        IRemoteServerProjects? provider;

        lock (_gate)
        {
            if (State == RemoteServerState.Stopped) return;

            connection = _connection;
            provider = _provider;

            _connection = null;
            State = RemoteServerState.Stopped;
            ConnectedClientName = null;
            PairingCodeValue = null;
            ConnectAddress = null;
            ConnectAddresses = [];

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts = null;

            _certificate?.Dispose();
            _certificate = null;

            SetThreadExecutionState(EsContinuous);
        }

        connection?.Close(reason, message);

        if (provider != null)
        {
            provider.ActiveProjectsChanged -= OnActiveProjectsChanged;
            RunOnUi(() => provider.SetAgentInputLocked(false, promptsLocked: false));
        }

        DisposePublishers();
        log.Info($"RemoteServer: stopped — {reason}");
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------ accepting clients

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                log.Warn($"RemoteServer: accept failed — {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var peer = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        log.Info($"RemoteServer: incoming connection from {peer}");

        SslStream? tls = null;
        try
        {
            client.NoDelay = true;
            tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct).ConfigureAwait(false);

            await FramedConnection.WriteMagicAsync(tls, ct).ConfigureAwait(false);
            if (!await FramedConnection.ReadMagicAsync(tls, ct).ConfigureAwait(false))
            {
                log.Warn($"RemoteServer: {peer} did not send the Agent Dock preamble");
                tls.Dispose();
                client.Dispose();
                return;
            }

            var connection = new FramedConnection(tls, log, $"server:{peer}");

            await connection.SendDirectAsync(new ServerHelloMsg(
                RemoteProtocol.Version,
                HostName,
                App.Version,
                CertificateFingerprint ?? ""), ct).ConfigureAwait(false);

            if (await connection.ReceiveDirectAsync(ct).ConfigureAwait(false) is not ClientHelloCmd hello)
            {
                connection.Close(DisconnectReason.ProtocolMismatch, "Expected a client hello.");
                return;
            }

            if (hello.ProtocolVersion != RemoteProtocol.Version)
            {
                log.Warn($"RemoteServer: {peer} speaks protocol {hello.ProtocolVersion}, we speak {RemoteProtocol.Version}");
                connection.Close(
                    DisconnectReason.ProtocolMismatch,
                    $"This server speaks protocol v{RemoteProtocol.Version}; the client speaks v{hello.ProtocolVersion}. " +
                    "Update both machines to the same Agent Dock version.");
                return;
            }

            // One client at a time: input ownership is then never ambiguous.
            lock (_gate)
            {
                if (_connection != null)
                {
                    log.Info($"RemoteServer: refused {peer} — '{ConnectedClientName}' is already connected");
                    connection.Close(
                        DisconnectReason.AlreadyInUse,
                        $"'{ConnectedClientName}' is already connected to this server.");
                    return;
                }
            }

            if (!await AuthenticateAsync(connection, hello, peer, ct).ConfigureAwait(false))
                return;

            AttachClient(connection, hello.ClientName, peer);
        }
        catch (OperationCanceledException)
        {
            tls?.Dispose();
            client.Dispose();
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteServer: handshake with {peer} failed — {ex.Message}");
            tls?.Dispose();
            client.Dispose();
        }
    }

    private async Task<bool> AuthenticateAsync(
        FramedConnection connection,
        ClientHelloCmd hello,
        string peer,
        CancellationToken ct)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow < _lockoutUntil)
            {
                var remaining = (int)(_lockoutUntil - DateTime.UtcNow).TotalSeconds;
                log.Warn($"RemoteServer: {peer} rejected — pairing locked out for another {remaining}s");
                connection.Close(DisconnectReason.AuthFailed,
                    $"Too many failed attempts. Try again in {remaining} seconds.");
                return false;
            }
        }

        var nonce = PairingCode.NewNonce();
        await connection.SendDirectAsync(new AuthChallengeMsg(nonce), ct).ConfigureAwait(false);

        if (await connection.ReceiveDirectAsync(ct).ConfigureAwait(false) is not AuthProofCmd proof)
        {
            connection.Close(DisconnectReason.AuthFailed, "Expected a pairing proof.");
            return false;
        }

        bool ok;
        lock (_gate)
        {
            // A stored token lets a dropped Wi-Fi connection come back without re-prompting the
            // human for a code they have already typed once.
            ok = PairingCode.TokensMatch(_sessionToken, proof.SessionToken)
                 || (PairingCodeValue != null && PairingCode.VerifyProof(PairingCodeValue, nonce, proof.CodeProof));
        }

        if (!ok)
        {
            int failures;
            lock (_gate)
            {
                failures = ++_authFailures;
                if (failures >= MaxAuthFailures)
                {
                    _lockoutUntil = DateTime.UtcNow.Add(AuthLockout);
                    _authFailures = 0;
                }
            }

            var note = $"Pairing attempt from {peer} was rejected ({failures}/{MaxAuthFailures}).";
            log.Warn($"RemoteServer: {note}");
            RunOnUi(() => AuthFailureReported?.Invoke(note));

            connection.Close(DisconnectReason.AuthFailed, "The pairing code was not accepted.");
            return false;
        }

        string token;
        lock (_gate)
        {
            _authFailures = 0;
            _sessionToken = PairingCode.NewSessionToken();
            token = _sessionToken;
        }

        await connection
            .SendDirectAsync(new AuthResultMsg(true, token, DisconnectReason.Unknown, null, null), ct)
            .ConfigureAwait(false);

        log.Info($"RemoteServer: {peer} paired as '{hello.ClientName}'");
        return true;
    }

    // ------------------------------------------------------------------ attached client

    private void AttachClient(FramedConnection connection, string clientName, string peer)
    {
        lock (_gate)
        {
            _connection = connection;
            ConnectedClientName = string.IsNullOrWhiteSpace(clientName) ? peer : clientName;
            State = RemoteServerState.ClientConnected;
        }

        connection.FrameReceived += frame => RunOnUi(() => HandleCommand(connection, frame));
        connection.Closed += (reason, message) => OnClientClosed(connection, reason, message);
        connection.Start();

        RunOnUi(() =>
        {
            BuildPublishers(connection);

            // With a client attached the prompts lock too: the human is at the other end, and two
            // live answerers for one question is a race. Stop Server is not agent interaction and
            // stays live, which is how a returning user reclaims control.
            _provider?.SetAgentInputLocked(true, promptsLocked: true);

            SendHostSnapshot(connection);
            RaiseStateChanged();
        });
    }

    private void OnClientClosed(FramedConnection connection, DisconnectReason reason, string? message)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_connection, connection)) return;

            _connection = null;
            ConnectedClientName = null;
            if (State == RemoteServerState.ClientConnected)
                State = RemoteServerState.Listening;
        }

        log.Info($"RemoteServer: client disconnected — {reason} {message}");

        RunOnUi(() =>
        {
            DisposePublishers();
            // Still hosting, so the composer stays locked; the prompts come back, because this
            // machine is once again the only place they can be answered.
            _provider?.SetAgentInputLocked(true, promptsLocked: false);
            RaiseStateChanged();
        });
    }

    private void BuildPublishers(FramedConnection connection)
    {
        DisposePublishers();

        var created = _provider?.CreatePublishers() ?? [];
        foreach (var publisher in created)
        {
            publisher.Publish += frame => connection.Send(frame);
            _publishers.Add(publisher);
            _byId[publisher.Id] = publisher;
        }
    }

    private void DisposePublishers()
    {
        foreach (var publisher in _publishers)
            publisher.Dispose();

        _publishers.Clear();
        _byId.Clear();
    }

    private void SendHostSnapshot(FramedConnection connection)
    {
        connection.Send(new HostSnapshotMsg(
            HostName,
            _publishers.Select(p => p.Describe()).ToList()));

        foreach (var publisher in _publishers)
            connection.Send(publisher.Snapshot());
    }

    /// <summary>
    /// The active session set changed. Rather than diffing tab-by-tab, the publishers are rebuilt
    /// and a fresh host snapshot is sent: the set is small, this happens rarely, and it cannot
    /// drift out of sync the way an incremental path could.
    /// </summary>
    private void OnActiveProjectsChanged()
    {
        FramedConnection? connection;
        lock (_gate) connection = _connection;
        if (connection == null) return;

        RunOnUi(() =>
        {
            BuildPublishers(connection);
            _provider?.SetAgentInputLocked(true, promptsLocked: true);
            SendHostSnapshot(connection);
        });
    }

    private void HandleCommand(FramedConnection connection, RemoteFrame frame)
    {
        try
        {
            var projectId = frame switch
            {
                SendMessageCmd c => c.ProjectId,
                AllowPermissionCmd c => c.ProjectId,
                DenyPermissionCmd c => c.ProjectId,
                AnswerQuestionCmd c => c.ProjectId,
                ListDirectoryCmd c => c.ProjectId,
                ReadFileCmd c => c.ProjectId,
                ReadDiffCmd c => c.ProjectId,
                WatchDirectoryCmd c => c.ProjectId,
                UpdateTodoItemsCmd c => c.ProjectId,
                LocalCommandCmd c => c.ProjectId,
                ResyncCmd c => c.ProjectId,
                AckCmd c => c.ProjectId,
                _ => null,
            };

            if (projectId == null) return;

            // Acks are informational for now: the recovery path is a resync, which cannot be
            // subtly wrong the way a replay-from-buffer can.
            if (frame is AckCmd) return;

            if (!_byId.TryGetValue(projectId, out var publisher))
            {
                log.Warn($"RemoteServer: command for unknown project '{projectId}'");
                return;
            }

            if (frame is WatchDirectoryCmd) return; // pinning is tracked client-side for now

            var reply = publisher.HandleCommand(frame);
            if (reply != null)
                connection.Send(reply);
        }
        catch (Exception ex)
        {
            log.Error("RemoteServer: command handling failed", ex);
        }
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private void RaiseStateChanged() => RunOnUi(() => StateChanged?.Invoke());

    public void Dispose() => StopInternal(DisconnectReason.ServerStopped, "Agent Dock is closing.");
}
