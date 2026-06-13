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

    /// <summary>
    /// The group icon. A built-in icon name (e.g. "folder", "code"). Null means the
    /// default folder icon. Unlike projects, groups have no folder so only built-in
    /// icons are supported (no auto-discovered image files).
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Hex colour (#RRGGBB) for the glyph foreground on the group's built-in icon.
    /// Null means use the theme default.
    /// </summary>
    public string? IconColor { get; set; }
}
