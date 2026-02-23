using System.IO;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Reads and writes per-project settings from .agentdock/settings.json.
/// </summary>
public static class ProjectSettingsManager
{
    private const string SettingsFolder = ".agentdock";
    private const string SettingsFile = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Returns the path to the .agentdock/settings.json for a project.
    /// </summary>
    public static string GetSettingsPath(string projectFolder)
        => Path.Combine(projectFolder, SettingsFolder, SettingsFile);

    /// <summary>
    /// Loads project settings. Returns default settings if the file doesn't exist.
    /// </summary>
    public static ProjectSettings Load(string projectFolder)
    {
        var path = GetSettingsPath(projectFolder);

        if (!File.Exists(path))
            return new ProjectSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ProjectSettings>(json, ReadOptions)
                   ?? new ProjectSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"ProjectSettings: failed to read {path} — {ex.Message}");
            return new ProjectSettings();
        }
    }

    /// <summary>
    /// Saves project settings, creating the .agentdock folder if needed.
    /// </summary>
    public static void Save(string projectFolder, ProjectSettings settings)
    {
        var dir = Path.Combine(projectFolder, SettingsFolder);
        var path = Path.Combine(dir, SettingsFile);

        try
        {
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"ProjectSettings: failed to write {path} — {ex.Message}");
        }
    }

    /// <summary>
    /// Updates a single setting without overwriting others.
    /// Loads existing settings, applies the update, and saves.
    /// </summary>
    public static void Update(string projectFolder, Action<ProjectSettings> update)
    {
        var settings = Load(projectFolder);
        update(settings);
        Save(projectFolder, settings);
    }
}
