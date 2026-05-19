namespace AgentDock.Models;

/// <summary>
/// A named grouping of project tabs (a "meta tab"). When two or more groups exist,
/// the meta tab bar appears above the project tab strip and only the active group's
/// projects are visible.
/// </summary>
public class ProjectGroup
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
}
