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

    /// <summary>
    /// When true, a dynamic "Active Projects" group is shown on the right of the meta
    /// tab bar (only while grouping is in use). It gathers the projects with a live
    /// agent session, most-recently-active first. On unless a workspace file turns it
    /// off, including for legacy files that predate the setting.
    /// </summary>
    public bool ShowActiveProjectsGroup { get; set; } = true;

    /// <summary>
    /// How many projects the dynamic "Active Projects" group shows before it stops
    /// listing them. Legacy files that predate the setting get the default.
    /// </summary>
    public int ActiveProjectsLimit { get; set; } = DefaultActiveProjectsLimit;

    public const int DefaultActiveProjectsLimit = 5;
    public const int MinActiveProjectsLimit = 1;
    public const int MaxActiveProjectsLimit = 20;

    /// <summary>
    /// Holds a limit inside the supported range. Guards both workspace files edited by
    /// hand and free-text entry in the settings dialog.
    /// </summary>
    public static int ClampActiveProjectsLimit(int value) =>
        Math.Clamp(value, MinActiveProjectsLimit, MaxActiveProjectsLimit);
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
