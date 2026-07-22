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
    /// or null if the account isn't logged in / has no identity yet.
    /// </summary>
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

    /// <summary>True if the account's config dir holds stored credentials.</summary>
    public static bool IsLoggedIn(string accountId) =>
        File.Exists(Path.Combine(ConfigDirFor(accountId), ".credentials.json"));

    /// <summary>
    /// Opens an interactive Claude session in a console window pointed at this
    /// account's config dir, so the user can complete the normal browser OAuth
    /// login. Credentials copied by hand don't work — each dir must log in once
    /// (tokens refresh themselves afterwards).
    /// </summary>
    public static void LaunchLogin(string accountId)
    {
        var dir = ConfigDirFor(accountId);
        Directory.CreateDirectory(dir);

        // UseShellExecute=true is required to show a console window, but it
        // forbids setting ProcessStartInfo.Environment — so the config dir is
        // exported inside the spawned shell instead. /s makes cmd treat
        // everything between the outer quotes as the command verbatim.
        var binary = ClaudeSession.ClaudeBinaryPath;
        var inner =
            $"set \"CLAUDE_CONFIG_DIR={dir}\" && " +
            "echo Signing in to Claude for this account... && " +
            "echo If you are not prompted, type /login and press Enter. && " +
            "echo. && " +
            $"\"{binary}\"";

        Log.Info($"AccountManager: launching login for id={accountId} at {dir}");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/s /k \"{inner}\"",
            UseShellExecute = true
        });
    }
}
