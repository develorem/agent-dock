using System.Text.Json.Serialization;

namespace AgentDock.Models;

/// <summary>
/// Top-level serialization model for .agentdock workspace files.
/// </summary>
public class WorkspaceFile
{
    public int Version { get; set; } = 2;
    public string Theme { get; set; } = "Dark";
    public string ToolbarPosition { get; set; } = "Top";
    public string? ActiveProjectPath { get; set; }
    public List<WorkspaceProject> Projects { get; set; } = [];

    /// <summary>
    /// Project tab groups (meta tabs). Empty when the workspace has no grouping —
    /// in that case every project is implicitly ungrouped and the meta tab bar is hidden.
    /// </summary>
    public List<ProjectGroup> Groups { get; set; } = [];

    /// <summary>
    /// Id of the group whose projects are visible. Null when no grouping is in use.
    /// </summary>
    public string? ActiveGroupId { get; set; }
}

/// <summary>
/// Per-project data stored in a workspace file.
/// </summary>
public class WorkspaceProject
{
    public string FolderPath { get; set; } = "";
    public string? DockingLayout { get; set; }

    /// <summary>
    /// Id of the <see cref="ProjectGroup"/> this project belongs to, or null when no grouping is in use.
    /// </summary>
    public string? GroupId { get; set; }
}
