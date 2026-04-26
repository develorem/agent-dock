using System.Collections.Generic;

namespace AgentDock.Models;

public class TodoItem
{
    public string Text { get; set; } = "";
    public bool IsCompleted { get; set; }
}

/// <summary>
/// Per-project settings stored in .agentdock/settings.json within the project folder.
/// </summary>
public class ProjectSettings
{
    /// <summary>
    /// Optional display name override for the project. When null or empty, the folder
    /// name is used as the display name in tabs, panel titles, and the window title.
    /// </summary>
    public string? Name { get; set; }

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

    /// <summary>
    /// Per-project todo list items.
    /// Null means no items have been created yet.
    /// </summary>
    public List<TodoItem>? TodoItems { get; set; }

    /// <summary>
    /// Play the Windows "device connect" sound when a session starts.
    /// Defaults to true so existing users keep current behaviour.
    /// </summary>
    public bool SoundOnSessionStart { get; set; } = true;

    /// <summary>
    /// Play the Windows "message nudge" sound when the agent finishes a turn
    /// and is waiting for the user's input.
    /// </summary>
    public bool SoundOnAgentWaiting { get; set; } = true;

    /// <summary>
    /// Play the Windows "device disconnect" sound when a session ends.
    /// </summary>
    public bool SoundOnSessionEnd { get; set; } = true;
}
