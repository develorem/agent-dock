using System.Diagnostics;
using System.IO;

namespace AgentDock.Services;

public enum GitFileStatus
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked
}

public record GitFileEntry(string FilePath, GitFileStatus Status, bool IsStaged);

public class GitService
{
    private readonly string _workingDirectory;

    public GitService(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public bool IsGitRepository()
    {
        var gitDir = Path.Combine(_workingDirectory, ".git");
        return Directory.Exists(gitDir) || File.Exists(gitDir); // file for worktrees
    }

    public string? GetCurrentBranch()
    {
        return RunGit("rev-parse --abbrev-ref HEAD")?.Trim();
    }

    public List<GitFileEntry> GetStatus()
    {
        var entries = new List<GitFileEntry>();

        var output = RunGit("status --porcelain=v1");
        if (output == null)
            return entries;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3)
                continue;

            var indexStatus = line[0];
            var workTreeStatus = line[1];
            var filePath = line[3..].Trim();

            // Handle renames: "R  old -> new"
            if (filePath.Contains(" -> "))
                filePath = filePath.Split(" -> ")[^1];

            // Staged changes (index column)
            if (indexStatus != ' ' && indexStatus != '?')
            {
                entries.Add(new GitFileEntry(filePath, ParseStatus(indexStatus), IsStaged: true));
            }

            // Unstaged changes (work tree column)
            if (workTreeStatus != ' ')
            {
                var status = workTreeStatus == '?' ? GitFileStatus.Untracked : ParseStatus(workTreeStatus);
                entries.Add(new GitFileEntry(filePath, status, IsStaged: false));
            }
        }

        return entries;
    }

    public string? GetDiff(string filePath, bool staged)
    {
        var args = staged
            ? $"diff --cached -- \"{filePath}\""
            : $"diff -- \"{filePath}\"";

        var result = RunGit(args);

        // For untracked files, show the file contents as "new file"
        if (string.IsNullOrEmpty(result))
        {
            var fullPath = Path.Combine(_workingDirectory, filePath);
            if (File.Exists(fullPath))
            {
                try
                {
                    var content = File.ReadAllText(fullPath);
                    return $"--- /dev/null\n+++ b/{filePath}\n" +
                           string.Join("\n", content.Split('\n').Select(l => $"+{l}"));
                }
                catch
                {
                    return null;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a browsable web URL for the <c>origin</c> remote, or null if there is
    /// no origin or it isn't a recognizable web host (e.g. a plain SSH git server).
    /// </summary>
    public string? GetRemoteWebUrl()
    {
        var url = RunGit("remote get-url origin")?.Trim();
        return ToWebUrl(url);
    }

    /// <summary>
    /// Converts a git remote URL into a browsable https web URL, or returns null when
    /// the remote can't be mapped to a web page. Pure/testable — no process launch.
    ///
    /// Rules:
    /// - http(s) remotes are already web URLs → returned cleaned (credentials and a
    ///   trailing <c>.git</c> stripped). Covers self-hosted GitHub/GitLab over https.
    /// - SSH remotes (scp-style <c>git@host:path</c> or <c>ssh://…</c>) are converted
    ///   only for recognized web hosts, since an arbitrary SSH git server isn't a web
    ///   server. Azure DevOps uses a distinct web-path shape and is special-cased.
    /// </summary>
    public static string? ToWebUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        var url = remoteUrl.Trim();

        // http(s): already a web URL. Strip embedded credentials + trailing ".git".
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                return null;

            var httpPath = StripDotGit(uri.AbsolutePath).TrimEnd('/');
            if (string.IsNullOrEmpty(httpPath))
                return null;

            return $"https://{uri.Host}{httpPath}";
        }

        // Otherwise SSH — extract (host, path).
        string? host;
        string? path;
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            // scp-like: [user@]host:path
            var afterUser = url.Contains('@') ? url[(url.IndexOf('@') + 1)..] : url;
            var colon = afterUser.IndexOf(':');
            if (colon <= 0)
                return null;
            host = afterUser[..colon];
            path = afterUser[(colon + 1)..];
        }

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(path))
            return null;

        path = StripDotGit(path.Trim('/'));
        if (string.IsNullOrEmpty(path))
            return null;

        // Azure DevOps SSH: v3/{org}/{project}/{repo} -> dev.azure.com/{org}/{project}/_git/{repo}
        if (host.Equals("ssh.dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("vs-ssh.visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts[0].Equals("v3", StringComparison.OrdinalIgnoreCase))
                return $"https://dev.azure.com/{parts[1]}/{parts[2]}/_git/{parts[3]}";
            return null;
        }

        return IsKnownWebHost(host) ? $"https://{host}/{path}" : null;
    }

    private static string StripDotGit(string s)
        => s.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    private static bool IsKnownWebHost(string host)
    {
        host = host.ToLowerInvariant();
        string[] known = ["github.com", "gitlab.com", "bitbucket.org", "dev.azure.com", "codeberg.org"];
        return known.Contains(host)
            || host.Contains("github")
            || host.Contains("gitlab")
            || host.Contains("bitbucket");
    }

    public List<string> GetLocalBranches()
    {
        var output = RunGit("branch --format=%(refname:short)");
        if (string.IsNullOrEmpty(output))
            return [];

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(b => b.Trim())
                     .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    public (bool Success, string Message) CheckoutBranch(string branchName)
    {
        return RunGitCommand($"checkout \"{branchName}\"");
    }

    public (bool Success, string Message) CreateAndCheckoutBranch(string branchName)
    {
        return RunGitCommand($"checkout -b \"{branchName}\"");
    }

    private static GitFileStatus ParseStatus(char c) => c switch
    {
        'M' => GitFileStatus.Modified,
        'A' => GitFileStatus.Added,
        'D' => GitFileStatus.Deleted,
        'R' => GitFileStatus.Renamed,
        '?' => GitFileStatus.Untracked,
        _ => GitFileStatus.Modified
    };

    private string? RunGit(string arguments)
    {
        // Instrumentation: these calls are synchronous (Process + WaitForExit) and
        // today run on the UI thread via GitStatusControl.RefreshStatus, so a slow
        // git is a direct UI stall. PerfDiagnostics logs the duration + thread.
        var start = Stopwatch.GetTimestamp();
        PerfDiagnostics.GitOpStart();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return output;
        }
        catch
        {
            return null;
        }
        finally
        {
            PerfDiagnostics.GitOpEnd(arguments, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                System.Threading.Thread.CurrentThread.ManagedThreadId);
        }
    }

    private (bool Success, string Message) RunGitCommand(string arguments)
    {
        var start = Stopwatch.GetTimestamp();
        PerfDiagnostics.GitOpStart();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "Failed to start git process");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            var message = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            return (process.ExitCode == 0, message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            PerfDiagnostics.GitOpEnd(arguments, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                System.Threading.Thread.CurrentThread.ManagedThreadId);
        }
    }
}
