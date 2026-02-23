using System.Windows;
using System.Windows.Media;
using HL.Interfaces;
using ICSharpCode.AvalonEdit.Highlighting;

namespace AgentDock.Services;

public enum AppTheme
{
    Light,
    Dark
}

public static class ThemeManager
{

    private static ResourceDictionary? _currentThemeDictionary;
    private static bool _sharedStylesLoaded;

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Light;

    public static event Action<AppTheme>? ThemeChanged;

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

    public static void ApplyTheme(AppTheme theme, bool raiseEvent = true)
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

        // Load the new theme dictionary
        var uri = theme == AppTheme.Dark
            ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("Themes/LightTheme.xaml", UriKind.Relative);

        _currentThemeDictionary = new ResourceDictionary { Source = uri };
        app.Resources.MergedDictionaries.Add(_currentThemeDictionary);

        // Switch syntax highlighting theme
        HighlightingManager.SetCurrentTheme(theme == AppTheme.Dark ? "VS2019_Dark" : "Light");

        SaveThemePreference(theme);

        if (raiseEvent)
            ThemeChanged?.Invoke(theme);
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
        if (CurrentTheme == AppTheme.Light)
        {
            return ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance
                .GetDefinitionByExtension(extension);
        }

        return null;
    }

    private static AppTheme LoadThemePreference()
    {
        var value = AppSettings.GetString("Theme", "Light");
        return value == "Dark" ? AppTheme.Dark : AppTheme.Light;
    }

    private static void SaveThemePreference(AppTheme theme)
    {
        AppSettings.SetString("Theme", theme.ToString());
    }
}
