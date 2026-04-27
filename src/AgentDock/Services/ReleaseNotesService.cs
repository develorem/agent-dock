using System.IO;
using System.Reflection;

namespace AgentDock.Services;

/// <summary>
/// Reads release-notes markdown bundled with the assembly as embedded resources.
/// Files in <c>docs/release-notes/v*.md</c> are embedded with logical name
/// <c>ReleaseNotes/v{X.Y.Z}.md</c>.
/// </summary>
public static class ReleaseNotesService
{
    /// <summary>
    /// Loads the markdown body for the given version (e.g. "0.9.0").
    /// Returns null if the resource is not embedded.
    /// </summary>
    public static string? GetNotesForVersion(string version)
    {
        var resourceName = $"ReleaseNotes/v{version}.md";
        try
        {
            using var stream = typeof(ReleaseNotesService).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Log.Info($"ReleaseNotes: no embedded notes for v{version}");
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warn($"ReleaseNotes: failed to read v{version} — {ex.Message}");
            return null;
        }
    }
}
