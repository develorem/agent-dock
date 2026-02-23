using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AgentDock.Models;
using AgentDock.Services;

namespace AgentDock.Windows;

public partial class ProjectSettingsDialog : Window
{
    private string? _selectedIcon;
    private string? _selectedIconColor;
    private Border? _selectedIconTile;
    private Border? _selectedFgSwatch;
    private readonly string _projectFolder;

    /// <summary>
    /// The resulting settings if the user clicks OK, or null if cancelled.
    /// </summary>
    public ProjectSettings? Result { get; private set; }

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

    private ProjectSettingsDialog(string projectFolder, ProjectSettings settings)
    {
        _projectFolder = projectFolder;
        _selectedIcon = settings.Icon ?? "folder";
        _selectedIconColor = settings.IconColor;

        InitializeComponent();

        PopulateIconGrid(settings.Icon);
        PopulateSwatches();
        UpdatePreview();
    }

    /// <summary>
    /// Shows the Project Settings dialog. Returns the updated ProjectSettings if OK, or null if cancelled.
    /// </summary>
    public static ProjectSettings? Show(Window owner, string projectFolder)
    {
        var settings = ProjectSettingsManager.Load(projectFolder);
        var dialog = new ProjectSettingsDialog(projectFolder, settings) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    // --- Icon Grid ---

    private void PopulateIconGrid(string? currentIcon)
    {
        foreach (var icon in BuiltInIcons.All)
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

            if (icon.Name.Equals(currentIcon, StringComparison.OrdinalIgnoreCase))
                SelectIconTile(tile, icon.Name);

            BuiltInPanel.Children.Add(tile);
        }

        // Discovered images
        var logos = MainWindow.FindAllProjectLogos(_projectFolder);
        if (logos.Count > 0)
        {
            DiscoveredSection.Visibility = Visibility.Visible;

            foreach (var logoPath in logos)
            {
                try
                {
                    var bitmap = new BitmapImage(new Uri(logoPath));
                    var img = new Image
                    {
                        Source = bitmap,
                        Width = 24,
                        Height = 24,
                        Stretch = Stretch.Uniform
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

                    var iconValue = logoPath.StartsWith(_projectFolder, StringComparison.OrdinalIgnoreCase)
                        ? System.IO.Path.GetRelativePath(_projectFolder, logoPath)
                        : logoPath;

                    var tile = CreateIconTile(img, iconValue, System.IO.Path.GetFileName(logoPath));

                    if (string.Equals(iconValue, currentIcon, StringComparison.OrdinalIgnoreCase))
                        SelectIconTile(tile, iconValue);

                    DiscoveredPanel.Children.Add(tile);
                }
                catch
                {
                    // Skip images that fail to load
                }
            }
        }
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
            // Collapse the grid after selection
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
            // "Default" swatch — dotted border circle with no fill
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

        var builtIn = BuiltInIcons.Find(icon);
        if (builtIn != null)
        {
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
        else
        {
            // File-based icon
            var filePath = System.IO.Path.IsPathRooted(icon)
                ? icon
                : System.IO.Path.Combine(_projectFolder, icon);

            if (File.Exists(filePath))
            {
                try
                {
                    var img = new Image
                    {
                        Source = new BitmapImage(new Uri(filePath)),
                        Stretch = Stretch.Uniform
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    IconPreviewHost.Child = img;
                    IconPreviewLabel.Text = System.IO.Path.GetFileName(filePath);
                }
                catch
                {
                    SetFallbackPreview();
                }
            }
            else
            {
                SetFallbackPreview();
            }
        }
    }

    private void SetFallbackPreview()
    {
        var fallback = BuiltInIcons.Default;
        IconPreviewHost.Child = new TextBlock
        {
            Text = fallback.Glyph,
            FontFamily = new FontFamily(fallback.FontFamily),
            FontSize = 24,
            Foreground = (Brush)FindResource("HelpForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        IconPreviewLabel.Text = fallback.Label;
    }

    // --- Event Handlers ---

    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        IconGridSection.Visibility = IconGridSection.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new ProjectSettings
        {
            Icon = _selectedIcon,
            IconColor = _selectedIconColor
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
