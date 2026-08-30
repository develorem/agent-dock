using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Threading;
using AgentDock.Controls;
using AgentDock.Models;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services.Remote;

public enum RemoteClientState { Disconnected, Connecting, AwaitingCode, Connected, Reconnecting }

/// <summary>
/// Why a connection attempt stopped, in a form the connect dialog can act on rather than just
/// display. <see cref="NeedsCode"/> is the one that is not an error: it means the transport is
/// fine and the server is now asking who you are.
/// </summary>
public sealed record RemoteConnectOutcome(
    bool Success,
    bool NeedsCode,
    DisconnectReason Reason,
    string? Message,
    string? ServerName,
    string? Fingerprint,
    bool FingerprintChanged);

/// <summary>
/// Connects to a hosting Agent Dock and keeps the connection alive.
///
/// The handshake is deliberately two-phase so the UI can behave the way the feature was
/// described: connect first (which proves the address is right and shows which machine answered),
/// then prompt for the pairing code. A single-shot connect would have to ask for the code before
/// knowing whether the host even exists.
///
/// Reconnects are automatic and silent: they reuse the session token issued at pairing, so a
/// dropped Wi-Fi connection does not make the user re-type a code, and the panels are marked
/// stale rather than cleared while it happens.
/// </summary>
public sealed class RemoteClientService(
    ILogService log,
    RemoteIdentity identity)
    : IRemoteChatChannel, IRemoteFileChannel, IRemoteProjectSettingsChannel, IDisposable
{
    private readonly object _gate = new();

    private TcpClient? _tcp;
    private SslStream? _tls;
    private FramedConnection? _connection;
    private Dispatcher? _dispatcher;
    private CancellationTokenSource? _reconnectCts;

    private string _host = "";
    private int _port;
    private string? _pendingNonce;
    private string? _sessionToken;
    private string? _code;
    private bool _userRequestedDisconnect;

    public RemoteClientState State { get; private set; } = RemoteClientState.Disconnected;
    public string? ServerName { get; private set; }
    public string? ServerFingerprint { get; private set; }
    public string Endpoint => _port == 0 ? _host : $"{_host}:{_port}";

    /// <summary>Frames from the server, already marshalled to the UI thread.</summary>
    public event Action<RemoteFrame>? FrameReceived;

    public event Action? StateChanged;

    /// <summary>Terminal or transient loss, with a reason the user can act on.</summary>
    public event Action<DisconnectReason, string?>? Disconnected;

    // ------------------------------------------------------------------ connecting

    /// <summary>
    /// Phase one: open the socket, complete TLS, verify the pinned fingerprint and exchange
    /// hellos. Returns with <see cref="RemoteConnectOutcome.NeedsCode"/> set when the server is
    /// ready for a pairing code.
    /// </summary>
    public async Task<RemoteConnectOutcome> ConnectAsync(
        string host,
        int port,
        Dispatcher dispatcher,
        CancellationToken ct = default)
    {
        Disconnect(notify: false);

        _host = host;
        _port = port;
        _dispatcher = dispatcher;
        _userRequestedDisconnect = false;
        SetState(RemoteClientState.Connecting);

        try
        {
            _tcp = new TcpClient { NoDelay = true };

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await _tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }

            var fingerprintChanged = false;
            var pinned = identity.GetPinnedFingerprint(host);
            string? observed = null;

            _tls = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, certificate, _, _) =>
                {
                    // The certificate is self-signed by design, so chain validation is not the
                    // question. The question is whether this is the same machine we paired with.
                    if (certificate == null) return false;

                    observed = RemoteIdentity.FingerprintOf(
                        X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));

                    if (pinned != null && pinned != observed)
                        fingerprintChanged = true;

                    // Accept the transport here; the caller decides what a changed fingerprint
                    // means. Failing inside the callback would lose the detail needed to explain
                    // it, and no secret has been sent yet.
                    return true;
                });

            await _tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct).ConfigureAwait(false);

            if (!await FramedConnection.ReadMagicAsync(_tls, ct).ConfigureAwait(false))
            {
                Disconnect(notify: false);
                return Failure(DisconnectReason.ProtocolMismatch,
                    "That address answered, but it is not an Agent Dock server.");
            }

            await FramedConnection.WriteMagicAsync(_tls, ct).ConfigureAwait(false);

            var connection = new FramedConnection(_tls, log, $"client:{host}:{port}");

            var (hello, helloRefusal) = await ExpectAsync<ServerHelloMsg>(
                connection, "The server did not identify itself.", ct).ConfigureAwait(false);
            // The tuple cannot express "one or the other", so the null check is explicit rather
            // than suppressed — a refusal always carries an outcome, and a frame always carries a value.
            if (helloRefusal != null || hello == null)
                return helloRefusal ?? Failure(DisconnectReason.ConnectionLost, "Handshake failed.");

            if (hello.ProtocolVersion != RemoteProtocol.Version)
            {
                Disconnect(notify: false);
                return Failure(DisconnectReason.ProtocolMismatch,
                    $"This client speaks protocol v{RemoteProtocol.Version}; " +
                    $"'{hello.MachineName}' speaks v{hello.ProtocolVersion}. " +
                    "Update both machines to the same Agent Dock version.");
            }

            ServerName = hello.MachineName;
            ServerFingerprint = observed ?? hello.CertFingerprint;

            await connection.SendDirectAsync(
                new ClientHelloCmd(RemoteProtocol.Version, Environment.MachineName, App.Version),
                ct).ConfigureAwait(false);

            var (challenge, challengeRefusal) = await ExpectAsync<AuthChallengeMsg>(
                connection, "The server did not offer a pairing challenge.", ct).ConfigureAwait(false);
            if (challengeRefusal != null || challenge == null)
                return challengeRefusal ?? Failure(DisconnectReason.ConnectionLost, "Handshake failed.");

            _pendingNonce = challenge.Nonce;
            _connection = connection;
            SetState(RemoteClientState.AwaitingCode);

            log.Info($"RemoteClient: reached '{hello.MachineName}' at {host}:{port}, awaiting pairing code");

            return new RemoteConnectOutcome(
                Success: false,
                NeedsCode: true,
                DisconnectReason.Unknown,
                null,
                hello.MachineName,
                ServerFingerprint,
                fingerprintChanged);
        }
        catch (OperationCanceledException)
        {
            Disconnect(notify: false);
            return Failure(DisconnectReason.ConnectionLost,
                $"Could not reach {host}:{port} — the connection timed out.");
        }
        catch (Exception ex)
        {
            Disconnect(notify: false);
            return Failure(DisconnectReason.ConnectionLost, $"Could not connect to {host}:{port} — {ex.Message}");
        }
    }

    /// <summary>Phase two: prove the pairing code and start replicating.</summary>
    public async Task<RemoteConnectOutcome> SubmitCodeAsync(string typedCode, CancellationToken ct = default)
    {
        var connection = _connection;
        var nonce = _pendingNonce;

        if (connection == null || nonce == null)
            return Failure(DisconnectReason.ConnectionLost, "Not connected.");

        var code = PairingCode.Normalize(typedCode);
        var proof = PairingCode.ComputeProof(code, nonce);

        try
        {
            await connection.SendDirectAsync(new AuthProofCmd(proof, null), ct).ConfigureAwait(false);

            var (result, resultRefusal) = await ExpectAsync<AuthResultMsg>(
                connection, "The server did not answer the pairing attempt.", ct).ConfigureAwait(false);
            if (resultRefusal != null || result == null)
                return resultRefusal ?? Failure(DisconnectReason.ConnectionLost, "Pairing failed.");

            if (!result.Success)
            {
                var message = result.Message ?? DisconnectReasonText.Describe(result.FailureReason);
                Disconnect(notify: false);
                return Failure(result.FailureReason, message);
            }

            _code = code;
            _sessionToken = result.SessionToken;

            // Pin on first successful pairing. Doing it only after the code is accepted means a
            // wrong address never leaves a bogus entry behind.
            if (ServerFingerprint != null)
                identity.PinFingerprint(_host, ServerFingerprint);

            Attach(connection);
            log.Info($"RemoteClient: paired with '{ServerName}'");

            return new RemoteConnectOutcome(true, false, DisconnectReason.Unknown, null,
                ServerName, ServerFingerprint, false);
        }
        catch (Exception ex)
        {
            Disconnect(notify: false);
            return Failure(DisconnectReason.ConnectionLost, ex.Message);
        }
    }

    /// <summary>
    /// Reads the next handshake frame, expecting <typeparamref name="T"/>.
    ///
    /// The important case is the one that is not <typeparamref name="T"/>: a server that is
    /// refusing us sends a <see cref="DisconnectMsg"/> carrying the actual reason — locked out,
    /// already in use, version mismatch. Treating that as "unexpected frame" would replace a
    /// precise, actionable message with a generic one, which is exactly the failure mode that
    /// makes remote features feel inscrutable.
    /// </summary>
    private async Task<(T? Frame, RemoteConnectOutcome? Refusal)> ExpectAsync<T>(
        FramedConnection connection,
        string whatWasExpected,
        CancellationToken ct) where T : RemoteFrame
    {
        var frame = await connection.ReceiveDirectAsync(ct).ConfigureAwait(false);

        switch (frame)
        {
            case T expected:
                return (expected, null);

            case DisconnectMsg refusal:
                Disconnect(notify: false);
                return (null, Failure(
                    refusal.Reason,
                    refusal.Message ?? DisconnectReasonText.Describe(refusal.Reason)));

            case null:
                Disconnect(notify: false);
                return (null, Failure(DisconnectReason.ConnectionLost,
                    "The server closed the connection during the handshake."));

            default:
                Disconnect(notify: false);
                return (null, Failure(DisconnectReason.ProtocolMismatch, whatWasExpected));
        }
    }

    private void Attach(FramedConnection connection)
    {
        connection.FrameReceived += frame => RunOnUi(() => FrameReceived?.Invoke(frame));
        connection.Closed += OnConnectionClosed;
        connection.Start();
        SetState(RemoteClientState.Connected);
    }

    // ------------------------------------------------------------------ reconnect

    private void OnConnectionClosed(DisconnectReason reason, string? message)
    {
        lock (_gate) _connection = null;

        RunOnUi(() => Disconnected?.Invoke(reason, message));

        // Terminal reasons: retrying would either fail identically or be wrong.
        if (_userRequestedDisconnect ||
            reason is DisconnectReason.ServerStopped
                or DisconnectReason.Kicked
                or DisconnectReason.CodeRegenerated
                or DisconnectReason.ProtocolMismatch
                or DisconnectReason.AuthFailed
                or DisconnectReason.AlreadyInUse)
        {
            SetState(RemoteClientState.Disconnected);
            return;
        }

        SetState(RemoteClientState.Reconnecting);
        _reconnectCts = new CancellationTokenSource();
        _ = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
    }

    /// <summary>
    /// Reconnects with backoff, using the session token so the user is not asked for the code
    /// again. Capped rather than unbounded: a machine that is simply switched off should not have
    /// the client spinning forever, but a laptop lid closed for ten minutes should recover.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        var delays = new[] { 1, 2, 4, 8, 15, 30, 30, 30, 60, 60 };

        foreach (var seconds in delays)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            log.Info($"RemoteClient: reconnect attempt to {_host}:{_port}");

            var outcome = await ConnectAsync(_host, _port, _dispatcher!, ct).ConfigureAwait(false);
            if (!outcome.NeedsCode) continue;

            var connection = _connection;
            if (connection == null) continue;

            try
            {
                await connection
                    .SendDirectAsync(new AuthProofCmd(
                        _code != null && _pendingNonce != null
                            ? PairingCode.ComputeProof(_code, _pendingNonce)
                            : null,
                        _sessionToken), ct)
                    .ConfigureAwait(false);

                if (await connection.ReceiveDirectAsync(ct).ConfigureAwait(false) is AuthResultMsg { Success: true } result)
                {
                    _sessionToken = result.SessionToken ?? _sessionToken;
                    Attach(connection);
                    log.Info($"RemoteClient: reconnected to '{ServerName}'");
                    return;
                }
            }
            catch (Exception ex)
            {
                log.Warn($"RemoteClient: reconnect attempt failed — {ex.Message}");
            }
        }

        log.Warn("RemoteClient: giving up reconnecting");
        SetState(RemoteClientState.Disconnected);
        RunOnUi(() => Disconnected?.Invoke(
            DisconnectReason.ConnectionLost,
            "Could not reconnect to the server."));
    }

    // ------------------------------------------------------------------ commands out

    private void Send(RemoteFrame frame)
    {
        var connection = _connection;
        if (connection == null)
        {
            log.Warn($"RemoteClient: dropped {frame.GetType().Name} — not connected");
            return;
        }

        connection.Send(frame);
    }

    public void SendMessage(
        string projectId,
        string messageId,
        string text,
        IReadOnlyList<ImageAttachment>? images)
    {
        var payload = images?
            .Select(i => new RemoteImageAttachment(
                "attachment",
                i.MediaType,
                Convert.FromBase64String(i.Base64Data)))
            .ToList();

        Send(new SendMessageCmd(messageId, projectId, text, payload));
    }

    public void AllowPermission(string projectId, string requestId)
        => Send(new AllowPermissionCmd(projectId, requestId));

    public void DenyPermission(string projectId, string requestId, string? reason)
        => Send(new DenyPermissionCmd(projectId, requestId, reason));

    public void AnswerQuestion(string projectId, string requestId, string questionText, string answer)
        => Send(new AnswerQuestionCmd(projectId, requestId, questionText, answer));

    public void SendLocalCommand(string projectId, string command)
        => Send(new LocalCommandCmd(projectId, command));

    public void ListDirectory(string projectId, string requestId, string relativePath)
        => Send(new ListDirectoryCmd(projectId, requestId, relativePath));

    public void ReadFile(string projectId, string requestId, string relativePath, string? knownVersion)
        => Send(new ReadFileCmd(projectId, requestId, relativePath, knownVersion));

    public void ReadDiff(string projectId, string requestId, string relativePath, bool staged)
        => Send(new ReadDiffCmd(projectId, requestId, relativePath, staged));

    public void WatchDirectory(string projectId, string relativePath, bool watch)
        => Send(new WatchDirectoryCmd(projectId, relativePath, watch));

    public void UpdateTodoItems(string projectId, List<RemoteTodoItem> items)
        => Send(new UpdateTodoItemsCmd(projectId, items));

    public void RequestResync(string projectId) => Send(new ResyncCmd(projectId));

    // ------------------------------------------------------------------ teardown

    public void Disconnect(bool notify = true)
    {
        _userRequestedDisconnect = true;

        try { _reconnectCts?.Cancel(); } catch { }
        _reconnectCts = null;

        FramedConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }

        connection?.Close(DisconnectReason.ClientRequested, "Disconnected by the user.", sendBye: notify);

        try { _tls?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _tls = null;
        _tcp = null;

        SetState(RemoteClientState.Disconnected);
    }

    private RemoteConnectOutcome Failure(DisconnectReason reason, string message)
    {
        SetState(RemoteClientState.Disconnected);
        return new RemoteConnectOutcome(false, false, reason, message, ServerName, ServerFingerprint, false);
    }

    private void SetState(RemoteClientState state)
    {
        if (State == state) return;
        State = state;
        RunOnUi(() => StateChanged?.Invoke());
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public void Dispose() => Disconnect(notify: false);
}
