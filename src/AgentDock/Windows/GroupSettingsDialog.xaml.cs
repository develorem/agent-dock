using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentDock.Models;
using AgentDock.Services;

namespace AgentDock.Windows;

public partial class GroupSettingsDialog : Window
{
    private string? _selectedIcon;
    private string? _selectedIconColor;
    private Border? _selectedIconTile;
    private Border? _selectedFgSwatch;

    /// <summary>
    /// The chosen group name, icon and icon colour, or null if the user cancelled.
    /// </summary>
    public GroupSettingsResult? Result { get; private set; }

    private static readonly string?[] ColorPresets =
    [
        null,        // default / none
        "#E74C3C",   // red
        "#E67E22",   // orange
        "#F1C40F",   // yellow
        "#27AE60",   // green
        "#16A085",   // teal
        "#2980B9",   // blue
        "#5B3CC4",   // indigo
        "#8E44AD",   // purple
        "#E91E63",   // pink
        "#7F8C8D",   // gray
        "#BDC3C7",   // light
    ];

    private GroupSettingsDialog(ProjectGroup group)
    {
        _selectedIcon = group.Icon ?? "folder";
        _selectedIconColor = group.IconColor;

        InitializeComponent();

        NameTextBox.Text = group.Name;

        PopulateIconGrid(group.Icon);
        PopulateSwatches();
        UpdatePreview();
    }

