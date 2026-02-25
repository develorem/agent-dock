using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AgentDock.Services;

namespace AgentDock.Controls;

public partial class FilePreviewControl : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".zip", ".tar", ".gz", ".7z", ".rar",
        ".bin", ".dat", ".db", ".sqlite", ".woff", ".woff2", ".ttf",
        ".otf", ".eot", ".mp3", ".mp4", ".avi", ".mov", ".pdf",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".nupkg", ".snk", ".pfx"
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown"
    };

    // Max file size to preview (5 MB)
    private const long MaxTextFileSize = 5 * 1024 * 1024;

    private string? _currentExtension;
    private bool _isMarkdownRendered;

    public FilePreviewControl()
    {
        InitializeComponent();
        ApplyLinkColor();
        ThemeManager.ThemeChanged += _ => OnThemeChanged();
    }

    private void OnThemeChanged()
    {
        ApplyLinkColor();

        // Re-apply syntax highlighting with new theme colors
        if (_currentExtension != null && TextPreview.Visibility == Visibility.Visible && _diffColorizer == null)
        {
            TextPreview.SyntaxHighlighting = ThemeManager.GetHighlighting(_currentExtension);
        }

        TextPreview.TextArea.TextView.Redraw();

        // Re-render markdown so FlowDocument picks up new theme colors
        if (MarkdownPreview.Visibility == Visibility.Visible)
        {
            var md = MarkdownPreview.Markdown;
            MarkdownPreview.Markdown = "";
            MarkdownPreview.Markdown = md;
        }
    }

    private void ApplyLinkColor()
    {
        TextPreview.TextArea.TextView.LinkTextForegroundBrush =
            ThemeManager.GetBrush("PreviewLinkForeground");
    }

    private MarkdownLinkColorizer? _linkColorizer;

    private void ApplyMarkdownLinkColorizer(string extension)
    {
        RemoveMarkdownLinkColorizer();

        if (MarkdownExtensions.Contains(extension))
        {
            _linkColorizer = new MarkdownLinkColorizer();
            TextPreview.TextArea.TextView.LineTransformers.Add(_linkColorizer);
        }
    }

    private void RemoveMarkdownLinkColorizer()
    {
        if (_linkColorizer != null)
        {
            TextPreview.TextArea.TextView.LineTransformers.Remove(_linkColorizer);
            _linkColorizer = null;
        }
    }

    public void ShowFile(string filePath)
    {
        HideAll();

        if (!File.Exists(filePath))
        {
            ShowNoPreview("File not found");
            return;
        }

        var extension = Path.GetExtension(filePath);

        if (ImageExtensions.Contains(extension))
        {
            ShowImage(filePath, extension);
        }
        else if (BinaryExtensions.Contains(extension))
        {
            ShowNoPreview("No Preview — binary file");
        }
        else if (MarkdownExtensions.Contains(extension))
        {
            ShowMarkdown(filePath, extension);
        }
        else
        {
            ShowText(filePath, extension);
        }
    }

    private DiffLineColorizer? _diffColorizer;

    public void ShowDiff(string diffContent)
    {
        HideAll();

        // Remove any previous diff colorizer
        RemoveDiffColorizer();

        TextPreview.Text = diffContent;
        TextPreview.SyntaxHighlighting = null; // disable built-in highlighting for diffs

        // Add custom diff line colorizer
        _diffColorizer = new DiffLineColorizer();
        TextPreview.TextArea.TextView.LineTransformers.Add(_diffColorizer);

        TextPreview.Visibility = Visibility.Visible;
    }

    public void Clear()
    {
        HideAll();
        EmptyMessage.Visibility = Visibility.Visible;
    }

    private void ShowMarkdown(string filePath, string extension)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxTextFileSize)
            {
                ShowNoPreview($"File too large ({fileInfo.Length / 1024 / 1024} MB)");
                return;
            }

            if (IsBinaryFile(filePath))
            {
                ShowNoPreview("No Preview — binary file");
                return;
            }

            var markdownText = File.ReadAllText(filePath);
            _currentExtension = extension;

            // Load into AvalonEdit for source view (hidden initially)
            TextPreview.Load(filePath);
            TextPreview.SyntaxHighlighting = ThemeManager.GetHighlighting(extension);
            ApplyMarkdownLinkColorizer(extension);
            TextPreview.ScrollToHome();

            // Render markdown preview
            MarkdownPreview.Markdown = markdownText;
            MarkdownPreview.Visibility = Visibility.Visible;

            // Show toggle button in "rendered" state
            _isMarkdownRendered = true;
            MarkdownToggleIcon.Text = "\uE943"; // Code icon — click to see source
            MarkdownToggleButton.ToolTip = "Switch to source view";
            MarkdownToggleButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowNoPreview($"Cannot preview: {ex.Message}");
        }
    }

    private void MarkdownToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isMarkdownRendered)
        {
            // Switch to source view
            MarkdownPreview.Visibility = Visibility.Collapsed;
            TextPreview.Visibility = Visibility.Visible;
            _isMarkdownRendered = false;
            MarkdownToggleIcon.Text = "\uE890"; // Preview/eye icon — click to see rendered
            MarkdownToggleButton.ToolTip = "Switch to rendered view";
        }
        else
        {
            // Switch to rendered view
            TextPreview.Visibility = Visibility.Collapsed;
            MarkdownPreview.Visibility = Visibility.Visible;
            _isMarkdownRendered = true;
            MarkdownToggleIcon.Text = "\uE943"; // Code icon — click to see source
            MarkdownToggleButton.ToolTip = "Switch to source view";
        }
    }

    private void ShowText(string filePath, string extension)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxTextFileSize)
            {
                ShowNoPreview($"File too large ({fileInfo.Length / 1024 / 1024} MB)");
                return;
            }

            // Check if file appears to be binary by reading first chunk
            if (IsBinaryFile(filePath))
            {
                ShowNoPreview("No Preview — binary file");
                return;
            }

            TextPreview.Load(filePath);
            _currentExtension = extension;
            TextPreview.SyntaxHighlighting = ThemeManager.GetHighlighting(extension);
            ApplyMarkdownLinkColorizer(extension);
            TextPreview.ScrollToHome();
            TextPreview.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowNoPreview($"Cannot preview: {ex.Message}");
        }
    }

    private void ShowImage(string filePath, string extension)
    {
        try
        {
            // SVG not natively supported by WPF — show as text
            if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                ShowText(filePath, extension);
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            ImagePreview.Source = bitmap;
            ImageContainer.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowNoPreview($"Cannot load image: {ex.Message}");
        }
    }

    private void ShowNoPreview(string message)
    {
        NoPreviewMessage.Text = message;
        NoPreviewMessage.Visibility = Visibility.Visible;
    }

    private void HideAll()
    {
        EmptyMessage.Visibility = Visibility.Collapsed;
        TextPreview.Visibility = Visibility.Collapsed;
        MarkdownPreview.Visibility = Visibility.Collapsed;
        MarkdownToggleButton.Visibility = Visibility.Collapsed;
        ImageContainer.Visibility = Visibility.Collapsed;
        NoPreviewMessage.Visibility = Visibility.Collapsed;
        _isMarkdownRendered = false;
        RemoveDiffColorizer();
        RemoveMarkdownLinkColorizer();
    }

    private void RemoveDiffColorizer()
    {
        if (_diffColorizer != null)
        {
            TextPreview.TextArea.TextView.LineTransformers.Remove(_diffColorizer);
            _diffColorizer = null;
        }
    }

    /// <summary>
    /// Checks if a file is likely binary by looking for null bytes in the first 8KB.
    /// </summary>
    private static bool IsBinaryFile(string filePath)
    {
        try
        {
            var buffer = new byte[8192];
            using var stream = File.OpenRead(filePath);
            var bytesRead = stream.Read(buffer, 0, buffer.Length);

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
