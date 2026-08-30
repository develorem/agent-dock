using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AgentDock.Services.Remote;

namespace AgentDock.Controls;

/// <summary>
/// Two changes to the explorer, which turn out to be the same change.
///
/// <b>Incremental patching.</b> <c>Refresh</c> used to clear the tree, rebuild it, and then try to
/// put the expansion state back — the collect-rebuild-restore dance is the tell that a wholesale
/// rebuild destroys UI state. Because it is wired to the git watcher, the agent editing files made
/// the local tree flicker. Patching in place fixes that for the local case and is a precondition
/// for the remote one.
///
/// <b>Remote mode.</b> Children arrive from the server instead of the disk, fetched when a node is
/// expanded and then pinned, so the replica stays proportional to what the user is looking at
/// rather than to the size of the repository.
/// </summary>
public partial class FileExplorerControl
{
    /// <summary>Root-level nodes. An observable collection so the tree can be patched, not rebuilt.</summary>
    private readonly ObservableCollection<FileNode> _rootNodes = [];

    private IRemoteFileChannel? _remoteChannel;
    private string? _remoteProjectId;

    /// <summary>Outstanding directory requests, keyed by request id.</summary>
    private readonly Dictionary<string, FileNode> _pendingListings = [];

    /// <summary>Directories the client has materialized, so the server only watches those.</summary>
    private readonly HashSet<string> _pinnedDirectories = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRemote => _remoteChannel != null;

    // ------------------------------------------------------------------ incremental patching

    /// <summary>
    /// Reconciles <paramref name="target"/> to <paramref name="incoming"/> with the minimum number
    /// of collection edits, matching on path.
    ///
    /// Existing nodes are kept rather than replaced, which is the whole point: a kept node keeps
    /// its expansion state, its selection, and any children already loaded beneath it. This is the
    /// same discipline <c>GitStatusControl.SyncFileList</c> already applies to its own list.
    /// </summary>
    private void SyncChildren(ObservableCollection<FileNode> target, List<FileNode> incoming)
    {
        var byPath = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in target)
            if (node.FullPath != null)
                byPath[node.FullPath] = node;

        // Remove anything that is gone, back to front so indices stay valid.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var existing = target[i];
            var stillPresent = existing.FullPath != null &&
                               incoming.Any(n => string.Equals(n.FullPath, existing.FullPath, StringComparison.OrdinalIgnoreCase));

