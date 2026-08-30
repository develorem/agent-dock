using System.Windows.Media;
using AgentDock.Models;
using HL.Interfaces;
using ICSharpCode.AvalonEdit.Highlighting;

namespace AgentDock.Services.Abstractions;

/// <summary>The set of themes the app can apply.</summary>
public interface IThemeRegistry
{
    IReadOnlyList<ThemeDescriptor> All { get; }
    ThemeDescriptor Default { get; }
    ThemeDescriptor? FindById(string id);

    /// <summary>Resolves a stored theme string, tolerating legacy "Dark"/"Light" values.</summary>
    ThemeDescriptor Resolve(string? themeString);
}

/// <summary>Applies themes and resolves themed resources.</summary>
public interface IThemeService
{
    ThemeDescriptor CurrentTheme { get; }
    ThemeBaseVariant BaseVariant { get; }
    IThemedHighlightingManager HighlightingManager { get; }

    event Action<ThemeDescriptor>? ThemeChanged;

    void Initialize();
    void ApplyTheme(ThemeDescriptor theme, bool raiseEvent = true);
    void ApplyTheme(string themeId);
    SolidColorBrush GetBrush(string key);
    IHighlightingDefinition? GetHighlighting(string extension);
}
