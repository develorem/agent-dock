namespace AgentDock.Models;

/// <summary>
/// Represents an open project in the workspace.
/// </summary>
public class ProjectInfo
{
    public required string FolderPath { get; init; }

    public string FolderName => System.IO.Path.GetFileName(FolderPath) ?? FolderPath;
}
