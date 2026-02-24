namespace AgentDock.Models;

/// <summary>
/// Per-project settings stored in .agentdock/settings.json within the project folder.
/// </summary>
public class ProjectSettings
{
    /// <summary>
    /// The project icon. Can be:
    /// - A built-in icon name (e.g. "folder", "code", "python", "csharp")
    /// - A file path relative to the project root (e.g. "assets/logo.png")
    /// - An absolute file path
    /// - Null to trigger auto-discovery on next load
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Hex colour (#RRGGBB) for the glyph foreground on built-in icons.
    /// Null means use the theme default.
    /// </summary>
    public string? IconColor { get; set; }

    /// <summary>
    /// A brief description of the project (one or two sentences).
    /// Null means no description has been set.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Font size for the project description panel.
    /// Null means use the default (13).
    /// </summary>
    public double? DescriptionFontSize { get; set; }
}
