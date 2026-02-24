using System.Windows;
using System.Windows.Input;
using AgentDock.Services;

namespace AgentDock.Windows;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _updateInfo;
    private CancellationTokenSource? _downloadCts;

    public UpdateDialog(Window owner, UpdateInfo updateInfo)
    {
        InitializeComponent();
        Owner = owner;
        _updateInfo = updateInfo;

        CurrentVersionText.Text = $"v{App.Version}";
        NewVersionText.Text = $"v{updateInfo.Version}";
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

        var installerPath = await UpdateCheckService.DownloadInstallerAsync(
            _updateInfo.DownloadUrl, progress, _downloadCts.Token);

        if (installerPath == null)
        {
            ProgressText.Text = "Download failed. Please try again later.";
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Retry";
            LaterButton.IsEnabled = true;
            return;
        }

        UpdateCheckService.LaunchUpdateAndShutdown(installerPath);
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        DialogResult = false;
    }
}
