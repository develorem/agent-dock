using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>Creates sessions that drive a local <c>claude</c> subprocess.</summary>
public sealed class ClaudeSessionFactory(
    ILogService log,
    IClaudeEnvironment environment,
    IPerfDiagnostics perf) : IClaudeSessionFactory
{
    public ClaudeSession Create(string workingDirectory, string? accountConfigDir = null)
        => new(workingDirectory, log, environment, perf, accountConfigDir);
}
