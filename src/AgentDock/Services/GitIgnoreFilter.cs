using System.IO;
using System.Text.RegularExpressions;

namespace AgentDock.Services;

/// <summary>
/// Parses .gitignore files and checks whether paths should be ignored.
/// </summary>
public class GitIgnoreFilter
{
    private readonly List<(Regex Pattern, bool IsNegation)> _rules = [];

    // Always ignore these regardless of .gitignore
    private static readonly HashSet<string> AlwaysIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea",
        "__pycache__", ".DS_Store", "Thumbs.db"
    };

    public GitIgnoreFilter(string projectRoot)
    {
        var gitignorePath = Path.Combine(projectRoot, ".gitignore");
        if (File.Exists(gitignorePath))
        {
            foreach (var line in File.ReadAllLines(gitignorePath))
                ParseLine(line);
        }
    }

    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var name = Path.GetFileName(relativePath);

        // Always-ignore list (common build/tool directories)
        if (isDirectory && AlwaysIgnore.Contains(name))
            return true;

        if (!isDirectory && AlwaysIgnore.Contains(name))
            return true;

        // Normalize path separators for matching
        var normalized = relativePath.Replace('\\', '/');
        if (isDirectory && !normalized.EndsWith('/'))
            normalized += "/";

        var ignored = false;
        foreach (var (pattern, isNegation) in _rules)
        {
            if (pattern.IsMatch(normalized) || pattern.IsMatch(name) ||
                (isDirectory && pattern.IsMatch(name + "/")))
            {
                ignored = !isNegation;
            }
        }

        return ignored;
    }

    private void ParseLine(string line)
    {
        var trimmed = line.Trim();

        // Skip empty lines and comments
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            return;

        var isNegation = false;
        if (trimmed.StartsWith('!'))
        {
            isNegation = true;
            trimmed = trimmed[1..];
        }

        // Convert gitignore glob pattern to regex
        var regexPattern = GlobToRegex(trimmed);
        try
        {
            var regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _rules.Add((regex, isNegation));
        }
        catch (RegexParseException)
        {
            // Skip malformed patterns
        }
    }

    private static string GlobToRegex(string glob)
    {
        var pattern = glob.TrimEnd('/');
        var result = new System.Text.StringBuilder();

        // If pattern starts with /, it's anchored to root
        var anchored = pattern.StartsWith('/');
        if (anchored)
            pattern = pattern[1..];

        if (!anchored)
            result.Append("(^|/)");
        else
            result.Append('^');

        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        // ** matches everything including /
                        result.Append(".*");
                        i++; // skip second *
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                            i++; // skip trailing /
                    }
                    else
                    {
                        // * matches everything except /
                        result.Append("[^/]*");
                    }
                    break;
                case '?':
                    result.Append("[^/]");
                    break;
                case '.':
                    result.Append("\\.");
                    break;
                case '/':
                    result.Append('/');
                    break;
                default:
                    result.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        result.Append("(/|$)");
        return result.ToString();
    }
}
