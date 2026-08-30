using System.Diagnostics;
using AgentDock.Models;

namespace AgentDock.Services.Abstractions;

/// <summary>Manages the configured Claude logins and their isolated config directories.</summary>
public interface IAccountManager
{
    string AccountsRoot { get; }
    string ConfigDirFor(string accountId);

    List<ClaudeAccount> Load();
    void Save(List<ClaudeAccount> accounts);
    ClaudeAccount Add(string name);
    void Remove(string accountId, bool deleteFiles);
    string? ReadEmail(string accountId);
    bool IsLoggedIn(string accountId);
    Process? LaunchLogin(string accountId);
    Task<bool> RunLoginAsync(string accountId, CancellationToken ct = default);
}

/// <summary>Outcome of a plan-usage fetch.</summary>
public enum UsageFetchStatus
{
    Success,
    AuthMissing,    // credentials file not found / malformed / tokens blanked out
    AuthExpired,    // HTTP 401 — the token was rejected outright
    TokenStale,     // stored token lapsed; the account is fine, we just can't read usage
    NetworkError,   // DNS / connection / timeout
    RateLimited,    // HTTP 429 — back off (honours Retry-After if present)
    ServerError,    // HTTP 5xx or unexpected response
}

public record UsageFetchResult(
    UsageFetchStatus Status,
    UsageSummary? Summary,
    string? ErrorMessage,
    TimeSpan? RetryAfter = null);

/// <summary>Reads plan usage for a login from the Anthropic OAuth usage endpoint.</summary>
public interface IUsageService
{
    Task<UsageFetchResult> FetchAsync(string? configDir = null, CancellationToken ct = default);
}

/// <summary>
/// Where the agent CLI lives and whether it is usable. Separated from <c>ClaudeSession</c>
/// so the binary path is no longer static mutable state — a process that hosts more than
/// one project host cannot share one global path.
/// </summary>
public interface IClaudeEnvironment
{
    string BinaryPath { get; set; }
    bool IsAvailable();
    string ResolveBinaryPath();
}
