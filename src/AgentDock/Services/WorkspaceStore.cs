using System.IO;
using System.Text.Json;
using AgentDock.Models;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>
/// Handles saving/loading .agentdock workspace files and tracking recent workspaces.
/// </summary>
public sealed class WorkspaceStore(ILogService log, IAppSettingsStore appSettings) : IWorkspaceStore
{
    private const string RecentWorkspacesKey = "RecentWorkspaces";
    private const int MaxRecentWorkspaces = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Saves a workspace to the specified file path.
    /// </summary>
    public void Save(string filePath, WorkspaceFile workspace)
    {
        var json = JsonSerializer.Serialize(workspace, JsonOptions);
        File.WriteAllText(filePath, json);
        AddRecentWorkspace(filePath);
        log.Info($"WorkspaceStore: saved workspace to '{filePath}'");
    }

    /// <summary>
    /// Loads a workspace from the specified file path.
    /// </summary>
    public WorkspaceFile? Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            log.Warn($"WorkspaceStore: file not found '{filePath}'");
            return null;
        }

        var json = File.ReadAllText(filePath);
        var workspace = JsonSerializer.Deserialize<WorkspaceFile>(json, JsonOptions);
        if (workspace != null)
            AddRecentWorkspace(filePath);

        log.Info($"WorkspaceStore: loaded workspace from '{filePath}' ({workspace?.Projects.Count ?? 0} projects)");
        return workspace;
    }

    /// <summary>
    /// Adds a workspace path to the recent list (most recent first), removing duplicates and stale entries.
    /// </summary>
    public void AddRecentWorkspace(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var recent = GetRecentWorkspaces();

        // Remove existing entry (case-insensitive) so it moves to the top
        recent.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));

        // Insert at the top
        recent.Insert(0, fullPath);

        // Trim to max
        if (recent.Count > MaxRecentWorkspaces)
            recent.RemoveRange(MaxRecentWorkspaces, recent.Count - MaxRecentWorkspaces);

        appSettings.SetStringList(RecentWorkspacesKey, recent);
    }

    /// <summary>
    /// Returns the list of recent workspace paths, with stale (non-existent) entries removed.
    /// </summary>
    public List<string> GetRecentWorkspaces()
    {
        var recent = appSettings.GetStringList(RecentWorkspacesKey);

        // Remove entries that no longer exist on disk
        var valid = recent.Where(File.Exists).ToList();

        // If we removed stale entries, persist the cleaned list
        if (valid.Count != recent.Count)
            appSettings.SetStringList(RecentWorkspacesKey, valid);

        return valid;
    }
}
