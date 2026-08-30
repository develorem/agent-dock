using System.Windows;
using System.Windows.Input;
using AgentDock.Services;

using AgentDock.Services.Abstractions;

namespace AgentDock.Windows;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _updateInfo;
    private CancellationTokenSource? _downloadCts;

    private readonly IMarkdownRenderer _markdown;
    private readonly IUpdateCheckService _updateCheck;

    public UpdateDialog(
        Window owner,
        UpdateInfo updateInfo,
        IMarkdownRenderer markdown,
        IUpdateCheckService updateCheck)
    {
        _markdown = markdown;
        _updateCheck = updateCheck;
        InitializeComponent();
        Owner = owner;
        _updateInfo = updateInfo;

        CurrentVersionText.Text = $"v{App.Version}";
        NewVersionText.Text = $"v{updateInfo.Version}";

        if (!string.IsNullOrWhiteSpace(updateInfo.Notes))
        {
            var notes = MarkdownRenderer.StripLeadingVersionHeading(updateInfo.Notes);
            _markdown.RenderTo(NotesViewer, notes);
            NotesPanel.Visibility = Visibility.Visible;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Downloading...";

        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            DownloadProgress.Value = percent;
            ProgressText.Text = percent < 100
                ? $"Downloading... {percent}%"
                : "Download complete. Installing...";
        });

        var installerPath = await _updateCheck.DownloadInstallerAsync(
            _updateInfo.DownloadUrl, progress, _downloadCts.Token);

        if (installerPath == null)
        {
            ProgressText.Text = "Download failed. Please try again later.";
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Retry";
            LaterButton.IsEnabled = true;
            return;
        }

        _updateCheck.LaunchUpdateAndShutdown(installerPath);
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        DialogResult = false;
    }
}
