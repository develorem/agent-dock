using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentDock.Windows;

/// <summary>
/// Lightbox for a queued image attachment: shows it larger than the input-area
/// chip, with a Remove button. <see cref="Show"/> returns true if the user asked
/// to remove the image (Gmail-style: click the thumbnail to enlarge, remove from
/// there).
/// </summary>
public partial class ImagePreviewDialog : Window
{
    /// <summary>True when the user clicked Remove.</summary>
    public bool RemoveRequested { get; private set; }

    private ImagePreviewDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the preview. Returns true if the user clicked Remove, false otherwise.
    /// </summary>
    public static bool Show(Window owner, ImageSource image, string title)
    {
        var dialog = new ImagePreviewDialog { Owner = owner };
        dialog.PreviewImage.Source = image;
        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.ShowDialog();
        return dialog.RemoveRequested;
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveRequested = true;
        DialogResult = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
