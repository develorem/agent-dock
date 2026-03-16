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
    }

    private (bool Success, string Message) RunGitCommand(string arguments)
    {
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
    }
}
