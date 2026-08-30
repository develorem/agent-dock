using System.IO;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services.Remote;

/// <summary>
/// Serves directory listings, file bodies and diffs for one project to a connected client.
///
/// Every path arriving from the client is untrusted network input, so two guards are
/// unconditional and applied in one place rather than at each call site:
/// <list type="number">
/// <item><b>Containment</b> — the resolved full path must sit inside the project root. Without
/// this, <c>../../../../Users/&lt;name&gt;/.ssh/id_rsa</c> is a valid request.</item>
/// <item><b>Size cap</b> — a client cannot make the server allocate an arbitrarily large frame
/// by asking for a multi-gigabyte file.</item>
/// </list>
/// Listings reuse the server's own <see cref="GitIgnoreFilter"/>, so the remote tree hides
/// exactly what the local tree hides.
/// </summary>
public sealed class RemoteFileAccess(
    string projectRoot,
    ILogService log,
    IPerfDiagnostics perf)
{
    private readonly string _root = Path.GetFullPath(projectRoot);
    private readonly GitIgnoreFilter _ignoreFilter = new(projectRoot);

    /// <summary>Text files above this are served truncated rather than refused.</summary>
    private const int TextPreviewCap = 2 * 1024 * 1024;

    private static readonly string[] BinaryExtensions =
    [
        ".exe", ".dll", ".pdb", ".zip", ".gz", ".7z", ".rar", ".pfx", ".so", ".dylib",
        ".pdf", ".ico", ".bin", ".dat", ".obj", ".lib", ".nupkg", ".ttf", ".otf", ".woff",
    ];

    private static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg",
    ];

    /// <summary>
    /// Resolves a client-supplied relative path against the project root, or returns null if it
    /// escapes. Comparison is done on the canonical full paths with a trailing separator, so
    /// <c>C:\proj-evil</c> cannot pass as being inside <c>C:\proj</c>.
    /// </summary>
    private string? Resolve(string relativePath)
    {
        try
        {
            if (Path.IsPathRooted(relativePath))
            {
                // A rooted path is only acceptable if it is already inside the root — the
                // client should be sending relative paths, but chat file-links carry absolute
                // server paths, so accept and re-verify rather than reject outright.
                var rootedFull = Path.GetFullPath(relativePath);
                return IsInsideRoot(rootedFull) ? rootedFull : null;
            }

            var combined = Path.GetFullPath(Path.Combine(_root, relativePath));
            return IsInsideRoot(combined) ? combined : null;
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteFileAccess: rejected path '{relativePath}' — {ex.Message}");
            return null;
        }
    }

    private bool IsInsideRoot(string fullPath)
    {
        if (string.Equals(fullPath, _root, StringComparison.OrdinalIgnoreCase)) return true;

        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private string ToRelative(string fullPath)
        => Path.GetRelativePath(_root, fullPath).Replace('\\', '/');

    // ------------------------------------------------------------------ listings

    public DirectoryListingMsg ListDirectory(string projectId, string requestId, string relativePath)
    {
        var full = Resolve(relativePath);
        if (full == null)
            return new DirectoryListingMsg(projectId, requestId, relativePath, [], "Path is outside the project");

        if (!Directory.Exists(full))
            return new DirectoryListingMsg(projectId, requestId, relativePath, [], "Directory not found");

        try
        {
            var entries = new List<RemoteFileEntry>();

            foreach (var dir in Directory.GetDirectories(full).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (_ignoreFilter.IsIgnored(ToRelative(dir), isDirectory: true)) continue;
                entries.Add(new RemoteFileEntry(Path.GetFileName(dir), ToRelative(dir), IsDirectory: true));
            }

            foreach (var file in Directory.GetFiles(full).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (_ignoreFilter.IsIgnored(ToRelative(file), isDirectory: false)) continue;
                entries.Add(new RemoteFileEntry(Path.GetFileName(file), ToRelative(file), IsDirectory: false));
            }

            return new DirectoryListingMsg(projectId, requestId, relativePath, entries, null);
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteFileAccess: listing failed for '{relativePath}' — {ex.Message}");
            return new DirectoryListingMsg(projectId, requestId, relativePath, [], ex.Message);
        }
    }

    // ------------------------------------------------------------------ content

    /// <summary>
    /// An mtime+size token. Cheap to compute and sufficient to tell a client whether its cached
    /// copy is still the same bytes, which is all the cache key needs.
    /// </summary>
    public static string VersionOf(FileInfo info)
        => $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}";

    public string? VersionOfPath(string relativePath)
    {
        var full = Resolve(relativePath);
        if (full == null || !File.Exists(full)) return null;
        return VersionOf(new FileInfo(full));
    }

    public FileContentMsg ReadFile(
        string projectId,
        string requestId,
        string relativePath,
        string? knownVersion)
    {
        var full = Resolve(relativePath);
        if (full == null)
            return Failure(projectId, requestId, relativePath, "Path is outside the project");

        if (!File.Exists(full))
            return Failure(projectId, requestId, relativePath, "File not found");

        try
        {
            var info = new FileInfo(full);
            var version = VersionOf(info);

            // The client already holds these exact bytes — answer with the version alone and
            // let it serve from its own cache. This is what stops the re-querying.
            if (knownVersion != null && knownVersion == version)
                return new FileContentMsg(projectId, requestId, relativePath, version,
                    Text: null, Bytes: null, IsBinary: false, Truncated: false, Error: null);

            if (info.Length > RemoteProtocol.MaxFileBytes)
                return Failure(projectId, requestId, relativePath,
                    $"File is too large to preview remotely ({info.Length / (1024 * 1024)} MB)");

            var extension = Path.GetExtension(full).ToLowerInvariant();

            if (ImageExtensions.Contains(extension))
            {
                var bytes = File.ReadAllBytes(full);
                return new FileContentMsg(projectId, requestId, relativePath, version,
                    Text: null, Bytes: bytes, IsBinary: false, Truncated: false, Error: null);
            }

            if (BinaryExtensions.Contains(extension) || LooksBinary(full))
                return new FileContentMsg(projectId, requestId, relativePath, version,
                    Text: null, Bytes: null, IsBinary: true, Truncated: false, Error: null);

            var truncated = info.Length > TextPreviewCap;
            var text = truncated
                ? ReadTruncated(full, TextPreviewCap)
                : File.ReadAllText(full);

            return new FileContentMsg(projectId, requestId, relativePath, version,
                text, Bytes: null, IsBinary: false, truncated, Error: null);
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteFileAccess: read failed for '{relativePath}' — {ex.Message}");
            return Failure(projectId, requestId, relativePath, ex.Message);
        }
    }

    public FileContentMsg ReadDiff(
        string projectId,
        string requestId,
        string relativePath,
        bool staged)
    {
        var full = Resolve(relativePath);
        if (full == null)
            return Failure(projectId, requestId, relativePath, "Path is outside the project");

        try
        {
            var git = new GitService(_root, perf);
            var diff = git.GetDiff(ToRelative(full), staged);

            return new FileContentMsg(projectId, requestId, relativePath,
                Version: DateTime.UtcNow.Ticks.ToString("x"),
                Text: diff ?? "",
                Bytes: null,
                IsBinary: false,
                Truncated: false,
                Error: null);
        }
        catch (Exception ex)
        {
            log.Warn($"RemoteFileAccess: diff failed for '{relativePath}' — {ex.Message}");
            return Failure(projectId, requestId, relativePath, ex.Message);
        }
    }

    private static FileContentMsg Failure(string projectId, string requestId, string path, string error)
        => new(projectId, requestId, path, Version: "", Text: null, Bytes: null,
            IsBinary: false, Truncated: false, Error: error);

    private static string ReadTruncated(string path, int cap)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[cap];
        var read = reader.Read(buffer, 0, cap);
        return new string(buffer, 0, read);
    }

    /// <summary>
    /// Sniffs for a NUL byte in the first block — the same cheap heuristic the local preview
    /// uses. Better than an extension allow-list, which never covers everything a repo holds.
    /// </summary>
    private static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(8192, (int)Math.Min(stream.Length, 8192))];
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
                if (buffer[i] == 0) return true;
        }
        catch
        {
            // Unreadable is not the same as binary, but neither is previewable.
        }

        return false;
    }
}
