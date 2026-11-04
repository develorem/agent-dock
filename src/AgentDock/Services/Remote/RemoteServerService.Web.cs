using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentDock.Services.Remote;

/// <summary>
/// The browser half of Server Mode: routes requests from <see cref="RemoteWebListener"/> into
/// the same auth state and the same <see cref="AttachClient"/> path the native TCP listener
/// uses, so there is exactly one place that decides who gets the connection slot.
///
/// Auth bridge, in short: a browser cannot do the native handshake's raw binary challenge, so it
/// does the same HMAC-over-nonce proof via two small JSON endpoints instead, computed client-side
/// with the Web Crypto API. Success sets an HttpOnly session cookie; the WebSocket upgrade at
/// <c>/ws</c> is gated on that cookie, never on the raw code. No TLS of its own - see
/// <see cref="RemoteWebListener"/> and docs/remote-sessions.md for why that is deliberate.
/// </summary>
public sealed partial class RemoteServerService
{
    private const string SessionCookieName = "adsess";
    private static readonly TimeSpan WebAuthAttemptTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (string Nonce, DateTime Expiry)> _webAuthAttempts = new();

    private static readonly Dictionary<string, string> StaticAssets = new()
    {
        ["/"] = "index.html",
        ["/index.html"] = "index.html",
        ["/app.js"] = "app.js",
        ["/style.css"] = "style.css",
    };

    private static readonly Dictionary<string, string> ContentTypes = new()
    {
        ["index.html"] = "text/html; charset=utf-8",
        ["app.js"] = "application/javascript; charset=utf-8",
        ["style.css"] = "text/css; charset=utf-8",
    };

    private async Task HandleWebRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";

        try
        {
            if (request.IsWebSocketRequest && path == "/ws")
            {
                await HandleWebSocketUpgradeAsync(context).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/auth/challenge")
            {
                await HandleAuthChallengeAsync(context).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/auth/proof")
            {
                await HandleAuthProofAsync(context).ConfigureAwait(false);
                return;
            }

            await ServeStaticAsync(context, path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteServer: web request to {path} failed — {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ auth bridge

    private async Task HandleAuthChallengeAsync(HttpListenerContext context)
    {
        if (TryCheckLockout(out var lockoutMessage))
        {
            await WriteJsonAsync(context, 429, new { Error = lockoutMessage }).ConfigureAwait(false);
            return;
        }

        PruneExpiredWebAuthAttempts();

        var nonce = PairingCode.NewNonce();
        var attemptId = Guid.NewGuid().ToString("N");
        _webAuthAttempts[attemptId] = (nonce, DateTime.UtcNow + WebAuthAttemptTtl);

        // PascalCase, matching the WebSocket frames' own convention (no naming policy is set on
        // FramedConnection.JsonOptions) - one casing to remember, not two.
        await WriteJsonAsync(context, 200, new { AttemptId = attemptId, Nonce = nonce }).ConfigureAwait(false);
    }

    private async Task HandleAuthProofAsync(HttpListenerContext context)
    {
        var peer = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
        var body = await ReadJsonBodyAsync<WebAuthProofRequest>(context.Request).ConfigureAwait(false);

        if (body == null
            || !_webAuthAttempts.TryRemove(body.AttemptId, out var attempt)
            || attempt.Expiry < DateTime.UtcNow)
        {
            await WriteJsonAsync(context, 400, new { Error = "Unknown or expired pairing attempt." })
                .ConfigureAwait(false);
            return;
        }

        if (!VerifyProofAndTrackFailures(attempt.Nonce, body.Proof, sessionToken: null, peer))
        {
            await WriteJsonAsync(context, 401, new { Error = "The pairing code was not accepted." })
                .ConfigureAwait(false);
            return;
        }

        var token = IssueSessionToken();

        // No Secure flag: this listener is plain HTTP by design (RemoteWebListener), and a
        // Secure cookie is silently dropped over http://. Real encryption is the user's own
        // reverse proxy's job - see docs/remote-sessions.md.
        context.Response.AppendHeader(
            "Set-Cookie", $"{SessionCookieName}={token}; HttpOnly; SameSite=Strict; Path=/; Max-Age=86400");

        log.Info($"RemoteServer: {peer} paired over the browser bridge");
        await WriteJsonAsync(context, 200, new { Success = true }).ConfigureAwait(false);
    }

    private void PruneExpiredWebAuthAttempts()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, value) in _webAuthAttempts)
        {
            if (value.Expiry < now)
                _webAuthAttempts.TryRemove(key, out _);
        }
    }

    private sealed record WebAuthProofRequest(string AttemptId, string Proof);

    // ------------------------------------------------------------------ websocket upgrade

    private async Task HandleWebSocketUpgradeAsync(HttpListenerContext context)
    {
        var cookie = context.Request.Cookies[SessionCookieName]?.Value;

        bool ok;
        lock (_gate) ok = PairingCode.TokensMatch(_sessionToken, cookie);

        if (!ok)
        {
            context.Response.StatusCode = 401;
            context.Response.Close();
            return;
        }

        var peer = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        var connection = new WebSocketConnection(wsContext.WebSocket, log, $"web:{peer}");

        // Browsers have no equivalent of the native client's configured machine name.
        AttachClient(connection, "Browser", peer);
    }

    // ------------------------------------------------------------------ static assets

    private async Task ServeStaticAsync(HttpListenerContext context, string path)
    {
        if (!StaticAssets.TryGetValue(path, out var fileName))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        var assembly = typeof(RemoteServerService).Assembly;
        using var stream = assembly.GetManifestResourceStream($"WebClient/{fileName}");
        if (stream == null)
        {
            log.Warn($"RemoteServer: embedded web asset '{fileName}' not found");
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        context.Response.ContentType = ContentTypes.GetValueOrDefault(fileName, "application/octet-stream");
        context.Response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        context.Response.Close();
    }

    // ------------------------------------------------------------------ json helpers

    private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, object body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = json.Length;
        await context.Response.OutputStream.WriteAsync(json).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return default;

        return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
    }
}