    /// <summary>
    /// Shows the Group Settings dialog. Returns the chosen settings if OK, or null if cancelled.
    /// </summary>
    public static GroupSettingsResult? Show(Window owner, ProjectGroup group)
    {
        var dialog = new GroupSettingsDialog(group) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    // --- Icon Grid (built-in only — groups have no folder to discover images from) ---

    private void PopulateIconGrid(string? currentIcon) => RenderBuiltInIcons(null);

    /// <summary>
    /// (Re)builds the built-in icon tiles, optionally filtered by a search query.
    /// Preserves the current selection across re-filtering.
    /// </summary>
    private void RenderBuiltInIcons(string? query)
    {
        // The previously selected tile is about to be removed from the panel;
        // drop the stale reference so a later selection doesn't try to un-highlight it.
        if (_selectedIconTile != null && BuiltInPanel.Children.Contains(_selectedIconTile))
            _selectedIconTile = null;

        BuiltInPanel.Children.Clear();

        var matches = BuiltInIcons.Search(query).ToList();

        foreach (var icon in matches)
        {
            var glyph = new TextBlock
            {
                Text = icon.Glyph,
                FontFamily = new FontFamily(icon.FontFamily),
                FontSize = 20,
                Foreground = (Brush)FindResource("HelpForeground"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var tile = CreateIconTile(glyph, icon.Name, icon.Label);

            if (icon.Name.Equals(_selectedIcon, StringComparison.OrdinalIgnoreCase))
                SelectIconTile(tile, icon.Name);

            BuiltInPanel.Children.Add(tile);
        }

        if (NoIconsLabel != null)
            NoIconsLabel.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void IconSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        IconSearchWatermark.Visibility = string.IsNullOrEmpty(IconSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        RenderBuiltInIcons(IconSearchBox.Text);
    }

    private Border CreateIconTile(UIElement content, string iconValue, string tooltip)
    {
        var tile = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = content,
            Tag = iconValue
        };

        tile.MouseLeftButtonDown += (_, _) =>
        {
            SelectIconTile(tile, iconValue);
            UpdatePreview();
            IconGridSection.Visibility = Visibility.Collapsed;
        };

        tile.MouseEnter += (_, _) =>
        {
            if (tile != _selectedIconTile)
                tile.Background = (Brush)FindResource("MenuHighlightBackground");
        };
        tile.MouseLeave += (_, _) =>
        {
            if (tile != _selectedIconTile)
                tile.Background = Brushes.Transparent;
        };

        return tile;
    }

    private void SelectIconTile(Border tile, string iconValue)
    {
        if (_selectedIconTile != null)
        {
            _selectedIconTile.BorderBrush = Brushes.Transparent;
            _selectedIconTile.Background = Brushes.Transparent;
        }

        _selectedIconTile = tile;
        _selectedIcon = iconValue;
        tile.BorderBrush = (Brush)FindResource("TabButtonActiveBorderBrush");
        tile.Background = (Brush)FindResource("MenuHighlightBackground");
    }

    // --- Colour Swatches ---

    private void PopulateSwatches()
    {
        foreach (var color in ColorPresets)
        {
            var swatch = CreateSwatch(color);

            if (string.Equals(color, _selectedIconColor, StringComparison.OrdinalIgnoreCase)
                || (color == null && _selectedIconColor == null))
            {
                SelectSwatch(swatch, color);
            }

            ForegroundSwatchPanel.Children.Add(swatch);
        }
    }

    private Border CreateSwatch(string? color)
    {
        var size = 22.0;
        UIElement content;

        if (color == null)
        {
            content = new Ellipse
            {
                Width = size - 4,
                Height = size - 4,
                Stroke = (Brush)FindResource("HelpForeground"),
                StrokeThickness = 1.2,
                StrokeDashArray = [2, 2],
                Fill = Brushes.Transparent
            };
        }
        else
        {
            var brush = ParseHexBrush(color) ?? Brushes.Gray;
            content = new Ellipse
            {
                Width = size - 4,
                Height = size - 4,
                Fill = brush,
                Stroke = Brushes.Transparent,
                StrokeThickness = 0
            };
        }

        var swatch = new Border
        {
            Width = size + 4,
            Height = size + 4,
            CornerRadius = new CornerRadius((size + 4) / 2),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            Child = content,
            Tag = color,
            ToolTip = color ?? "Default"
        };

        swatch.MouseLeftButtonDown += (_, _) =>
        {
            SelectSwatch(swatch, color);
            UpdatePreview();
        };

        swatch.MouseEnter += (_, _) =>
        {
            if (swatch != _selectedFgSwatch)
                swatch.Background = (Brush)FindResource("MenuHighlightBackground");
        };
        swatch.MouseLeave += (_, _) =>
        {
            if (swatch != _selectedFgSwatch)
                swatch.Background = Brushes.Transparent;
        };

        return swatch;
    }

    private void SelectSwatch(Border swatch, string? color)
    {
        if (_selectedFgSwatch != null)
        {
            _selectedFgSwatch.BorderBrush = Brushes.Transparent;
            _selectedFgSwatch.Background = Brushes.Transparent;
        }

        swatch.BorderBrush = (Brush)FindResource("TabButtonActiveBorderBrush");
        swatch.Background = (Brush)FindResource("MenuHighlightBackground");

        _selectedFgSwatch = swatch;
        _selectedIconColor = color;
    }

    // --- Preview ---

    private void UpdatePreview()
    {
        var icon = _selectedIcon ?? "folder";
        var builtIn = BuiltInIcons.Find(icon) ?? BuiltInIcons.Default;

        var foreground = _selectedIconColor != null
            ? ParseHexBrush(_selectedIconColor) ?? (Brush)FindResource("HelpForeground")
            : (Brush)FindResource("HelpForeground");

        IconPreviewHost.Child = new TextBlock
        {
            Text = builtIn.Glyph,
            FontFamily = new FontFamily(builtIn.FontFamily),
            FontSize = 24,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        IconPreviewLabel.Text = builtIn.Label;
    }

    // --- Event Handlers ---

    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        var show = IconGridSection.Visibility != Visibility.Visible;
        IconGridSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            IconSearchBox.SelectAll();
            IconSearchBox.Focus();
        }
    }

    // Closes the icon picker without changing the current selection. Gives a reachable
    // exit while the expanded grid pushes the dialog's OK/Cancel below the window.
    private void CancelIconPicker_Click(object sender, RoutedEventArgs e)
        => IconGridSection.Visibility = Visibility.Collapsed;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var typedName = NameTextBox.Text?.Trim();
        Result = new GroupSettingsResult
        {
            Name = string.IsNullOrWhiteSpace(typedName) ? null : typedName,
            Icon = _selectedIcon,
            IconColor = _selectedIconColor,
        };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static SolidColorBrush? ParseHexBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Result of the <see cref="GroupSettingsDialog"/>. <see cref="Name"/> is null when the
/// user cleared the field (the caller keeps the existing name in that case).
/// </summary>
public class GroupSettingsResult
{
    public string? Name { get; init; }
    public string? Icon { get; init; }
    public string? IconColor { get; init; }
}
