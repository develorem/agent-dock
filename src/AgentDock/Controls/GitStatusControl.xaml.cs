using System.IO;
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
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounceTimer;

    public GitStatusControl()
    {
        InitializeComponent();
        ThemeManager.ThemeChanged += _ => RefreshStatus();
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
        StartWatching(projectPath);
    }

    private void StartWatching(string projectPath)
    {
        // Debounce timer — coalesces rapid file changes into a single refresh
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RefreshStatus();
        };

        try
        {
            _watcher = new FileSystemWatcher(projectPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.Error += OnWatcherError;
        }
        catch (Exception ex)
        {
            Log.Warn($"GitStatusControl: FileSystemWatcher failed, falling back to polling — {ex.Message}");
            FallBackToPolling();
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore .git/index.lock churn during git operations — wait for it to settle
        Dispatcher.BeginInvoke(() =>
        {
            // Reset the debounce timer on every change
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        });
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Log.Warn($"GitStatusControl: FileSystemWatcher error — {e.GetException().Message}");

        // Watcher buffer overflow or disconnection — do a refresh and restart
        Dispatcher.BeginInvoke(() =>
        {
            RefreshStatus();
            FallBackToPolling();
        });
    }

    /// <summary>
    /// Falls back to 3-second polling if FileSystemWatcher fails.
    /// </summary>
    private void FallBackToPolling()
    {
        DisposeWatcher();

        var pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        pollTimer.Tick += (_, _) => RefreshStatus();
        pollTimer.Start();
        _debounceTimer = pollTimer; // reuse field for cleanup
    }

    public void StopWatching()
    {
        _debounceTimer?.Stop();
        _debounceTimer = null;
        DisposeWatcher();
    }

    private void DisposeWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void RefreshStatus()
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
        ? ThemeManager.GetBrush("GitStagedForeground")
        : ThemeManager.GetBrush("GitUnstagedForeground");

    public GitStatusItem(GitFileEntry entry)
    {
        Entry = entry;
    }
}
