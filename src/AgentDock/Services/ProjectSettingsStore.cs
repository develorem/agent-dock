using System.IO;
using System.Text.Json;
using AgentDock.Models;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>
/// Reads and writes per-project settings from .agentdock/settings.json on the local disk.
/// The <see cref="IProjectSettingsStore"/> seam lets a remote project host serve the same
/// state from the machine the project actually lives on.
/// </summary>
public sealed class ProjectSettingsStore(ILogService log) : IProjectSettingsStore
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
    /// Returns the path to the .agentdock/settings.json for a project. Internal: callers go
    /// through Load/Save/Update so a remote implementation never has to expose a local path.
    /// </summary>
    private static string GetSettingsPath(string projectFolder)
        => Path.Combine(projectFolder, SettingsFolder, SettingsFile);

    /// <summary>
    /// Loads project settings. Returns default settings if the file doesn't exist.
    /// </summary>
    public ProjectSettings Load(string projectFolder)
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
            log.Warn($"ProjectSettings: failed to read {path} — {ex.Message}");
            return new ProjectSettings();
        }
    }

    /// <summary>
    /// Saves project settings, creating the .agentdock folder if needed.
    /// </summary>
    public void Save(string projectFolder, ProjectSettings settings)
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
            log.Warn($"ProjectSettings: failed to write {path} — {ex.Message}");
        }
    }

    /// <summary>
    /// Updates a single setting without overwriting others.
    /// Loads existing settings, applies the update, and saves.
    /// </summary>
    public void Update(string projectFolder, Action<ProjectSettings> update)
    {
        var settings = Load(projectFolder);
        update(settings);
        Save(projectFolder, settings);
    }
}
