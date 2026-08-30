using System.Windows;
using AgentDock.Services;
using AgentDock.Services.Remote;

namespace AgentDock.Controls;

/// <summary>
/// The git panel's remote-session surface.
///
/// Server side it exposes the current status for replication. Client side it accepts a pushed
/// status and patches the bound collection through the same <c>SyncFileList</c> merge the local
/// path uses — so a remote git panel does not flicker any more than a local one does, and the
/// diff-and-patch discipline is shared rather than reimplemented.
/// </summary>
public partial class GitStatusControl
{
    /// <summary>
    /// Fired after the bound collection has been patched. This is the replication hook; it
    /// deliberately does not carry the payload, so a publisher pulls a consistent snapshot rather
    /// than trusting an event argument assembled at a different moment.
    /// </summary>
    public event Action? StatusReplicated;

    public bool IsGitRepository => _isGitRepository;

    /// <summary>Current status, as replicated to a client.</summary>
    public GitStatusSnapshot DescribeStatus() => new(
        _isGitRepository,
        _currentBranch,
        _remoteWebUrl,
        _items.Select(i => new GitFileEntrySnapshot(
                i.Entry.FilePath,
                i.Entry.Status,
                i.Entry.IsStaged))
            .ToList());

    // ------------------------------------------------------------------ client side

    private bool _isRemote;

    /// <summary>
    /// Puts the panel into remote mode. It stops running git itself — the watcher, the polling
    /// timer and <c>RefreshStatus</c> would all be operating on the wrong machine's disk.
    /// </summary>
    public void EnterRemoteMode()
    {
        _isRemote = true;

        StopWatching();
        _gitService = null;

        // Branch switching is a mutation on someone else's machine. It is not a method that
        // throws — the affordance simply is not offered.
        if (BranchName.Parent is FrameworkElement)
            BranchPanel.IsEnabled = false;
    }

    /// <summary>Applies a status pushed by the server, patching rather than rebuilding.</summary>
    public void ApplyRemoteStatus(GitStatusSnapshot status)
    {
        _isGitRepository = status.IsRepository;
        _currentBranch = status.Branch;
        _remoteWebUrl = status.RemoteWebUrl;

        if (!status.IsRepository)
        {
            NotGitMessage.Visibility = Visibility.Visible;
            StatusPanel.Visibility = Visibility.Collapsed;
            NoChangesMessage.Visibility = Visibility.Collapsed;
            BranchPanel.Visibility = Visibility.Collapsed;
            if (_items.Count > 0) _items.Clear();
            return;
        }

        if (!string.IsNullOrEmpty(status.Branch))
        {
            if (BranchName.Text != status.Branch) BranchName.Text = status.Branch;
            BranchPanel.Visibility = Visibility.Visible;
        }
        else
        {
            BranchPanel.Visibility = Visibility.Collapsed;
        }

        // The remote-open button is offered only when the server resolved a browsable remote;
        // the URL is public information and opens in the client's own browser.
        OpenRemoteButton.Visibility = string.IsNullOrEmpty(_remoteWebUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var target = status.Entries
            .Select(e => new GitFileEntry(e.FilePath, e.Status, e.IsStaged))
            .OrderBy(e => e.IsStaged ? 0 : 1)
            .ThenBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (target.Count == 0)
        {
            if (_items.Count > 0) _items.Clear();
            StatusPanel.Visibility = Visibility.Collapsed;
            NoChangesMessage.Visibility = Visibility.Visible;
            NotGitMessage.Visibility = Visibility.Collapsed;
            return;
        }

        NoChangesMessage.Visibility = Visibility.Collapsed;
        NotGitMessage.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;

        SyncFileList(target, new HashSet<GitFileEntry>(target));
    }
}
