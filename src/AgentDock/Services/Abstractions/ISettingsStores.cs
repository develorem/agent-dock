using AgentDock.Models;

namespace AgentDock.Services.Abstractions;

/// <summary>
/// Application-scoped settings (%LOCALAPPDATA%/AgentDock/settings.json).
/// Merge-style writes: each consumer owns its own keys and never clobbers others.
/// </summary>
public interface IAppSettingsStore
{
    string SettingsDir { get; }
    string SettingsFile { get; }

    string GetString(string key, string defaultValue = "");
    void SetString(string key, string value);
    List<string> GetStringList(string key);
    void SetStringList(string key, List<string> values);
}

/// <summary>
/// Project-scoped settings, persisted in <c>&lt;project&gt;/.agentdock/settings.json</c>.
///
/// Deliberately an instance service rather than a static helper: this is the seam a remote
/// project host substitutes so that todo items, the project description, the icon choice and
/// the preferred account round-trip to the machine the project actually lives on.
/// </summary>
public interface IProjectSettingsStore
{
    ProjectSettings Load(string projectFolder);
    void Save(string projectFolder, ProjectSettings settings);
    void Update(string projectFolder, Action<ProjectSettings> update);
}

/// <summary>Reads and writes <c>.agentdock</c> workspace files and the recent-workspace list.</summary>
public interface IWorkspaceStore
{
    void Save(string filePath, WorkspaceFile workspace);
    WorkspaceFile? Load(string filePath);
    void AddRecentWorkspace(string filePath);
    List<string> GetRecentWorkspaces();
}
