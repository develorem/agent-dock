using System.Windows.Media;

namespace AgentDock.Services;

/// <summary>
/// Registry of built-in project icons available for tab buttons.
/// Uses Segoe MDL2 Assets glyphs — vector, theme-aware, no external assets needed.
/// </summary>
public static class BuiltInIcons
{
    public record IconInfo(string Name, string Label, string Glyph, string FontFamily);

    private static readonly IconInfo[] Icons =
    [
        new("folder",   "Folder",      "\uED25", "Segoe MDL2 Assets"),    //
        new("code",     "Code",        "\uE943", "Segoe MDL2 Assets"),    // { } code brackets
        new("terminal", "Terminal",    "\uE756", "Segoe MDL2 Assets"),    //  command prompt
        new("web",      "Web",        "\uE774", "Segoe MDL2 Assets"),    //  globe
        new("database", "Database",    "\uEE94", "Segoe MDL2 Assets"),    //  database
        new("settings", "Settings",    "\uE713", "Segoe MDL2 Assets"),    //  gear
        new("library",  "Library",     "\uE8F1", "Segoe MDL2 Assets"),    //  library
        new("bug",      "Bug",         "\uEBE8", "Segoe MDL2 Assets"),    //  bug
        new("rocket",   "Rocket",      "\uF3ED", "Segoe MDL2 Assets"),    //  rocket
        new("game",     "Game",        "\uE7FC", "Segoe MDL2 Assets"),    //  game controller
        new("music",    "Music",       "\uE8D6", "Segoe MDL2 Assets"),    //  music note
        new("cloud",    "Cloud",       "\uE753", "Segoe MDL2 Assets"),    //  cloud
        new("star",     "Star",        "\uE734", "Segoe MDL2 Assets"),    //  favorite star
        new("heart",    "Heart",       "\uEB51", "Segoe MDL2 Assets"),    //  heart
        new("shield",   "Shield",      "\uE76E", "Segoe MDL2 Assets"),    //  shield
        new("lock",     "Lock",        "\uE72E", "Segoe MDL2 Assets"),    //  lock
        new("home",     "Home",        "\uE80F", "Segoe MDL2 Assets"),    //  home
        new("paint",    "Paint",       "\uE771", "Segoe MDL2 Assets"),    //  highlight/brush
        new("camera",   "Camera",      "\uE722", "Segoe MDL2 Assets"),    //  camera
        new("phone",    "Phone",       "\uE717", "Segoe MDL2 Assets"),    //  cell phone
        new("pin",      "Pin",         "\uE718", "Segoe MDL2 Assets"),    //  pin/attach
        new("wrench",   "Wrench",      "\uE8A1", "Segoe MDL2 Assets"),    //  repair
        new("calendar", "Calendar",    "\uE787", "Segoe MDL2 Assets"),    //  calendar week
        new("search",   "Search",      "\uE721", "Segoe MDL2 Assets"),    //  zoom/search
    ];

    /// <summary>
    /// All available built-in icons.
    /// </summary>
    public static IReadOnlyList<IconInfo> All => Icons;

    /// <summary>
    /// Tries to find a built-in icon by name. Case-insensitive.
    /// </summary>
    public static IconInfo? Find(string name)
    {
        foreach (var icon in Icons)
        {
            if (icon.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return icon;
        }
        return null;
    }

    /// <summary>
    /// Checks if a name refers to a built-in icon.
    /// </summary>
    public static bool IsBuiltIn(string name) => Find(name) != null;

    /// <summary>
    /// The default icon used when no icon is configured or discovered.
    /// </summary>
    public static IconInfo Default => Icons[0]; // folder
}
