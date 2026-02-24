using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Central registry of all available themes. To add a new theme, add an entry here
/// and create the corresponding XAML resource dictionary in the Themes folder.
/// </summary>
public static class ThemeRegistry
{
    private static readonly List<ThemeDescriptor> _themes =
    [
        new("Obsidian",   "Obsidian",   ThemeBaseVariant.Dark,  "Themes/ObsidianTheme.xaml"),
        new("Midnight",   "Midnight",   ThemeBaseVariant.Dark,  "Themes/MidnightTheme.xaml"),
        new("Ember",      "Ember",      ThemeBaseVariant.Dark,  "Themes/EmberTheme.xaml"),
        new("Frost",      "Frost",      ThemeBaseVariant.Light, "Themes/FrostTheme.xaml"),
        new("Parchment",  "Parchment",  ThemeBaseVariant.Light, "Themes/ParchmentTheme.xaml"),
        new("Sakura",     "Sakura",     ThemeBaseVariant.Light, "Themes/SakuraTheme.xaml"),
    ];

    public static IReadOnlyList<ThemeDescriptor> All => _themes;

    public static ThemeDescriptor Default => _themes[0]; // Obsidian

    public static ThemeDescriptor? FindById(string id)
        => _themes.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a theme string from settings/workspace, with backward compatibility
    /// for old "Dark"/"Light" values.
    /// </summary>
    public static ThemeDescriptor Resolve(string? themeString)
    {
        if (string.IsNullOrEmpty(themeString))
            return Default;

        var found = FindById(themeString);
        if (found != null) return found;

        // Backward compatibility
        return themeString switch
        {
            "Dark" => FindById("Obsidian")!,
            "Light" => FindById("Frost")!,
            _ => Default
        };
    }
}
