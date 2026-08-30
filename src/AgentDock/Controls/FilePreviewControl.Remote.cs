using System.IO;
using System.Windows;
using AgentDock.Services.Remote;

namespace AgentDock.Controls;

/// <summary>
/// The preview panel's remote-session surface.
///
/// Content arrives from the server and is cached under (path, version), where version is an
/// mtime+size token. That cache is what stops the panel re-fetching the same bytes every time a
/// file is clicked — but it is also why invalidation matters more here than in most caches: the
/// agent is continuously rewriting the repository, so a remote user reading a file Claude edited
/// thirty seconds ago, with nothing to indicate it, is a genuine way to make a wrong decision.
/// Invalidations ride the same watcher that drives git status.
/// </summary>
public partial class FilePreviewControl
{
    private IRemoteFileChannel? _remoteChannel;
    private string? _remoteProjectId;
    private string? _remoteDisplayRoot;

    /// <summary>Cached bodies, keyed by relative path.</summary>
    private readonly Dictionary<string, CachedContent> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which request the panel is currently waiting for, so stale replies are ignored.</summary>
    private string? _awaitingRequestId;

    /// <summary>Paths the server has told us are stale, so the next view re-fetches.</summary>
    private readonly HashSet<string> _stalePaths = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedContent(string Version, string? Text, byte[]? Bytes, bool IsBinary, bool Truncated);

    public bool IsRemote => _remoteChannel != null;

    public void EnterRemoteMode(IRemoteFileChannel channel, string projectId, string displayRoot)
    {
        _remoteChannel = channel;
        _remoteProjectId = projectId;
        _remoteDisplayRoot = displayRoot;

        // Revealing in Explorer would open a folder on the other machine's disk. It is a host
        // capability this host does not have, so the affordance is withdrawn rather than left to fail.
        RevealInExplorerButton.Visibility = Visibility.Collapsed;
    }

    private string ToRelative(string path)
    {
        if (string.IsNullOrEmpty(_remoteDisplayRoot)) return path.Replace('\\', '/');

        if (path.StartsWith(_remoteDisplayRoot, StringComparison.OrdinalIgnoreCase))
            return path[_remoteDisplayRoot.Length..].TrimStart('\\', '/').Replace('\\', '/');

        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Requests a file from the server, serving the cached copy immediately when it is still
    /// current. A cache hit sends the known version along so the server can answer "unchanged"
    /// with a single small frame instead of the whole body.
    /// </summary>
    public bool TryShowRemoteFile(string path)
    {
        if (_remoteChannel == null || _remoteProjectId == null) return false;

        var relative = ToRelative(path);
        var isStale = _stalePaths.Contains(relative);

        if (!isStale && _cache.TryGetValue(relative, out var cached))
        {
            RenderCached(relative, cached);
            return true;
        }

        HideAll();
        ClosePreviewButton.Visibility = Visibility.Visible;
        ShowNoPreview("Loading…");

        _awaitingRequestId = Guid.NewGuid().ToString();
        _cache.TryGetValue(relative, out var existing);
        _remoteChannel.ReadFile(_remoteProjectId, _awaitingRequestId, relative, existing?.Version);
        return true;
    }

    /// <summary>Requests a diff from the server. Diffs are never cached — they change constantly.</summary>
    public bool TryShowRemoteDiff(string path, bool staged)
    {
        if (_remoteChannel == null || _remoteProjectId == null) return false;

        HideAll();
        ClosePreviewButton.Visibility = Visibility.Visible;
        ShowNoPreview("Loading diff…");

        _awaitingRequestId = Guid.NewGuid().ToString();
        _remoteChannel.ReadDiff(_remoteProjectId, _awaitingRequestId, ToRelative(path), staged);
        return true;
    }

    /// <summary>Applies a content frame, ignoring replies to requests the user has moved past.</summary>
    public void ApplyRemoteContent(FileContentMsg content)
    {
        if (_awaitingRequestId != null && content.RequestId != _awaitingRequestId)
            return;

        _awaitingRequestId = null;

        if (content.Error != null)
        {
            ShowNoPreview(content.Error);
            return;
        }

        var relative = content.RelativePath;
        _stalePaths.Remove(relative);

        // A body-less reply with a version means "you already have this" — serve the cached copy.
        if (content.Text == null && content.Bytes == null && !content.IsBinary &&
            _cache.TryGetValue(relative, out var cached) && cached.Version == content.Version)
        {
            RenderCached(relative, cached);
            return;
        }

        var entry = new CachedContent(
            content.Version, content.Text, content.Bytes, content.IsBinary, content.Truncated);

        _cache[relative] = entry;
        TrimCache();
        RenderCached(relative, entry);
    }

    /// <summary>
    /// Bounded by total bytes rather than entry count: one large file matters far more than fifty
    /// small ones, and an entry-count cap would happily hold a hundred megabytes.
    /// </summary>
    private void TrimCache()
    {
        const long budget = 24 * 1024 * 1024;

        long Size(CachedContent c) => (c.Text?.Length ?? 0) * 2L + (c.Bytes?.Length ?? 0);

        var total = _cache.Values.Sum(Size);
        if (total <= budget) return;

        foreach (var key in _cache.Keys.ToList())
        {
            if (total <= budget) break;
            total -= Size(_cache[key]);
            _cache.Remove(key);
        }
    }

    private void RenderCached(string relativePath, CachedContent content)
    {
        HideAll();
        ClosePreviewButton.Visibility = Visibility.Visible;

        var extension = Path.GetExtension(relativePath);

        if (content.IsBinary)
        {
            ShowNoPreview("No Preview — binary file");
            return;
        }

        if (content.Bytes is { Length: > 0 })
        {
            ShowRemoteImage(content.Bytes);
            return;
        }

        var text = content.Text ?? "";
        if (content.Truncated)
            text += "\n\n… truncated — the file is too large to preview in full.";

        ShowRemoteText(text, extension);
    }

    /// <summary>
    /// Marks paths stale. Nothing is re-fetched eagerly: the next time the user looks at one of
    /// these files it is fetched fresh, which keeps an agent that touches hundreds of files from
    /// generating hundreds of transfers nobody asked for.
    /// </summary>
    public void ApplyRemoteInvalidation(IReadOnlyList<string> relativePaths)
    {
        foreach (var path in relativePaths)
            _stalePaths.Add(path);
    }
}

/// <summary>
/// Renderers for content that arrived as bytes rather than as a file on disk. They mirror the
/// local <c>ShowText</c> / <c>ShowImage</c> paths — same highlighting, same colorizers — so a
/// remote preview looks identical to a local one rather than merely similar.
/// </summary>
public partial class FilePreviewControl
{
    private void ShowRemoteText(string text, string extension)
    {
        try
        {
            TextPreview.Text = text;
            _currentExtension = extension;

            // JSON is rendered by JsonColorizer rather than a built-in highlighter, so its colours
            // stay theme-aware — same reasoning as the local path.
            TextPreview.SyntaxHighlighting = JsonExtensions.Contains(extension)
                ? null
                : _theme.GetHighlighting(extension);

            ApplyMarkdownLinkColorizer(extension);
            ApplyJsonColorizer(extension);
            TextPreview.ScrollToHome();
            TextPreview.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowNoPreview($"Cannot preview: {ex.Message}");
        }
    }

    private void ShowRemoteImage(byte[] bytes)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            ImagePreview.Source = bitmap;
            ImageContainer.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowNoPreview($"Cannot load image: {ex.Message}");
        }
    }
}
