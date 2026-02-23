using System.Text.Json.Serialization;

namespace AgentDock.Models;

/// <summary>
/// Top-level serialization model for .agentdock workspace files.
/// </summary>
public class WorkspaceFile
{
    public int Version { get; set; } = 1;
    public string Theme { get; set; } = "Dark";
    public string ToolbarPosition { get; set; } = "Top";
    public string? ActiveProjectPath { get; set; }
    public List<WorkspaceProject> Projects { get; set; } = [];
}

/// <summary>
/// Per-project data stored in a workspace file.
/// </summary>
public class WorkspaceProject
{
    public string FolderPath { get; set; } = "";
    public string? DockingLayout { get; set; }
}
