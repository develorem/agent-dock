namespace AgentDock.Models;

/// <summary>
/// Represents an open project in the workspace.
/// </summary>
public class ProjectInfo
{
    public required string FolderPath { get; init; }

    public string FolderName => System.IO.Path.GetFileName(FolderPath) ?? FolderPath;

    /// <summary>
    /// Optional user-chosen display name from <see cref="ProjectSettings.Name"/>.
    /// When set, takes precedence over <see cref="FolderName"/> for all UI display.
    /// Updated in-memory when the user edits the project settings.
    /// </summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// The name shown in tabs, panel titles, and the window title.
    /// Falls back to the folder name when no custom name is set.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(CustomName) ? FolderName : CustomName!;
}
