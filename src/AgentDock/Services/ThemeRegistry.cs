using AgentDock.Models;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>
/// Central registry of all available themes. To add a new theme, add an entry here
/// and create the corresponding XAML resource dictionary in the Themes folder.
/// </summary>
public sealed class ThemeRegistry : IThemeRegistry
{
    private static readonly List<ThemeDescriptor> Themes =
    [
        new("Obsidian",   "Obsidian",   ThemeBaseVariant.Dark,  "Themes/ObsidianTheme.xaml"),
        new("Midnight",   "Midnight",   ThemeBaseVariant.Dark,  "Themes/MidnightTheme.xaml"),
        new("Ember",      "Ember",      ThemeBaseVariant.Dark,  "Themes/EmberTheme.xaml"),
        new("Frost",      "Frost",      ThemeBaseVariant.Light, "Themes/FrostTheme.xaml"),
        new("Parchment",  "Parchment",  ThemeBaseVariant.Light, "Themes/ParchmentTheme.xaml"),
        new("Sakura",     "Sakura",     ThemeBaseVariant.Light, "Themes/SakuraTheme.xaml"),
    ];

    public IReadOnlyList<ThemeDescriptor> All => Themes;

    public ThemeDescriptor Default => Themes[0]; // Obsidian

    public ThemeDescriptor? FindById(string id)
        => Themes.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a theme string from settings/workspace, with backward compatibility
    /// for old "Dark"/"Light" values.
    /// </summary>
    public ThemeDescriptor Resolve(string? themeString)
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