            if (!stillPresent)
                target.RemoveAt(i);
        }

        // Insert or move into the incoming order.
        for (var i = 0; i < incoming.Count; i++)
        {
            var wanted = incoming[i];

            if (wanted.FullPath != null && byPath.TryGetValue(wanted.FullPath, out var existing))
            {
                var currentIndex = target.IndexOf(existing);
                if (currentIndex < 0)
                {
                    target.Insert(Math.Min(i, target.Count), existing);
                }
                else if (currentIndex != i)
                {
                    target.Move(currentIndex, Math.Min(i, target.Count - 1));
                }

                // The icon can change (a folder opened, a file's extension re-mapped) without the
                // node identity changing.
                if (existing.Icon != wanted.Icon && !existing.IsExpanded)
                    existing.Icon = wanted.Icon;

                continue;
            }

            target.Insert(Math.Min(i, target.Count), wanted);
        }
    }

    /// <summary>
    /// Re-reads every directory the user has actually opened and patches it. Collapsed subtrees
    /// are left alone — they are re-read when expanded, so walking them now would be wasted work.
    /// </summary>
    public void RefreshIncremental()
    {
        if (IsRemote)
        {
            // The server pushes invalidations; asking it to re-list everything on a local timer
            // would be exactly the polling the replica model exists to avoid.
            RequestListing(null, _rootNodes);
            return;
        }

        if (string.IsNullOrEmpty(_rootPath)) return;

        SyncChildren(_rootNodes, BuildLocalChildren(_rootPath));

        foreach (var node in _rootNodes.ToList())
            RefreshExpandedRecursive(node);
    }

    private void RefreshExpandedRecursive(FileNode node)
    {
        if (!node.IsDirectory || !node.IsExpanded || node.FullPath == null) return;
        if (!Directory.Exists(node.FullPath))
            return;

        SyncChildren(node.Children, BuildLocalChildren(node.FullPath));

        foreach (var child in node.Children.ToList())
            RefreshExpandedRecursive(child);
    }

    /// <summary>Builds the child nodes for one directory from disk, gitignore-filtered.</summary>
    private List<FileNode> BuildLocalChildren(string directoryPath)
    {
        var result = new List<FileNode>();

        try
        {
            var info = new DirectoryInfo(directoryPath);

            foreach (var dir in info.GetDirectories()
                         .Where(d => !IsIgnored(d.FullName, isDirectory: true))
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var node = new FileNode(_theme)
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    IsDirectory = true,
                    Icon = "📁",
                };
                node.Children.Add(new FileNode(_theme) { Name = "Loading...", Icon = "" });
                result.Add(node);
            }

            foreach (var file in info.GetFiles()
                         .Where(f => !IsIgnored(f.FullName, isDirectory: false))
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new FileNode(_theme)
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    Icon = GetFileIcon(file.Extension),
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return result;
    }

    // ------------------------------------------------------------------ remote mode

    /// <summary>
    /// Switches the panel to serving its tree from a remote host. The local actions that cannot
    /// cross the wire are hidden rather than left to fail: opening a folder in Explorer or an
    /// editor would act on the wrong machine.
    /// </summary>
    public void EnterRemoteMode(IRemoteFileChannel channel, string projectId, string displayRoot)
    {
        _remoteChannel = channel;
        _remoteProjectId = projectId;
        _rootPath = displayRoot;

        FileTree.ItemsSource = _rootNodes;
        _rootNodes.Clear();

        // "Open in VS Code" / "Reveal in Explorer" are host capabilities this host does not have.
        VsCodeButton.Visibility = Visibility.Collapsed;

        RequestListing(null, _rootNodes);
    }

    /// <summary>Asks the server for a directory's children and pins it for invalidations.</summary>
    private void RequestListing(FileNode? node, ObservableCollection<FileNode> _)
    {
        if (_remoteChannel == null || _remoteProjectId == null) return;

        var relative = node == null ? "" : ToRemoteRelative(node.FullPath);
        var requestId = Guid.NewGuid().ToString();

        if (node != null) _pendingListings[requestId] = node;

        if (_pinnedDirectories.Add(relative))
            _remoteChannel.WatchDirectory(_remoteProjectId, relative, watch: true);

        _remoteChannel.ListDirectory(_remoteProjectId, requestId, relative);
    }

    private string ToRemoteRelative(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "";
        if (string.IsNullOrEmpty(_rootPath)) return fullPath;

        if (fullPath.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath[_rootPath.Length..].TrimStart('\\', '/');
            return relative.Replace('\\', '/');
        }

        return fullPath.Replace('\\', '/');
    }

    /// <summary>Applies a directory listing pushed by the server, patching in place.</summary>
    public void ApplyRemoteListing(DirectoryListingMsg listing)
    {
        if (listing.Error != null)
        {
            _pendingListings.Remove(listing.RequestId);
            return;
        }

        var incoming = new List<FileNode>(listing.Entries.Count);
        foreach (var entry in listing.Entries)
        {
            var node = new FileNode(_theme)
            {
                Name = entry.Name,
                // Remote nodes carry the server-relative path in FullPath. Nothing local ever
                // resolves it against this machine's disk — every use routes back over the wire.
                FullPath = CombineRemote(entry.RelativePath),
                IsDirectory = entry.IsDirectory,
                Icon = entry.IsDirectory ? "📁" : GetFileIcon(Path.GetExtension(entry.Name)),
            };

            if (entry.IsDirectory)
                node.Children.Add(new FileNode(_theme) { Name = "Loading...", Icon = "" });

            incoming.Add(node);
        }

        if (_pendingListings.Remove(listing.RequestId, out var parent))
            SyncChildren(parent.Children, incoming);
        else if (string.IsNullOrEmpty(listing.RelativePath))
            SyncChildren(_rootNodes, incoming);
    }

    private string CombineRemote(string relativePath)
        => string.IsNullOrEmpty(_rootPath)
            ? relativePath
            : _rootPath.TrimEnd('\\', '/') + "/" + relativePath;

    /// <summary>
    /// Server told us these paths changed. Only directories the client actually materialized are
    /// re-listed; anything collapsed is re-read when the user next opens it.
    /// </summary>
    public void ApplyRemoteInvalidation(IReadOnlyList<string> relativePaths)
    {
        if (_remoteChannel == null) return;

        var directories = relativePaths
            .Select(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : "")
            .Distinct()
            .Where(_pinnedDirectories.Contains)
            .ToList();

        foreach (var directory in directories)
        {
            var node = FindNodeByRemoteRelative(directory);
            if (directory.Length == 0)
                RequestListing(null, _rootNodes);
            else if (node != null && node.IsExpanded)
                RequestListing(node, node.Children);
        }
    }

    private FileNode? FindNodeByRemoteRelative(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<FileNode> level = _rootNodes;
        FileNode? current = null;

        foreach (var segment in segments)
        {
            current = level.FirstOrDefault(n => string.Equals(n.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (current == null) return null;
            level = current.Children;
        }

        return current;
    }

    /// <summary>Called when a node is expanded in remote mode, to fetch its children.</summary>
    private bool TryExpandRemote(FileNode node)
    {
        if (_remoteChannel == null) return false;

        RequestListing(node, node.Children);
        return true;
    }
}

/// <summary>
/// What a remote file explorer and preview need from their connection. Narrow on purpose: the
/// panels ask for paths and receive content, and know nothing about framing or reconnects.
/// </summary>
public interface IRemoteFileChannel
{
    void ListDirectory(string projectId, string requestId, string relativePath);
    void ReadFile(string projectId, string requestId, string relativePath, string? knownVersion);
    void ReadDiff(string projectId, string requestId, string relativePath, bool staged);
    void WatchDirectory(string projectId, string relativePath, bool watch);
}
