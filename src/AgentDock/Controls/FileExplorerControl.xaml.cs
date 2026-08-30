using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentDock.Models;
using AgentDock.Services;

using AgentDock.Services.Abstractions;

namespace AgentDock.Controls;

public partial class FileExplorerControl : UserControl
{
    /// <summary>
    /// Raised when a file is clicked for preview.
    /// </summary>
    public event Action<string>? FileSelected;

    /// <summary>
    /// Raised when the user changes project settings (icon, colours, etc.) via the settings dialog.
    /// </summary>
    public event Action? ProjectSettingsChanged;

    private GitIgnoreFilter? _gitIgnoreFilter;
    private string _rootPath = string.Empty;

    /// <summary>
    /// The root directory path loaded into this explorer.
    /// </summary>
    public string RootPath => _rootPath;

    /// <summary>
    /// Global set of available tool names (e.g. "VS Code", "Cursor", "Visual Studio").
    /// Populated from prerequisite check results on startup.
    /// </summary>
    public static HashSet<string> AvailableTools { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly IProjectSettingsStore _projectSettings;
    private readonly IThemeService _theme;

    public FileExplorerControl(
        IProjectSettingsStore projectSettings,
        IThemeService theme)
    {
        _projectSettings = projectSettings;
        _theme = theme;

        InitializeComponent();
    }

    public void LoadDirectory(string rootPath)
    {
        _rootPath = rootPath;
        _gitIgnoreFilter = new GitIgnoreFilter(rootPath);

        // Toggle VS Code toolbar button visibility
        VsCodeButton.Visibility = AvailableTools.Contains("VS Code")
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Bound to an observable collection so the tree can be patched rather than rebuilt.
        FileTree.ItemsSource = _rootNodes;
        SyncChildren(_rootNodes, BuildLocalChildren(rootPath));
    }

    /// <summary>
    /// Gets the folder name for use in panel titles.
    /// </summary>
    public string FolderName => Path.GetFileName(_rootPath) ?? _rootPath;

    /// <summary>
    /// Expands the tree to <paramref name="absolutePath"/>, selects the matching node,
    /// and raises <see cref="FileSelected"/> if it's a file. The path must be inside
    /// the loaded root and exist on disk; otherwise this is a no-op.
    /// </summary>
    public void RevealAndSelect(string absolutePath)
    {
        if (string.IsNullOrEmpty(_rootPath) || string.IsNullOrEmpty(absolutePath))
            return;

        string rootFull, targetFull;
        try
        {
            rootFull = Path.GetFullPath(_rootPath);
            targetFull = Path.GetFullPath(absolutePath);
        }
        catch { return; }

        if (!File.Exists(targetFull) && !Directory.Exists(targetFull))
            return;

        if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return;
        if (targetFull.Length == rootFull.Length)
            return; // path is the root itself

        var relative = Path.GetRelativePath(rootFull, targetFull);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;

        IEnumerable<FileNode> currentLevel = FileTree.Items.OfType<FileNode>();
        FileNode? targetNode = null;

        for (var i = 0; i < segments.Length; i++)
        {
            var match = currentLevel.FirstOrDefault(n =>
                string.Equals(n.Name, segments[i], StringComparison.OrdinalIgnoreCase));
            if (match == null) return;

            var isLast = i == segments.Length - 1;
            if (!isLast)
            {
                if (!match.IsDirectory) return;
                if (match.Children.Count == 1 && match.Children[0].FullPath == null)
                    LoadChildren(match);
                match.IsExpanded = true;
                match.Icon = "📂"; // open folder
                currentLevel = match.Children;
            }
            else
            {
                targetNode = match;
            }
        }

        if (targetNode == null) return;

        targetNode.IsSelected = true;

        // Scroll the materialized container into view once the TreeView has caught up.
        var captured = targetNode;
        Dispatcher.BeginInvoke(() => BringNodeIntoView(captured), DispatcherPriority.Background);

        if (!targetNode.IsDirectory && targetNode.FullPath != null)
            FileSelected?.Invoke(targetNode.FullPath);
    }

    private void BringNodeIntoView(FileNode node)
    {
        var tvi = FindContainer(FileTree, FileTree.Items, node);
        tvi?.BringIntoView();
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, ItemCollection items, FileNode target)
    {
        foreach (var obj in items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(obj) is not TreeViewItem tvi)
                continue;
            if (ReferenceEquals(obj, target))
                return tvi;
            if (obj is FileNode n && n.IsDirectory && n.IsExpanded)
            {
                var found = FindContainer(tvi, tvi.Items, target);
                if (found != null) return found;
            }
        }
        return null;
    }
    /// <summary>
    /// Re-reads the tree in place. Patches rather than rebuilding, so expansion state,
    /// selection and scroll position survive — the collect-rebuild-restore version of this
    /// flickered every time the agent touched a file, since it is wired to the git watcher.
    /// </summary>
    public void Refresh() => RefreshIncremental();

