using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AgentDock.Services;

namespace AgentDock.Controls;

public partial class FileExplorerControl : UserControl
{
    /// <summary>
    /// Raised when a file is clicked for preview.
    /// </summary>
    public event Action<string>? FileSelected;

    private GitIgnoreFilter? _gitIgnoreFilter;
    private string _rootPath = string.Empty;

    public FileExplorerControl()
    {
        InitializeComponent();
    }

    public void LoadDirectory(string rootPath)
    {
        _rootPath = rootPath;
        _gitIgnoreFilter = new GitIgnoreFilter(rootPath);

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

    private void TreeViewItem_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileNode node } && !node.IsDirectory && node.FullPath != null)
        {
            FileSelected?.Invoke(node.FullPath);
        }
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

    public ObservableCollection<FileNode> Children { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
}
