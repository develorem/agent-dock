using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentDock.Models;
using AgentDock.Services;

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

    public FileExplorerControl()
    {
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

        FileTree.Items.Clear();

        // Load root contents directly — don't show the root folder itself
        var rootNode = new FileNode
        {
            Name = Path.GetFileName(rootPath) ?? rootPath,
            FullPath = rootPath,
            IsDirectory = true
        };

        LoadChildren(rootNode);

        foreach (var child in rootNode.Children)
            FileTree.Items.Add(child);
    }

    /// <summary>
    /// Gets the folder name for use in panel titles.
    /// </summary>
    public string FolderName => Path.GetFileName(_rootPath) ?? _rootPath;

    /// <summary>
    /// Refreshes the file tree while preserving expanded folder state.
    /// </summary>
    public void Refresh()
    {
        if (string.IsNullOrEmpty(_rootPath))
            return;

        var expandedPaths = CollectExpandedPaths();
        LoadDirectory(_rootPath);
        RestoreExpandedState(expandedPaths);
    }

    private HashSet<string> CollectExpandedPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileNode node in FileTree.Items)
            CollectExpandedRecursive(node, paths);
        return paths;
    }

    private void CollectExpandedRecursive(FileNode node, HashSet<string> paths)
    {
        if (!node.IsDirectory || node.FullPath == null || !node.IsExpanded)
            return;

        paths.Add(node.FullPath);
        foreach (var child in node.Children)
            CollectExpandedRecursive(child, paths);
    }

    private void RestoreExpandedState(HashSet<string> expandedPaths)
    {
        if (expandedPaths.Count == 0)
            return;

        foreach (FileNode node in FileTree.Items)
            RestoreExpandedRecursive(node, expandedPaths);
    }

    private void RestoreExpandedRecursive(FileNode node, HashSet<string> expandedPaths)
    {
        if (!node.IsDirectory || node.FullPath == null)
            return;
        if (!expandedPaths.Contains(node.FullPath))
            return;

        // Load real children (replacing dummy "Loading..." node)
        if (node.Children.Count == 1 && node.Children[0].FullPath == null)
            LoadChildren(node);

        node.IsExpanded = true;
        node.Icon = "\uD83D\uDCC2"; // open folder

        foreach (var child in node.Children)
            RestoreExpandedRecursive(child, expandedPaths);
    }

    private void LoadChildren(FileNode parentNode)
    {
        parentNode.Children.Clear();

        try
        {
            // Directories first, then files, both alphabetical
            var dirInfo = new DirectoryInfo(parentNode.FullPath!);

            var directories = dirInfo.GetDirectories()
                .Where(d => !IsIgnored(d.FullName, isDirectory: true))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var dir in directories)
            {
                var dirNode = new FileNode
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    IsDirectory = true,
                    Icon = "\uD83D\uDCC1" // closed folder
                };

                // Add a dummy child so the expand arrow shows
                dirNode.Children.Add(new FileNode { Name = "Loading...", Icon = "" });
                parentNode.Children.Add(dirNode);
            }

            var files = dirInfo.GetFiles()
                .Where(f => !IsIgnored(f.FullName, isDirectory: false))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                parentNode.Children.Add(new FileNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    Icon = GetFileIcon(file.Extension)
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (IOException)
        {
            // Skip on IO errors
        }
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
        if (node.Children.Count == 1 && node.Children[0].FullPath == null)
        {
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

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rootPath))
            return;

        var owner = Window.GetWindow(this);
        if (owner == null)
            return;

        var result = Windows.ProjectSettingsDialog.Show(owner, _rootPath);

        if (result != null)
        {
            ProjectSettingsManager.Save(_rootPath, result);
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
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command} \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true
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

public class FileNode : INotifyPropertyChanged
{
    private string _icon = "";
    private string _name = "";
    private bool _isExpanded;

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

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }

    public ObservableCollection<FileNode> Children { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
}
