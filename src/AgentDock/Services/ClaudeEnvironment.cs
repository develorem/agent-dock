using System.Diagnostics;
using System.IO;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>
/// Locates the Claude Code CLI and reports whether it is usable.
///
/// This was previously static mutable state on <c>ClaudeSession</c>
/// (<c>ClaudeSession.ClaudeBinaryPath</c>). A single global path is wrong as soon as one
/// process hosts more than one project host — a remote host's agent lives on another
/// machine entirely and has no local binary path at all.
/// </summary>
public sealed class ClaudeEnvironment(ILogService log) : IClaudeEnvironment
{
    /// <summary>
    /// Path to the claude binary. Defaults to "claude" (found via PATH).
    /// Set via Settings &gt; Claude Path Override and persisted in app settings.
    /// </summary>
    public string BinaryPath { get; set; } = "claude";

    public bool IsAvailable()
    {
        try
        {
            // Use cmd.exe /c to resolve .cmd/.bat wrappers (npm-installed CLIs on Windows)
            // This matches the prerequisite check behaviour.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {BinaryPath} --version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the Claude binary path to a full path that Process.Start can use directly.
    /// Searches PATH for .cmd/.bat/.exe variants if <see cref="BinaryPath"/> is just a name
    /// like "claude".
    /// </summary>
    public string ResolveBinaryPath()
    {
        var path = BinaryPath;

        // If it's already a rooted path that exists, use it directly
        if (Path.IsPathRooted(path) && File.Exists(path))
            return path;

        // Search PATH for the command. Prefer .cmd/.bat first because Claude Code CLI
        // on Windows is an npm .cmd wrapper, and we must avoid picking up claude.exe from
        // the Claude Desktop app (which doesn't support the JSON-lines protocol).
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = new[] { ".cmd", ".bat", "", ".exe" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, path + ext);
                if (File.Exists(candidate))
                {
                    log.Info($"ClaudeEnvironment: resolved '{path}' to '{candidate}'");
                    return candidate;
                }
            }
        }

        // Fallback: return as-is and let Process.Start try
        return path;
    }
}
