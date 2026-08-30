using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>
/// Shared read/write access to %LOCALAPPDATA%/AgentDock/settings.json.
/// Uses JsonNode for merge-style updates so multiple consumers (ThemeManager,
/// WorkspaceManager) can each write their own keys without clobbering others.
/// </summary>
public sealed class AppSettingsStore(ILogService log) : IAppSettingsStore
{
    public string SettingsDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock");

    public string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    private readonly object _lock = new();

    /// <summary>
    /// Reads a string value from settings.json, or returns defaultValue if missing.
    /// </summary>
    public string GetString(string key, string defaultValue = "")
    {
        lock (_lock)
        {
            try
            {
                var root = LoadRoot();
                return root?[key]?.GetValue<string>() ?? defaultValue;
            }
            catch (Exception ex)
            {
                log.Warn($"AppSettingsStore: failed to read '{key}' — {ex.Message}");
                return defaultValue;
            }
        }
    }

    /// <summary>
    /// Writes a string value to settings.json, preserving all other keys.
    /// </summary>
    public void SetString(string key, string value)
    {
        lock (_lock)
        {
            try
            {
                var root = LoadRoot() ?? new JsonObject();
                root[key] = value;
                SaveRoot(root);
            }
            catch (Exception ex)
            {
                log.Warn($"AppSettingsStore: failed to write '{key}' — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads a string list from settings.json, or returns empty list if missing.
    /// </summary>
    public List<string> GetStringList(string key)
    {
        lock (_lock)
        {
            try
            {
                var root = LoadRoot();
                if (root?[key] is JsonArray arr)
                    return arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s != "").ToList();
            }
            catch (Exception ex)
            {
                log.Warn($"AppSettingsStore: failed to read list '{key}' — {ex.Message}");
            }

            return [];
        }
    }

    /// <summary>
    /// Writes a string list to settings.json, preserving all other keys.
    /// </summary>
    public void SetStringList(string key, List<string> values)
    {
        lock (_lock)
        {
            try
            {
                var root = LoadRoot() ?? new JsonObject();
                var arr = new JsonArray();
                foreach (var v in values)
                    arr.Add(v);
                root[key] = arr;
                SaveRoot(root);
            }
            catch (Exception ex)
            {
                log.Warn($"AppSettingsStore: failed to write list '{key}' — {ex.Message}");
            }
        }
    }

    private JsonObject? LoadRoot()
    {
        if (!File.Exists(SettingsFile))
            return null;

        var json = File.ReadAllText(SettingsFile);
        return JsonNode.Parse(json)?.AsObject();
    }

    private void SaveRoot(JsonObject root)
    {
        Directory.CreateDirectory(SettingsDir);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsFile, root.ToJsonString(options));
    }
}