    /// <summary>
    /// Materializes one directory's children, replacing the placeholder node. Delegates to the
    /// same builder and patcher the incremental refresh uses, so there is one definition of
    /// what a directory's contents look like.
    /// </summary>
    private void LoadChildren(FileNode parentNode)
    {
        if (parentNode.FullPath == null) return;
        SyncChildren(parentNode.Children, BuildLocalChildren(parentNode.FullPath));
    }

    private bool IsIgnored(string fullPath, bool isDirectory)
    {
        if (_gitIgnoreFilter == null)
            return false;

        var relativePath = Path.GetRelativePath(_rootPath, fullPath);
        return _gitIgnoreFilter.IsIgnored(relativePath, isDirectory);
    }

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem treeViewItem)
            return;

        if (treeViewItem.DataContext is not FileNode node || !node.IsDirectory)
            return;

        // Check if children are just the dummy "Loading..." node
        // Children are still the placeholder — materialize them, from the server when remote.
        if (node.Children.Count == 1 && node.Children[0].FullPath == null)
        {
            if (!TryExpandRemote(node))
                LoadChildren(node);
        }

        node.Icon = "\uD83D\uDCC2"; // open folder

        e.Handled = true; // prevent bubbling to parent
    }

    private void TreeViewItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileNode node } && !node.IsDirectory && node.FullPath != null)
        {
            FileSelected?.Invoke(node.FullPath);
        }
    }

    private void TreeViewItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: FileNode node } || node.FullPath == null)
            return;

        // Select the right-clicked item
        if (sender is TreeViewItem tvi)
            tvi.IsSelected = true;

        var menu = new ContextMenu();
        var ext = Path.GetExtension(node.FullPath).ToLowerInvariant();

        if (node.IsDirectory)
        {
            // Folder context menu
            if (AvailableTools.Contains("VS Code"))
                menu.Items.Add(MakeMenuItem("Open in VS Code", "\uE70F", () => LaunchTool("code", node.FullPath)));

            if (AvailableTools.Contains("Cursor"))
                menu.Items.Add(MakeMenuItem("Open in Cursor", "\uE70F", () => LaunchTool("cursor", node.FullPath)));

            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            menu.Items.Add(MakeMenuItem("Open in Explorer", "\uED25", () => OpenInExplorer(node.FullPath)));
            menu.Items.Add(MakeMenuItem("Open Command Line", "\uE756", () => OpenCommandLine(node.FullPath)));
        }
        else
        {
            // File context menu
            if (ext is ".sln" or ".slnx")
            {
                if (AvailableTools.Contains("Visual Studio"))
                {
                    menu.Items.Add(MakeMenuItem("Open in Visual Studio", "\u2699", () => LaunchFile(node.FullPath)));
                }
            }

            if (AvailableTools.Contains("VS Code"))
                menu.Items.Add(MakeMenuItem("Open in VS Code", "\uE70F", () => LaunchTool("code", node.FullPath)));

            if (AvailableTools.Contains("Cursor"))
                menu.Items.Add(MakeMenuItem("Open in Cursor", "\uE70F", () => LaunchTool("cursor", node.FullPath)));

            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            menu.Items.Add(MakeMenuItem("Open in Explorer", "\uED25",
                () => OpenInExplorer(Path.GetDirectoryName(node.FullPath)!)));
        }

        if (menu.Items.Count > 0)
        {
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    // --- Toolbar Button Handlers ---

    private void OpenInVsCode_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_rootPath))
            LaunchTool("code", _rootPath);
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_rootPath))
            OpenInExplorer(_rootPath);
    }

    private void OpenInConsole_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_rootPath))
            OpenCommandLine(_rootPath);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rootPath))
            return;

        var owner = Window.GetWindow(this);
        if (owner == null)
            return;

        var result = Windows.ProjectSettingsDialog.Show(owner, _rootPath, _projectSettings);

        if (result != null)
        {
            _projectSettings.Update(_rootPath, s =>
            {
                s.Name = result.Name;
                s.Icon = result.Icon;
                s.IconColor = result.IconColor;
                s.Description = result.Description;
                s.SoundOnSessionStart = result.SoundOnSessionStart;
                s.SoundOnAgentWaiting = result.SoundOnAgentWaiting;
                s.SoundOnSessionEnd = result.SoundOnSessionEnd;
            });
            ProjectSettingsChanged?.Invoke();
        }
    }

    private static MenuItem MakeMenuItem(string header, string iconGlyph, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Icon = new TextBlock
        {
            Text = iconGlyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MenuIconForeground")
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static void LaunchTool(string command, string path)
    {
        try
        {
            // UseShellExecute = true ensures the launched process is fully
            // independent — no parent-child window relationship that could
            // steal foreground activation rights from AgentDock.
            Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { /* tool not available */ }
    }

    private static void LaunchFile(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch { /* failed to open */ }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Directory.Exists(path) ? path : $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { /* failed */ }
    }

    private static void OpenCommandLine(string folderPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                WorkingDirectory = folderPath,
                UseShellExecute = true
            });
        }
        catch { /* failed */ }
    }

    private static string GetFileIcon(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "\uD83D\uDCDD",       // code file
            ".csproj" or ".sln" or ".slnx" => "\u2699\uFE0F", // gear
            ".json" => "\uD83D\uDCCB",     // clipboard
            ".xml" or ".xaml" => "\uD83D\uDCCB",
            ".md" => "\uD83D\uDCD6",       // book
            ".txt" or ".log" => "\uD83D\uDCC4", // page
            ".js" or ".ts" or ".jsx" or ".tsx" => "\uD83D\uDCDD",
            ".py" => "\uD83D\uDCDD",
            ".html" or ".htm" => "\uD83C\uDF10", // globe
            ".css" or ".scss" or ".less" => "\uD83C\uDFA8", // palette
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".bmp" or ".ico" => "\uD83D\uDDBC\uFE0F", // image
            ".yml" or ".yaml" => "\uD83D\uDCCB",
            ".sh" or ".bat" or ".cmd" or ".ps1" => "\u26A1",  // terminal
            ".gitignore" or ".editorconfig" => "\u2699\uFE0F",
            _ => "\uD83D\uDCC4" // generic page
        };
    }
}

public class FileNode(IThemeService theme) : INotifyPropertyChanged
{
    private string _icon = "";
    private string _name = "";
    private bool _isExpanded;
    private bool _isSelected;

    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
    }

    public string? FullPath { get; set; }

    public bool IsDirectory { get; set; }

    public string Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    /// <summary>
    /// Returns a themed foreground brush: folder-yellow for directories, normal for files.
    /// </summary>
    public Brush IconForeground => IsDirectory
        ? theme.GetBrush("ExplorerFolderIconForeground")
        : theme.GetBrush("ExplorerForeground");

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public ObservableCollection<FileNode> Children { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
}
