using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentDock.Services;

/// <summary>
/// Generates a runtime taskbar icon by compositing the app logo with a
/// theme-colored accent bar at the bottom.
/// </summary>
public static class TaskbarIconHelper
{
    private static BitmapImage? _logoBitmap;

    /// <summary>
    /// Creates a BitmapSource of the app logo with a colored bar at the bottom.
    /// </summary>
    public static ImageSource CreateThemedIcon(Color barColor)
    {
        _logoBitmap ??= LoadLogoBitmap();

        int width = _logoBitmap.PixelWidth;
        int height = _logoBitmap.PixelHeight;

        // Bar: full width, ~8% of height, pinned to bottom
        int barHeight = Math.Max(2, (int)(height * 0.08));
        int barY = height - barHeight;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_logoBitmap, new Rect(0, 0, width, height));

            var barBrush = new SolidColorBrush(barColor);
            barBrush.Freeze();
            dc.DrawRectangle(barBrush, null, new Rect(0, barY, width, barHeight));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        return rtb;
    }

    private static BitmapImage LoadLogoBitmap()
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri("pack://application:,,,/Assets/agentdock.png");
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
