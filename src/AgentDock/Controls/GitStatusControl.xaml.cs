using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentDock.Models;
using AgentDock.Services;

namespace AgentDock.Controls;

public partial class GitStatusControl : UserControl
{
    /// <summary>
    /// Raised when a file is clicked for diff preview.
    /// Passes (filePath, diffContent).
    /// </summary>
    public event Action<string, string>? DiffRequested;

    /// <summary>
    /// Raised when the file system watcher detects changes (debounced).
    /// </summary>
    public event Action? FileSystemChanged;

    private GitService? _gitService;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounceTimer;
    private List<string> _allBranches = [];
    private bool _isGitRepository;
    private string _projectPath = "";
    // Only the active (visible) tab does git work. Background tabs are suspended
    // — no watcher, no status refreshes — so N open projects don't serialize N
    // synchronous `git status` calls onto the one UI thread. The toolbar diamond
    // still updates because it's driven by the session state, not by git.
    private bool _active;
    // Stored so it can be detached in Cleanup — a lambda subscription to the
    // static ThemeManager.ThemeChanged would root this control (and its cached
    // visuals) for the whole app lifetime, even after the project is closed.
    private readonly Action<ThemeDescriptor> _themeChangedHandler;

    // The bound file list. Set once as ItemsSource; refreshes apply a minimal
    // in-place delta (SyncFileList) rather than reassigning, so unchanged rows
    // don't re-render and the user's selection survives a refresh.
    private readonly ObservableCollection<GitStatusItem> _items = [];
    // Re-entrancy guard for the async refresh: if a refresh is requested while
    // one is running, we set a pending flag and run exactly one more pass after.
    private bool _refreshing;
    private bool _refreshQueued;

