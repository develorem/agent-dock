using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentDock.Services;

/// <summary>
/// Shared read/write access to %LOCALAPPDATA%/AgentDock/settings.json.
/// Uses JsonNode for merge-style updates so multiple consumers (ThemeManager,
/// WorkspaceManager) can each write their own keys without clobbering others.
/// </summary>
public static class AppSettings
{
    public static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock");

    public static readonly string SettingsFile =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly object Lock = new();

    /// <summary>
    /// Reads a string value from settings.json, or returns defaultValue if missing.
    /// </summary>
    public static string GetString(string key, string defaultValue = "")
    {
        lock (Lock)
        {
            try
            {
                var root = LoadRoot();
                return root?[key]?.GetValue<string>() ?? defaultValue;
            }
            catch (Exception ex)
            {
                Log.Warn($"AppSettings: failed to read '{key}' — {ex.Message}");
                return defaultValue;
            }
        }
    }

    /// <summary>
    /// Writes a string value to settings.json, preserving all other keys.
    /// </summary>
    public static void SetString(string key, string value)
    {
        lock (Lock)
        {
            try
            {
                var root = LoadRoot() ?? new JsonObject();
                root[key] = value;
                SaveRoot(root);
            }
            catch (Exception ex)
            {
                Log.Warn($"AppSettings: failed to write '{key}' — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads a string list from settings.json, or returns empty list if missing.
    /// </summary>
    public static List<string> GetStringList(string key)
    {
        lock (Lock)
        {
            try
            {
                var root = LoadRoot();
                if (root?[key] is JsonArray arr)
                    return arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s != "").ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"AppSettings: failed to read list '{key}' — {ex.Message}");
            }

            return [];
        }
    }

    /// <summary>
    /// Writes a string list to settings.json, preserving all other keys.
    /// </summary>
    public static void SetStringList(string key, List<string> values)
    {
        lock (Lock)
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
                Log.Warn($"AppSettings: failed to write list '{key}' — {ex.Message}");
            }
        }
    }

    private static JsonObject? LoadRoot()
    {
        if (!File.Exists(SettingsFile))
            return null;

        var json = File.ReadAllText(SettingsFile);
        return JsonNode.Parse(json)?.AsObject();
    }

    private static void SaveRoot(JsonObject root)
    {
        Directory.CreateDirectory(SettingsDir);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsFile, root.ToJsonString(options));
    }
}
