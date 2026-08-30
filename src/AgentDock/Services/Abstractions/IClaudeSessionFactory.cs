namespace AgentDock.Services.Abstractions;

/// <summary>
/// Creates agent sessions for a project directory.
///
/// A factory rather than direct construction so the chat panel never news up a
/// <see cref="ClaudeSession"/> itself. This is the seam a remote project host replaces:
/// the panel asks for a session, and whether that session drives a local subprocess or an
/// RPC channel is the factory's business, not the panel's.
/// </summary>
public interface IClaudeSessionFactory
{
    ClaudeSession Create(string workingDirectory, string? accountConfigDir = null);
}
