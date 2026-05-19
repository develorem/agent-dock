using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AgentDock.Services;

/// <summary>
/// Update release channel.
/// </summary>
/// <remarks>
/// <para><b>Stable</b> (default) only ever surfaces non-prerelease GitHub releases —
/// the <c>/releases/latest</c> endpoint excludes prereleases server-side.</para>
/// <para><b>Beta</b> opts in to prerelease tags (e.g. <c>v0.10.0-beta.1</c>) and
/// auto-updates between successive betas and to the eventual stable release.
/// Use this on machines that should track upcoming builds for testing.</para>
/// </remarks>
public enum UpdateChannel { Stable, Beta }

/// <summary>
/// Checks GitHub releases for newer versions and orchestrates the update process.
/// </summary>
public record UpdateInfo(string Version, string DownloadUrl, string ReleaseName, string Notes);

public static class UpdateCheckService
{
    private const string ApiBase = "https://api.github.com/repos/develorem/agent-dock";
    private const string LatestReleaseUrl = ApiBase + "/releases/latest";
    private const string AllReleasesUrl = ApiBase + "/releases?per_page=30";

    private const string InstallerAssetPrefix = "AgentDock-";
    private const string InstallerAssetSuffix = "-setup.exe";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "AgentDock-UpdateChecker");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>
    /// Checks GitHub releases for a newer version on the given <paramref name="channel"/>.
    /// Returns UpdateInfo if a newer version exists, null otherwise.
    /// Never throws — returns null on any error.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(UpdateChannel channel = UpdateChannel.Stable)
    {
        try
        {
            Log.Info($"UpdateCheck: checking for updates (channel={channel})");

            var current = SemVer.TryParse(App.Version);
            if (current is null)
            {
                Log.Warn($"UpdateCheck: could not parse current version '{App.Version}'");
                return null;
            }

            var candidate = channel == UpdateChannel.Beta
                ? await FindBestBetaReleaseAsync()
                : await FindLatestStableReleaseAsync();

            if (candidate is null)
                return null;

            if (candidate.Version.CompareTo(current) <= 0)
            {
                Log.Info($"UpdateCheck: current {current} >= remote {candidate.Version}, no update");
                return null;
            }

            if (string.IsNullOrEmpty(candidate.DownloadUrl))
            {
                Log.Warn("UpdateCheck: no setup.exe asset found in release");
                return null;
            }

            Log.Info($"UpdateCheck: update available — {candidate.Version} (current: {current})");
            return new UpdateInfo(
                candidate.Version.ToString(),
                candidate.DownloadUrl,
                candidate.ReleaseName,
                candidate.Notes);
        }
        catch (TaskCanceledException)
        {
            Log.Warn("UpdateCheck: request timed out");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"UpdateCheck: network error — {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("UpdateCheck: unexpected error", ex);
            return null;
        }
    }

    /// <summary>
    /// GET /releases/latest — returns the latest non-prerelease release.
    /// </summary>
    private static async Task<ReleaseCandidate?> FindLatestStableReleaseAsync()
    {
        using var response = await Http.GetAsync(LatestReleaseUrl);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"UpdateCheck: API returned {response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return ExtractCandidate(root, requireStable: true);
    }

    /// <summary>
    /// GET /releases — scans all releases (including prereleases) and returns the
    /// one with the highest SemVer.
    /// </summary>
    private static async Task<ReleaseCandidate?> FindBestBetaReleaseAsync()
    {
        using var response = await Http.GetAsync(AllReleasesUrl);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"UpdateCheck: API returned {response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return null;

        ReleaseCandidate? best = null;
        foreach (var release in root.EnumerateArray())
        {
            // Skip drafts — not visible to anyone yet
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;

            var c = ExtractCandidate(release, requireStable: false);
            if (c is null) continue;

            if (best is null || c.Version.CompareTo(best.Version) > 0)
                best = c;
        }
        return best;
    }

    /// <summary>
    /// Parses a GitHub release JSON element into a <see cref="ReleaseCandidate"/>.
    /// Returns null if the release isn't usable (missing/unparseable tag, etc.)
    /// or if <paramref name="requireStable"/> and the tag has a prerelease suffix.
    /// </summary>
    private static ReleaseCandidate? ExtractCandidate(JsonElement release, bool requireStable)
    {
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(tag))
            return null;

        var version = SemVer.TryParse(tag);
        if (version is null)
        {
            Log.Warn($"UpdateCheck: could not parse tag '{tag}'");
            return null;
        }

        if (requireStable && version.IsPrerelease)
        {
            Log.Info($"UpdateCheck: skipping pre-release {tag}");
            return null;
        }

        string? downloadUrl = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.StartsWith(InstallerAssetPrefix, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(InstallerAssetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    break;
                }
            }
        }

        var releaseName = release.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tag : tag;
        var notes = release.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

        return new ReleaseCandidate(version, downloadUrl ?? "", releaseName, notes);
    }

    private sealed record ReleaseCandidate(SemVer Version, string DownloadUrl, string ReleaseName, string Notes);

    /// <summary>
    /// Downloads the installer to a temp file with progress reporting.
    /// Returns the path to the downloaded file, or null on failure.
    /// </summary>
    public static async Task<string?> DownloadInstallerAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Log.Info($"UpdateCheck: downloading installer from {downloadUrl}");

            using var response = await Http.GetAsync(downloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var tempPath = Path.Combine(Path.GetTempPath(),
                $"AgentDock-update-{Guid.NewGuid():N}.exe");

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                    progress?.Report((double)totalRead / totalBytes);
            }

            progress?.Report(1.0);
            Log.Info($"UpdateCheck: download complete — {tempPath} ({totalRead} bytes)");
            return tempPath;
        }
        catch (OperationCanceledException)
        {
            Log.Info("UpdateCheck: download cancelled by user");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("UpdateCheck: download failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Writes a temp batch script that waits for our process to exit, runs the installer
    /// silently, launches the new app, then deletes itself. Shuts down the current app.
    /// </summary>
    public static void LaunchUpdateAndShutdown(string installerPath)
    {
        var appExePath = Environment.ProcessPath
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AgentDock.exe");

        var batchPath = Path.Combine(Path.GetTempPath(),
            $"AgentDock-update-{Guid.NewGuid():N}.bat");

        var script = $"""
            @echo off
            timeout /t 2 /nobreak >nul
            "{installerPath}" /VERYSILENT /SUPPRESSMSGBOXES
            timeout /t 1 /nobreak >nul
            start "" "{appExePath}"
            del "{installerPath}" >nul 2>&1
            del "%~f0" >nul 2>&1
            """;

        File.WriteAllText(batchPath, script);

        Log.Info($"UpdateCheck: launching update script — {batchPath}");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batchPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        System.Windows.Application.Current.Shutdown();
    }
}

/// <summary>
/// Minimal SemVer 2.0.0 parser and comparer — enough to order our release tags
/// correctly, including prerelease ordering rules (no-prerelease &gt; any prerelease,
/// numeric identifiers compared numerically, etc.). Not a full SemVer implementation
/// — build metadata after '+' is stripped, and identifier comparison only handles
/// the patterns we tag with (numeric and lowercase alphanumeric).
/// </summary>
internal sealed class SemVer(int major, int minor, int patch, string? prerelease) : IComparable<SemVer>
{
    public int Major { get; } = major;
    public int Minor { get; } = minor;
    public int Patch { get; } = patch;
    public string? Prerelease { get; } = prerelease;
    public bool IsPrerelease => Prerelease != null;

    public static SemVer? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.TrimStart('v', 'V');

        // Strip +build metadata (per SemVer it doesn't affect precedence).
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        var dash = s.IndexOf('-');
        var prerelease = dash >= 0 ? s[(dash + 1)..] : null;
        var versionPart = dash >= 0 ? s[..dash] : s;

        var parts = versionPart.Split('.');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        if (!int.TryParse(parts[2], out var patch)) return null;

        return new SemVer(major, minor, patch, prerelease);
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;

        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // SemVer rule: a version with prerelease has lower precedence than one without.
        if (Prerelease == null && other.Prerelease == null) return 0;
        if (Prerelease == null) return 1;
        if (other.Prerelease == null) return -1;

        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        var len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            var aIsNum = int.TryParse(a[i], out var an);
            var bIsNum = int.TryParse(b[i], out var bn);
            if (aIsNum && bIsNum)
            {
                c = an.CompareTo(bn);
                if (c != 0) return c;
            }
            else if (aIsNum) return -1; // numeric identifiers have lower precedence than alphanumeric
            else if (bIsNum) return 1;
            else
            {
                c = string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return c;
            }
        }
        // All compared identifiers equal — longer prerelease string has higher precedence.
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString()
        => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
