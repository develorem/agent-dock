using AgentDock.Services;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Tests for <see cref="GitService.ToWebUrl"/> — the pure mapping from a git remote
/// URL to a browsable https web URL (or null when the remote isn't a web host).
///
/// HOW TO ADD A CASE: if a real remote produces the wrong button URL (or a button
/// where there shouldn't be one), add the raw remote string + expected result here.
/// </summary>
public class RemoteWebUrlTests
{
    [Theory]
    // GitHub — https and SSH, with and without .git
    [InlineData("https://github.com/develorem/agent-dock.git", "https://github.com/develorem/agent-dock")]
    [InlineData("https://github.com/develorem/agent-dock", "https://github.com/develorem/agent-dock")]
    [InlineData("git@github.com:develorem/agent-dock.git", "https://github.com/develorem/agent-dock")]
    [InlineData("ssh://git@github.com/develorem/agent-dock.git", "https://github.com/develorem/agent-dock")]
    // GitLab / Bitbucket / Codeberg
    [InlineData("git@gitlab.com:group/sub/proj.git", "https://gitlab.com/group/sub/proj")]
    [InlineData("https://bitbucket.org/team/repo.git", "https://bitbucket.org/team/repo")]
    [InlineData("git@codeberg.org:user/repo.git", "https://codeberg.org/user/repo")]
    // Self-hosted GitHub Enterprise / GitLab (host substring heuristic) over SSH
    [InlineData("git@gitlab.mycorp.com:team/repo.git", "https://gitlab.mycorp.com/team/repo")]
    [InlineData("git@github.internal.example:team/repo.git", "https://github.internal.example/team/repo")]
    // Embedded credentials in an https URL are stripped
    [InlineData("https://user:token@github.com/o/r.git", "https://github.com/o/r")]
    // Azure DevOps — https passthrough and the distinct SSH path shape
    [InlineData("https://dev.azure.com/org/project/_git/repo", "https://dev.azure.com/org/project/_git/repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/project/repo", "https://dev.azure.com/org/project/_git/repo")]
    public void ToWebUrl_MapsRecognizedRemotes(string remote, string expected)
    {
        Assert.Equal(expected, GitService.ToWebUrl(remote));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Plain SSH git server — a host, not a web page → no button
    [InlineData("git@my-git-server.example:repos/thing.git")]
    [InlineData("ssh://git@internal-host/srv/git/thing.git")]
    // Local / file remotes
    [InlineData("/srv/git/thing.git")]
    [InlineData("file:///srv/git/thing.git")]
    public void ToWebUrl_ReturnsNullForNonWebRemotes(string? remote)
    {
        Assert.Null(GitService.ToWebUrl(remote));
    }
}
