using System.Windows;
using System.Windows.Media;
using AgentDock.Models;
using HL.Interfaces;
using ICSharpCode.AvalonEdit.Highlighting;

namespace AgentDock.Services;

public static class ThemeManager
{
    private static ResourceDictionary? _currentThemeDictionary;
    private static bool _sharedStylesLoaded;

    public static ThemeDescriptor CurrentTheme { get; private set; } = ThemeRegistry.Default;

    /// <summary>
    /// Convenience: returns the base variant (Dark/Light) of the current theme.
    /// Used by AvalonDock, syntax highlighting, and any code that only cares about dark vs light.
    /// </summary>
    public static ThemeBaseVariant BaseVariant => CurrentTheme.BaseVariant;

    public static event Action<ThemeDescriptor>? ThemeChanged;

    /// <summary>
    /// Themed highlighting manager from Dirkster.HL — provides dark-mode-aware syntax colors.
    /// </summary>
    public static IThemedHighlightingManager HighlightingManager { get; } =
        HL.Manager.ThemedHighlightingManager.Instance;

    public static void Initialize()
    {
        var saved = LoadThemePreference();
        ApplyTheme(saved, raiseEvent: false);
    }

    public static void ApplyTheme(ThemeDescriptor theme, bool raiseEvent = true)
    {
        CurrentTheme = theme;

        var app = Application.Current;
        if (app == null) return;

        // Load shared styles once (they use DynamicResource, so they auto-update)
        if (!_sharedStylesLoaded)
        {
            var sharedUri = new Uri("Themes/SharedStyles.xaml", UriKind.Relative);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = sharedUri });
            _sharedStylesLoaded = true;
        }

        // Remove previous theme dictionary
        if (_currentThemeDictionary != null)
            app.Resources.MergedDictionaries.Remove(_currentThemeDictionary);

        // Load the new theme dictionary by its ResourcePath
        var uri = new Uri(theme.ResourcePath, UriKind.Relative);
        _currentThemeDictionary = new ResourceDictionary { Source = uri };
        app.Resources.MergedDictionaries.Add(_currentThemeDictionary);

        // Switch syntax highlighting based on base variant
        HighlightingManager.SetCurrentTheme(
            theme.BaseVariant == ThemeBaseVariant.Dark ? "VS2019_Dark" : "Light");

        SaveThemePreference(theme);

        if (raiseEvent)
            ThemeChanged?.Invoke(theme);
    }

    /// <summary>
    /// Convenience overload: apply by theme Id string.
    /// </summary>
    public static void ApplyTheme(string themeId)
    {
        var theme = ThemeRegistry.Resolve(themeId);
        ApplyTheme(theme);
    }

    public static SolidColorBrush GetBrush(string key)
    {
        if (Application.Current.TryFindResource(key) is SolidColorBrush brush)
            return brush;
        return Brushes.Magenta; // obvious fallback for missing keys
    }

    /// <summary>
    /// Gets a syntax highlighting definition appropriate for the current theme.
    /// Falls back to the built-in AvalonEdit manager only in light mode (its colors
    /// assume a white background and are invisible on dark backgrounds).
    /// </summary>
    public static IHighlightingDefinition? GetHighlighting(string extension)
    {
        var themed = HighlightingManager.GetDefinitionByExtension(extension);
        if (themed != null)
            return themed;

        // Only fall back to non-themed AvalonEdit in light mode
        if (CurrentTheme.BaseVariant == ThemeBaseVariant.Light)
        {
            return ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance
                .GetDefinitionByExtension(extension);
        }

        return null;
    }

    private static ThemeDescriptor LoadThemePreference()
    {
        var value = AppSettings.GetString("Theme", "Obsidian");
        return ThemeRegistry.Resolve(value);
    }

    private static void SaveThemePreference(ThemeDescriptor theme)
    {
        AppSettings.SetString("Theme", theme.Id);
    }
}
