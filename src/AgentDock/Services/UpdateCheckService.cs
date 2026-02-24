using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AgentDock.Services;

/// <summary>
/// Checks GitHub releases for newer versions and orchestrates the update process.
/// </summary>
public record UpdateInfo(string Version, string DownloadUrl, string ReleaseName);

public static class UpdateCheckService
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/develorem/agent-dock/releases/latest";

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
    /// Checks GitHub releases for a newer version.
    /// Returns UpdateInfo if a newer version exists, null otherwise.
    /// Never throws — returns null on any error.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            Log.Info("UpdateCheck: checking for updates");

            using var response = await Http.GetAsync(ReleasesApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"UpdateCheck: API returned {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tagName))
                return null;

            var remoteVersionStr = tagName.TrimStart('v');

            // Skip pre-releases (the /latest endpoint excludes them, but belt-and-suspenders)
            if (remoteVersionStr.Contains('-'))
            {
                Log.Info($"UpdateCheck: skipping pre-release {tagName}");
                return null;
            }

            if (!Version.TryParse(remoteVersionStr, out var remoteVersion))
            {
                Log.Warn($"UpdateCheck: could not parse version '{remoteVersionStr}'");
                return null;
            }

            var currentVersionStr = App.Version.Split(['-', '+'])[0];
            if (!Version.TryParse(currentVersionStr, out var currentVersion))
            {
                Log.Warn($"UpdateCheck: could not parse current version '{App.Version}'");
                return null;
            }

            if (remoteVersion <= currentVersion)
            {
                Log.Info($"UpdateCheck: current {currentVersion} >= remote {remoteVersion}, no update");
                return null;
            }

            // Find the setup.exe asset
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.StartsWith(InstallerAssetPrefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(InstallerAssetSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                Log.Warn("UpdateCheck: no setup.exe asset found in release");
                return null;
            }

            var releaseName = root.GetProperty("name").GetString() ?? tagName;

            Log.Info($"UpdateCheck: update available — {remoteVersionStr} (current: {App.Version})");
            return new UpdateInfo(remoteVersionStr, downloadUrl, releaseName);
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
