using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Manages the set of configured Claude accounts. Each account owns a private
/// config directory under <see cref="AccountsRoot"/>; pointing a spawned
/// <c>claude</c> subprocess at it via the <c>CLAUDE_CONFIG_DIR</c> environment
/// variable makes that process act as an independent login. The registry
/// (id + friendly name) is persisted in settings.json; the authoritative
/// identity (email/org) and login state live in each account's own config dir.
/// </summary>
public static class AccountManager
{
    private const string SettingsKey = "Accounts";

    /// <summary>Root folder holding one config directory per account.</summary>
    public static readonly string AccountsRoot =
        Path.Combine(AppSettings.SettingsDir, "accounts");

    /// <summary>The <c>CLAUDE_CONFIG_DIR</c> path for a given account id.</summary>
    public static string ConfigDirFor(string accountId) =>
        Path.Combine(AccountsRoot, accountId);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Loads the configured accounts, or an empty list if none/parse error.</summary>
    public static List<ClaudeAccount> Load()
    {
        var json = AppSettings.GetString(SettingsKey);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<ClaudeAccount>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"AccountManager: failed to parse accounts — {ex.Message}");
            return [];
        }
    }

    /// <summary>Persists the accounts registry to settings.json.</summary>
    public static void Save(List<ClaudeAccount> accounts) =>
        AppSettings.SetString(SettingsKey, JsonSerializer.Serialize(accounts, JsonOpts));

    /// <summary>
    /// Creates a new account entry with its own config directory, persists it,
    /// and returns it. The account has no credentials until a login completes.
    /// </summary>
    public static ClaudeAccount Add(string name)
    {
        var accounts = Load();
        var id = Guid.NewGuid().ToString("N")[..8];
        var account = new ClaudeAccount
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim()
        };

        Directory.CreateDirectory(ConfigDirFor(id));
        accounts.Add(account);
        Save(accounts);
        Log.Info($"AccountManager: added account '{account.Name}' (id={id})");
        return account;
    }

    /// <summary>Removes an account from the registry, optionally deleting its config dir.</summary>
    public static void Remove(string accountId, bool deleteFiles)
    {
        var accounts = Load();
        accounts.RemoveAll(a => a.Id == accountId);
        Save(accounts);

        if (deleteFiles)
        {
            try
            {
                var dir = ConfigDirFor(accountId);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"AccountManager: failed to delete config dir for {accountId} — {ex.Message}");
            }
        }

        Log.Info($"AccountManager: removed account id={accountId} (deleteFiles={deleteFiles})");
    }

    /// <summary>
    /// Reads <c>oauthAccount.emailAddress</c> from the account's <c>.claude.json</c>,
    /// or null if the account has no identity yet.
    /// </summary>
    /// <remarks>
    /// This is an identity label, NOT a login check — it survives a logout, because the
    /// CLI clears tokens from <c>.credentials.json</c> and leaves <c>.claude.json</c>
    /// alone. Always pair it with <see cref="IsLoggedIn"/> before presenting an account
    /// as usable; showing the email on its own made revoked accounts look healthy.
    /// </remarks>
    public static string? ReadEmail(string accountId)
    {
        try
        {
            var file = Path.Combine(ConfigDirFor(accountId), ".claude.json");
            if (!File.Exists(file))
                return null;

            var node = JsonNode.Parse(File.ReadAllText(file));
            return node?["oauthAccount"]?["emailAddress"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True if the account's config dir holds a login that could actually work — a
    /// credentials file carrying at least one non-empty token.
    /// </summary>
    /// <remarks>
    /// Existence of the file is not enough. When the CLI's token refresh is rejected it
    /// rewrites <c>.credentials.json</c> in place with <c>accessToken</c>/
    /// <c>refreshToken</c> blanked and <c>expiresAt: 0</c>, keeping the surrounding
    /// metadata. The old existence check read that as "signed in", so dead accounts kept
    /// being offered and every session against them failed the instant it started.
    ///
    /// An empty access token with a live refresh token still counts: the access token
    /// lapses hourly and the CLI renews it on next use.
    /// </remarks>
    public static bool IsLoggedIn(string accountId)
    {
        try
        {
            var file = Path.Combine(ConfigDirFor(accountId), ".credentials.json");
            if (!File.Exists(file))
                return false;

            var oauth = JsonNode.Parse(File.ReadAllText(file))?["claudeAiOauth"];
            if (oauth == null)
                return false;

            return !string.IsNullOrEmpty(oauth["accessToken"]?.GetValue<string>())
                || !string.IsNullOrEmpty(oauth["refreshToken"]?.GetValue<string>());
        }
        catch
        {
            // Unreadable or malformed = not a login we can rely on.
            return false;
        }
    }

    /// <summary>
    /// Runs <c>claude auth login</c> in its own console window with
    /// <c>CLAUDE_CONFIG_DIR</c> pointed at this account, so the user completes the
    /// browser OAuth flow against that config dir. Returns the spawned process (so a
    /// caller can watch for it exiting), or null if it could not be started.
    /// </summary>
    /// <remarks>
    /// Credentials copied by hand don't work — each dir must complete a login of its
    /// own, after which the CLI keeps its tokens refreshed.
    ///
    /// This used to open a bare interactive <c>claude</c> session and count on the CLI
    /// prompting for a login, telling the user to type <c>/login</c> if it didn't. It
    /// doesn't prompt when a credentials file is already present — even a hollow one
    /// with its tokens blanked — so the window just sat at a normal session prompt.
    /// <c>claude auth login</c> is the CLI's dedicated sign-in command and goes straight
    /// into the browser flow regardless of what's on disk.
    /// </remarks>
    public static Process? LaunchLogin(string accountId)
    {
        var dir = ConfigDirFor(accountId);
        Directory.CreateDirectory(dir);

        var args = new List<string> { "auth", "login" };

        // Pre-fill the address this dir was last signed in as. With several accounts
        // the login pages are otherwise identical, and authenticating the wrong login
        // into this config dir is an easy and confusing mistake to make. The user can
        // still change it on the page.
        var email = ReadEmail(accountId);
        if (!string.IsNullOrWhiteSpace(email))
        {
            args.Add("--email");
            args.Add(email);
        }

        Log.Info($"AccountManager: launching login for id={accountId} at {dir}");

        // UseShellExecute=false does two things for us: it allows setting Environment
        // through the API (no `set` command built into a command line, so nothing to
        // mis-quote), and the child still gets its own console window, because this is
        // a GUI process with no console of its own to inherit. The previous comment
        // here claimed UseShellExecute=true was *required* for a console window; it
        // isn't, and believing that forced the whole shell-string approach.
        var psi = new ProcessStartInfo
        {
            FileName = ClaudeSession.ClaudeBinaryPath,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_CONFIG_DIR"] = dir;

        try
        {
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            // A shim install (npm's claude.cmd) isn't a PE image, so CreateProcess
            // rejects it. Those have to go through cmd.
            Log.Warn($"AccountManager: direct login launch failed ({ex.Message}) — retrying via cmd");
            return LaunchLoginViaShell(args, dir, accountId);
        }
    }

    // How long the login process may keep running after credentials have appeared on
    // disk before we close its window for it.
    private static readonly TimeSpan LingerGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When the account's credentials file was last written, or
    /// <see cref="DateTime.MinValue"/> if there isn't one. Used to tell a freshly
    /// completed login apart from credentials that were already sitting there.
    /// </summary>
    private static DateTime CredentialsWrittenAtUtc(string accountId)
    {
        try
        {
            var file = Path.Combine(ConfigDirFor(accountId), ".credentials.json");
            return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Runs the login for an account and waits for it to finish, returning whether the
    /// account ended up signed in. Safe to await directly from a UI event handler — the
    /// continuation resumes on the calling context.
    /// </summary>
    /// <remarks>
    /// <c>claude auth login</c> exits on its own when the sign-in completes, which closes
    /// its console window, so most of the time this just observes that and reports the
    /// result. The credentials poll covers the case where the process outlives the
    /// tokens it wrote — a lingering OAuth callback listener, or a "press any key" —
    /// where the window would otherwise sit there looking like the login hadn't finished.
    ///
    /// Polling rather than a FileSystemWatcher: it wants to observe two things at once
    /// (the file appearing and the process exiting), a browser login is human-paced so
    /// one-second granularity is ample, and there's no watcher lifetime to get wrong.
    /// </remarks>
    public static async Task<bool> RunLoginAsync(string accountId, CancellationToken ct = default)
    {
        // Baseline the credentials file BEFORE launching. On the re-authenticate path the
        // account is already signed in, so "IsLoggedIn is true" says nothing about this
        // login having finished — without the timestamp we'd start the grace timer on the
        // first poll and kill the process a couple of seconds in, while the user was still
        // in the browser. Completion means a credentials file that is valid *and* newer
        // than the one we started with.
        var writtenBefore = CredentialsWrittenAtUtc(accountId);

        var process = LaunchLogin(accountId);
        if (process == null)
            return false;

        using (process)
        {
            DateTime? credentialsSeenAt = null;

            while (!process.HasExited)
            {
                if (IsLoggedIn(accountId) && CredentialsWrittenAtUtc(accountId) > writtenBefore)
                {
                    credentialsSeenAt ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - credentialsSeenAt > LingerGrace)
                    {
                        Log.Info(
                            $"AccountManager: login id={accountId} wrote credentials but is still " +
                            "running — closing its window");

                        // entireProcessTree: the cmd fallback runs claude as a child, and
                        // killing only the shell would orphan it.
                        try { process.Kill(entireProcessTree: true); } catch { }
                        break;
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    // Caller gave up waiting (dialog closed). Leave the login running —
                    // the user may still be mid-flow in the browser.
                    return IsLoggedIn(accountId);
                }
            }
        }

        var signedIn = IsLoggedIn(accountId);
        Log.Info($"AccountManager: login finished for id={accountId} — signedIn={signedIn}");
        return signedIn;
    }

    /// <summary>
    /// Fallback for a <c>claude</c> that isn't directly executable (a batch shim).
    /// Uses <c>/c</c> so the window closes when the login finishes — the old <c>/k</c>
    /// left a stray command prompt behind every time — with <c>pause</c> holding it
    /// open only when the login actually failed and there's an error worth reading.
    /// </summary>
    private static Process? LaunchLoginViaShell(List<string> args, string dir, string accountId)
    {
        var quoted = new List<string>();
        foreach (var a in args)
            quoted.Add(a.Contains(' ') ? $"\"{a}\"" : a);

        // /s + outer quotes makes cmd treat everything between them verbatim.
        var inner = $"\"{ClaudeSession.ClaudeBinaryPath}\" {string.Join(" ", quoted)} || pause";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/s /c \"{inner}\"",
            UseShellExecute = false,
            CreateNoWindow = false
        };
        psi.Environment["CLAUDE_CONFIG_DIR"] = dir;

        try
        {
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"AccountManager: login launch failed for id={accountId} — {ex.Message}");
            return null;
        }
    }
}
