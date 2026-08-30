using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentDock.Models;

using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>
/// Fetches Claude Code plan usage (the same data shown in /status → Usage).
/// Reads the OAuth token from ~/.claude/.credentials.json and calls
/// GET https://api.anthropic.com/api/oauth/usage.
///
/// STRICTLY READ-ONLY with respect to <c>.credentials.json</c>. This service must
/// never perform an OAuth refresh-token grant, and must never write that file.
/// Refresh tokens are single-use and rotate: the Claude CLI is the sole owner of
/// that lifecycle for a config dir. When this service also spent them, the two
/// writers raced, a consumed refresh token got re-presented, and the server
/// revoked the whole token family — silently logging the account out of Agent
/// Dock entirely. A stale usage number costs nothing; that did.
///
/// So a lapsed access token is reported as <see cref="UsageFetchStatus.TokenStale"/> —
/// explicitly not an auth error — and the usage line keeps showing its last known
/// figure until the CLI refreshes the token itself on next use. Don't "fix" this
/// by adding a refresh back.
/// </summary>
public sealed class UsageService : IUsageService
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";

    // Treat a token as spent slightly before its hard expiry, so we don't burn a
    // request on one that will lapse in flight.
    private readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Fetches plan usage for one login. <paramref name="configDir"/> is that account's
    /// <c>CLAUDE_CONFIG_DIR</c> (holding its <c>.credentials.json</c>); null reads the
    /// machine default <c>~/.claude</c>. The usage endpoint rate-limits by source, so
    /// callers should fetch accounts sequentially (not concurrently) and honour the
    /// <see cref="UsageFetchStatus.RateLimited"/> Retry-After to avoid tripping HTTP 429.
    /// </summary>
    public async Task<UsageFetchResult> FetchAsync(string? configDir = null, CancellationToken ct = default)
    {
        Creds creds;
        try
        {
            creds = ReadCredentials(configDir);
        }
        catch (Exception ex)
        {
            return new UsageFetchResult(UsageFetchStatus.AuthMissing, null, ex.Message);
        }

        if (string.IsNullOrEmpty(creds.AccessToken))
            return new UsageFetchResult(UsageFetchStatus.AuthMissing, null, "Access token not found in credentials file");

        // A lapsed token is reported, not renewed — see the class remarks. Skipping the
        // call also avoids a guaranteed-401 round trip against a rate-limited endpoint.
        //
        // This is TokenStale, not AuthExpired: the account is perfectly usable (chat works,
        // because the CLI refreshes on use) and the only casualty is this usage reading.
        // Reporting it as an auth error would cry wolf about a healthy login roughly once
        // an hour, which is what pushed us into refreshing tokens here in the first place.
        if (IsExpired(creds))
            return new UsageFetchResult(UsageFetchStatus.TokenStale, null, "Stored access token has lapsed");

        return await CallUsageAsync(creds.AccessToken, ct);
    }

    /// <summary>Calls the usage endpoint with the given bearer token and maps the response.</summary>
    private async Task<UsageFetchResult> CallUsageAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", OAuthBeta);

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new UsageFetchResult(UsageFetchStatus.AuthExpired, null, "OAuth token expired");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Honour Retry-After when the server sends it (delta seconds or a date).
                var ra = response.Headers.RetryAfter;
                var retryAfter = ra?.Delta
                    ?? (ra?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
                return new UsageFetchResult(UsageFetchStatus.RateLimited, null, "HTTP 429", retryAfter);
            }

            if (!response.IsSuccessStatusCode)
                return new UsageFetchResult(UsageFetchStatus.ServerError, null, $"HTTP {(int)response.StatusCode}");

            var summary = await response.Content.ReadFromJsonAsync<UsageSummary>(cancellationToken: ct);
            return new UsageFetchResult(UsageFetchStatus.Success, summary, null);
        }
        catch (HttpRequestException ex)
        {
            return new UsageFetchResult(UsageFetchStatus.NetworkError, null, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return new UsageFetchResult(UsageFetchStatus.NetworkError, null, "Request timed out: " + ex.Message);
        }
    }

    private bool IsExpired(Creds creds) =>
        creds.ExpiresAtMs > 0 &&
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= creds.ExpiresAtMs - (long)ExpirySkew.TotalMilliseconds;

    private string CredentialsPath(string? configDir)
    {
        var dir = string.IsNullOrWhiteSpace(configDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configDir;
        return Path.Combine(dir, ".credentials.json");
    }

    private Creds ReadCredentials(string? configDir)
    {
        var path = CredentialsPath(configDir);

        if (!File.Exists(path))
            throw new FileNotFoundException("Claude credentials file not found", path);

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            return new Creds(null, 0);

        var access = oauth.TryGetProperty("accessToken", out var a) ? a.GetString() : null;
        var expiresAt = oauth.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out var v) ? v : 0L;

        return new Creds(access, expiresAt);
    }

    /// <summary>
    /// The read-only slice of one account's <c>.credentials.json</c> that this service
    /// needs. Deliberately carries no refresh token and no file path — there is nothing
    /// here to refresh with or write back to. See the class remarks.
    /// </summary>
    private sealed record Creds(string? AccessToken, long ExpiresAtMs);
}
