using System.Net;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services.Remote;

/// <summary>
/// The browser-facing half of Server Mode: a plain HTTP + WebSocket listener, started and
/// stopped alongside the native TCP+TLS listener by <see cref="RemoteServerService"/>, which it
/// knows nothing about beyond the request handler it is handed. All auth, session-slot and
/// publisher logic stays in <see cref="RemoteServerService"/> — this class is routing plumbing
/// only, on purpose, so there is exactly one place that owns "who is allowed in."
///
/// Deliberately plain HTTP, no TLS of its own: Agent Dock does not take on certificate
/// management. A LAN connection works as-is; anything beyond the LAN is the user's own reverse
/// proxy's job, documented as such, never Agent Dock's responsibility.
/// </summary>
public sealed class RemoteWebListener(ILogService log)
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public int? Port { get; private set; }
    public bool IsRunning => _listener != null;

    public sealed record StartResult(bool Success, string? Error);

    /// <summary>
    /// Binds and starts accepting. <paramref name="requestHandler"/> is invoked once per request
    /// (including WebSocket upgrades) on its own task; this class does not inspect the request.
    /// </summary>
    public StartResult Start(RemoteBindScope scope, int port, Func<HttpListenerContext, Task> requestHandler)
    {
        if (IsRunning) return new StartResult(false, "The web listener is already running.");

        var listener = new HttpListener();
        try
        {
            if (scope == RemoteBindScope.AllInterfaces)
            {
                // "+" binds every interface. On Windows this needs a one-time URL ACL
                // reservation unless the process is elevated - surfaced below rather than left
                // as an opaque AccessDenied the first time someone ticks this option.
                listener.Prefixes.Add($"http://+:{port}/");
            }
            else
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Prefixes.Add($"http://localhost:{port}/");
            }

            listener.Start();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5) // ERROR_ACCESS_DENIED
        {
            log.Warn($"RemoteWebListener: access denied binding port {port} for all interfaces");
            return new StartResult(false,
                $"Windows needs a one-time permission to share port {port} on all interfaces. " +
                $"Run this once as administrator, then try again:\n" +
                $"netsh http add urlacl url=http://+:{port}/ user=Everyone");
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteWebListener: failed to start on port {port} — {ex.Message}");
            try { listener.Close(); } catch { }
            return new StartResult(false, ex.Message);
        }

        _listener = listener;
        Port = port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, requestHandler, _cts.Token));

        log.Info($"RemoteWebListener: listening on port {port} ({scope})");
        return new StartResult(true, null);
    }

    public void Stop()
    {
        if (!IsRunning) return;

        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }

        _listener = null;
        _cts = null;
        Port = null;

        log.Info("RemoteWebListener: stopped");
    }

    private async Task AcceptLoopAsync(
        HttpListener listener, Func<HttpListenerContext, Task> requestHandler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { return; }
            catch (HttpListenerException) { return; }
            catch (Exception ex)
            {
                log.Warn($"RemoteWebListener: accept failed — {ex.Message}");
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await requestHandler(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Warn($"RemoteWebListener: request handling failed — {ex.Message}");
                    try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
                }
            }, ct);
        }
    }
}
