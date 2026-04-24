using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Fetches Claude Code plan usage (the same data shown in /status → Usage).
/// Reads the OAuth token from ~/.claude/.credentials.json and calls
/// GET https://api.anthropic.com/api/oauth/usage.
/// </summary>
public static class UsageService
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public enum FetchStatus
    {
        Success,
        AuthMissing,    // credentials file not found / malformed
        AuthExpired,    // HTTP 401 — user needs to log in again via Claude Code
        NetworkError,   // DNS / connection / timeout
        ServerError,    // HTTP 5xx or unexpected response
    }

    public record FetchResult(FetchStatus Status, UsageSummary? Summary, string? ErrorMessage);

    public static async Task<FetchResult> FetchAsync(CancellationToken ct = default)
    {
        string? token;
        try
        {
            token = ReadOAuthToken();
        }
        catch (Exception ex)
        {
            return new FetchResult(FetchStatus.AuthMissing, null, ex.Message);
        }

        if (string.IsNullOrEmpty(token))
            return new FetchResult(FetchStatus.AuthMissing, null, "Access token not found in credentials file");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", OAuthBeta);

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new FetchResult(FetchStatus.AuthExpired, null, "OAuth token expired");

            if (!response.IsSuccessStatusCode)
                return new FetchResult(FetchStatus.ServerError, null, $"HTTP {(int)response.StatusCode}");

            var summary = await response.Content.ReadFromJsonAsync<UsageSummary>(cancellationToken: ct);
            return new FetchResult(FetchStatus.Success, summary, null);
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult(FetchStatus.NetworkError, null, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return new FetchResult(FetchStatus.NetworkError, null, "Request timed out: " + ex.Message);
        }
    }

    private static string? ReadOAuthToken()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");

        if (!File.Exists(path))
            throw new FileNotFoundException("Claude credentials file not found", path);

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            return null;
        if (!oauth.TryGetProperty("accessToken", out var tokenEl))
            return null;

        return tokenEl.GetString();
    }
}
