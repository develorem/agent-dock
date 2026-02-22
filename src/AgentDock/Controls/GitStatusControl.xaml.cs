using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgentDock.Services;

namespace AgentDock.Controls;

public partial class GitStatusControl : UserControl
{
    /// <summary>
    /// Raised when a file is clicked for diff preview.
    /// Passes (filePath, diffContent).
    /// </summary>
    public event Action<string, string>? DiffRequested;

    private GitService? _gitService;
    private DispatcherTimer? _refreshTimer;

    public GitStatusControl()
    {
        InitializeComponent();
    }

    public void LoadRepository(string projectPath)
    {
        _gitService = new GitService(projectPath);

        if (!_gitService.IsGitRepository())
        {
            NotGitMessage.Visibility = Visibility.Visible;
            StatusPanel.Visibility = Visibility.Collapsed;
            NoChangesMessage.Visibility = Visibility.Collapsed;
            return;
        }

        RefreshStatus();

        // Auto-refresh every 3 seconds
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
    }

    public void StopWatching()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    private void RefreshStatus()
    {
        if (_gitService == null)
            return;

        var entries = _gitService.GetStatus();

        if (entries.Count == 0)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            NoChangesMessage.Visibility = Visibility.Visible;
            NotGitMessage.Visibility = Visibility.Collapsed;
            return;
        }

        NoChangesMessage.Visibility = Visibility.Collapsed;
        NotGitMessage.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;

        // Sort: staged first, then unstaged, alphabetical within each group
        var viewItems = entries
            .OrderBy(e => e.IsStaged ? 0 : 1)
            .ThenBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(e => new GitStatusItem(e))
            .ToList();

        FileList.ItemsSource = viewItems;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not GitStatusItem item || _gitService == null)
            return;

        var diff = _gitService.GetDiff(item.Entry.FilePath, item.Entry.IsStaged);
        if (diff != null)
        {
            DiffRequested?.Invoke(item.Entry.FilePath, diff);
        }
    }
}

public class GitStatusItem
{
    public GitFileEntry Entry { get; }

    public string FilePath => Entry.FilePath;

    public string StatusLabel => Entry.Status switch
    {
        GitFileStatus.Modified => "M",
        GitFileStatus.Added => "A",
        GitFileStatus.Deleted => "D",
        GitFileStatus.Renamed => "R",
        GitFileStatus.Untracked => "?",
        _ => " "
    };

    public string StagedLabel => Entry.IsStaged ? "(staged)" : "";

    public Brush StatusColor => Entry.IsStaged
        ? new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x40))   // green for staged
        : new SolidColorBrush(Color.FromRgb(0xD0, 0x8B, 0x20));  // amber for unstaged

    public GitStatusItem(GitFileEntry entry)
    {
        Entry = entry;
    }
}