    public GitStatusControl()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;
        _themeChangedHandler = _ => RefreshStatus();
        ThemeManager.ThemeChanged += _themeChangedHandler;
    }

    public void LoadRepository(string projectPath)
    {
        _projectPath = projectPath;
        _gitService = new GitService(projectPath);
        _isGitRepository = _gitService.IsGitRepository();

        if (!_isGitRepository)
        {
            NotGitMessage.Visibility = Visibility.Visible;
            StatusPanel.Visibility = Visibility.Collapsed;
            NoChangesMessage.Visibility = Visibility.Collapsed;
            BranchPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Status refresh + file watching are deferred to Activate(), called when
        // this project's tab becomes visible (see MainWindow.SwitchToProject).
        if (_active)
        {
            RefreshStatus();
            StartWatching(_projectPath);
        }
    }

    /// <summary>
    /// Called when this project's tab becomes the active/visible one. Does an
    /// immediate status refresh and starts the file watcher. Idempotent.
    /// </summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;
        if (!_isGitRepository) return;
        RefreshStatus();
        if (_watcher == null && _debounceTimer == null)
            StartWatching(_projectPath);
    }

    /// <summary>
    /// Called when this project's tab is switched away from. Stops the watcher
    /// and debounce timer so a background tab consumes no git/UI resources.
    /// </summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        StopWatching();
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
                // Default is 8 KB, which overflows on busy repos (node_modules installs,
                // build output) and drops events with "Too many changes at once". 64 KB
                // is the practical ceiling for non-paged pool usage on most systems.
                InternalBufferSize = 64 * 1024,
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

    /// <summary>
    /// Full teardown on project close. Detaches the theme handler (otherwise the
    /// static ThemeManager.ThemeChanged event roots this control forever) and
    /// stops all watching. Call from the project-removal path.
    /// </summary>
    public void Cleanup()
    {
        ThemeManager.ThemeChanged -= _themeChangedHandler;
        _active = false;
        StopWatching();
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

    private void CopyBranch_Click(object sender, RoutedEventArgs e)
    {
        var branch = BranchName.Text;
        if (!string.IsNullOrEmpty(branch))
            Clipboard.SetText(branch);
    }

    private void BranchMore_Click(object sender, RoutedEventArgs e)
    {
        if (_gitService == null)
            return;

        _allBranches = _gitService.GetLocalBranches();
        BranchFilterTextBox.Text = "";
        UpdateBranchList("");
        BranchPickerPopup.IsOpen = true;

        // Focus the textbox after the popup renders
        Dispatcher.BeginInvoke(() => BranchFilterTextBox.Focus(), DispatcherPriority.Input);
    }

    private void BranchFilterText_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateBranchList(BranchFilterTextBox.Text);
    }

    private void UpdateBranchList(string filter)
    {
        var currentBranch = BranchName.Text;

        var filtered = string.IsNullOrEmpty(filter)
            ? _allBranches
            : _allBranches.Where(b => b.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var items = filtered.Select(b => new BranchListItem(b, b == currentBranch)).ToList();
        BranchListBox.ItemsSource = items;

        // Show "+" button when filter text doesn't exactly match any branch
        var exactMatch = !string.IsNullOrWhiteSpace(filter) &&
                         _allBranches.Any(b => b.Equals(filter, StringComparison.OrdinalIgnoreCase));
        CreateBranchButton.Visibility = !string.IsNullOrWhiteSpace(filter) && !exactMatch
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BranchListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SwitchToSelectedBranch();
    }

    private void BranchListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SwitchToSelectedBranch();
    }

    private void BranchFilterTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BranchPickerPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // If there's a single filtered result, switch to it
            if (BranchListBox.Items.Count == 1)
            {
                BranchListBox.SelectedIndex = 0;
                SwitchToSelectedBranch();
            }
            // If the "+" button is visible, create the branch
            else if (CreateBranchButton.Visibility == Visibility.Visible)
            {
                CreateBranch();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Down && BranchListBox.Items.Count > 0)
        {
            BranchListBox.SelectedIndex = 0;
            BranchListBox.Focus();
            e.Handled = true;
        }
    }

    private void SwitchToSelectedBranch()
    {
        if (BranchListBox.SelectedItem is not BranchListItem item || _gitService == null)
            return;

        // Don't switch if already on this branch
        if (item.IsCurrent)
        {
            BranchPickerPopup.IsOpen = false;
            return;
        }

        var (success, message) = _gitService.CheckoutBranch(item.Name);
        BranchPickerPopup.IsOpen = false;

        if (success)
        {
            RefreshStatus();
            FileSystemChanged?.Invoke();
        }
        else
        {
            Log.Warn($"GitStatusControl: Failed to checkout branch '{item.Name}' — {message}");
        }
    }

    private void CreateBranch_Click(object sender, RoutedEventArgs e)
    {
        CreateBranch();
    }

    private void CreateBranch()
    {
        var branchName = BranchFilterTextBox.Text.Trim();
        if (string.IsNullOrEmpty(branchName) || _gitService == null)
            return;

        var (success, message) = _gitService.CreateAndCheckoutBranch(branchName);
        BranchPickerPopup.IsOpen = false;

        if (success)
        {
            RefreshStatus();
            FileSystemChanged?.Invoke();
        }
        else
        {
            Log.Warn($"GitStatusControl: Failed to create branch '{branchName}' — {message}");
        }
    }

    /// <summary>
    /// Refreshes branch + file status. The git work runs OFF the UI thread (it
    /// shells out to git, which used to block the UI thread directly); the UI is
    /// then updated as a minimal delta. Background tabs are skipped entirely.
    /// async void: this is an event-style entry point (timer tick, theme change,
    /// session-idle), not awaited by callers.
    /// </summary>
    public async void RefreshStatus()
    {
        // Background (non-active) tabs do no git work — see Activate/Deactivate.
        if (!_active || _gitService == null || !_isGitRepository)
            return;

        // Coalesce overlapping requests: if a refresh is already running, mark a
        // pending pass and let the running loop pick it up.
        if (_refreshing)
        {
            _refreshQueued = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshQueued = false;
                await RefreshCoreAsync();
            }
            while (_refreshQueued && _active && _gitService != null && _isGitRepository);
        }
        catch (Exception ex)
        {
            Log.Warn($"GitStatusControl.RefreshStatus failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var gitService = _gitService;
        if (gitService == null) return;

        // EVERYTHING that isn't a UI mutation happens on the background thread:
        // the blocking git calls AND the post-processing (sort + the membership
        // set used to diff). Only the minimal merge into the bound collection
        // (SyncFileList) runs on the UI thread after we resume.
        var (branch, target, targetSet) = await Task.Run(() =>
        {
            var b = gitService.GetCurrentBranch();
            var entries = gitService.GetStatus();
            // Sort: staged first, then unstaged, alphabetical within each group.
            var sorted = entries
                .OrderBy(e => e.IsStaged ? 0 : 1)
                .ThenBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (b, sorted, new HashSet<GitFileEntry>(sorted));
        });

        // The tab may have been deactivated/closed while git ran.
        if (!_active) return;

        // Branch header
        if (!string.IsNullOrEmpty(branch))
        {
            if (BranchName.Text != branch) BranchName.Text = branch;
            BranchPanel.Visibility = Visibility.Visible;
        }
        else
        {
            BranchPanel.Visibility = Visibility.Collapsed;
        }

        // File list
        if (target.Count == 0)
        {
            if (_items.Count > 0) _items.Clear();
            StatusPanel.Visibility = Visibility.Collapsed;
            NoChangesMessage.Visibility = Visibility.Visible;
            NotGitMessage.Visibility = Visibility.Collapsed;
            return;
        }

        NoChangesMessage.Visibility = Visibility.Collapsed;
        NotGitMessage.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;

        var changed = SyncFileList(target, targetSet);
        if (changed)
            FileSystemChanged?.Invoke();
    }

    /// <summary>
    /// Reconciles <see cref="_items"/> to <paramref name="target"/> (already
    /// sorted) with the minimum number of ObservableCollection edits. Rows whose
    /// <see cref="GitFileEntry"/> is unchanged keep their existing item instance,
    /// so they don't re-render and the selection survives. Returns true if it made
    /// any change (so callers can avoid firing downstream events on a no-op).
    /// </summary>
    private bool SyncFileList(List<GitFileEntry> target, HashSet<GitFileEntry> targetSet)
    {
        var changed = false;

        // 1. Drop rows no longer present (GitFileEntry is a record → value equality).
        //    targetSet was built on the background thread.
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(_items[i].Entry))
            {
                _items.RemoveAt(i);
                changed = true;
            }
        }

        // 2. Make _items match target order, reusing existing instances.
        for (int j = 0; j < target.Count; j++)
        {
            if (j < _items.Count && _items[j].Entry == target[j])
                continue;

            int existing = -1;
            for (int m = j + 1; m < _items.Count; m++)
            {
                if (_items[m].Entry == target[j]) { existing = m; break; }
            }

            if (existing >= 0)
                _items.Move(existing, j);          // reorder existing row
            else
                _items.Insert(j, new GitStatusItem(target[j]));   // brand-new row
            changed = true;
        }

        // 3. Trim any trailing leftovers (defensive — should be none after 1+2).
        while (_items.Count > target.Count)
        {
            _items.RemoveAt(_items.Count - 1);
            changed = true;
        }

        return changed;
    }

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not GitStatusItem item || _gitService == null)
            return;

        var gitService = _gitService;
        var entry = item.Entry;
        // Diffs can be large; fetch off the UI thread.
        var diff = await Task.Run(() => gitService.GetDiff(entry.FilePath, entry.IsStaged));
        if (diff != null)
            DiffRequested?.Invoke(entry.FilePath, diff);
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

public class BranchListItem(string name, bool isCurrent)
{
    public string Name { get; } = name;
    public bool IsCurrent { get; } = isCurrent;
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}
