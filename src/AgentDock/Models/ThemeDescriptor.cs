namespace AgentDock.Models;

/// <summary>
/// The two fundamental light/dark variants that AvalonDock and syntax highlighting support.
/// </summary>
public enum ThemeBaseVariant
{
    Light,
    Dark
}

/// <summary>
/// Describes a named theme with its metadata and resource location.
/// </summary>
public record ThemeDescriptor(
    string Id,
    string DisplayName,
    ThemeBaseVariant BaseVariant,
    string ResourcePath
);
