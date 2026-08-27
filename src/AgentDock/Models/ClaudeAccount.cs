namespace AgentDock.Models;

/// <summary>
/// A named Claude Code login. Each account maps to its own
/// <c>CLAUDE_CONFIG_DIR</c> (credentials, settings, history, session
/// transcripts) so several Claude accounts can run concurrently across
/// sessions in one Agent Dock instance.
/// </summary>
public class ClaudeAccount
{
    /// <summary>Stable identifier; also the folder name under the accounts root.</summary>
    public string Id { get; set; } = "";

    /// <summary>User-facing label, e.g. "Personal" or "Work".</summary>
    public string Name { get; set; } = "";
}
