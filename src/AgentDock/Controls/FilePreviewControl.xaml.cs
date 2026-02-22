using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit.Highlighting;

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

    // Max file size to preview (5 MB)
    private const long MaxTextFileSize = 5 * 1024 * 1024;

    public FilePreviewControl()
    {
        InitializeComponent();
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
        else
        {
            ShowText(filePath, extension);
        }
    }

    public void ShowDiff(string diffContent)
    {
        HideAll();

        TextPreview.Text = diffContent;
        TextPreview.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(".patch");
        TextPreview.Visibility = Visibility.Visible;
    }

    public void Clear()
    {
        HideAll();
        EmptyMessage.Visibility = Visibility.Visible;
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
            TextPreview.SyntaxHighlighting = HighlightingManager.Instance
                .GetDefinitionByExtension(extension);
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
        ImageContainer.Visibility = Visibility.Collapsed;
        NoPreviewMessage.Visibility = Visibility.Collapsed;
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
