using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>
    /// Raised when the file system watcher detects changes (debounced).
    /// </summary>
    public event Action? FileSystemChanged;

    private GitService? _gitService;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounceTimer;
    private List<string> _allBranches = [];

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
            BranchPanel.Visibility = Visibility.Collapsed;
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

    private void RefreshBranch()
    {
        var branch = _gitService?.GetCurrentBranch();
        if (!string.IsNullOrEmpty(branch))
        {
            BranchName.Text = branch;
            BranchPanel.Visibility = Visibility.Visible;
        }
        else
        {
            BranchPanel.Visibility = Visibility.Collapsed;
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

    public void RefreshStatus()
    {
        if (_gitService == null)
            return;

        RefreshBranch();

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

        // Skip rebuild if the list hasn't actually changed — avoids killing
        // the current selection and hover state on every debounce tick
        if (FileList.ItemsSource is List<GitStatusItem> current && EntriesMatch(current, viewItems))
        {
            return;
        }

        // Preserve the selected file across the rebuild
        var selectedPath = (FileList.SelectedItem as GitStatusItem)?.Entry;

        FileList.ItemsSource = viewItems;

        if (selectedPath != null)
        {
            var match = viewItems.FirstOrDefault(v =>
                v.Entry.FilePath == selectedPath.FilePath &&
                v.Entry.IsStaged == selectedPath.IsStaged);
            if (match != null)
                FileList.SelectedItem = match;
        }

        FileSystemChanged?.Invoke();
    }

    /// <summary>
    /// Returns true when both lists contain the same entries in the same order.
    /// </summary>
    private static bool EntriesMatch(List<GitStatusItem> a, List<GitStatusItem> b)
    {
        if (a.Count != b.Count)
            return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Entry != b[i].Entry)   // GitFileEntry is a record — value equality
                return false;
        }

        return true;
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

public class BranchListItem(string name, bool isCurrent)
{
    public string Name { get; } = name;
    public bool IsCurrent { get; } = isCurrent;
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}
