using System.Windows;
using System.Windows.Controls;
using AgentDock.Services;

namespace AgentDock.Controls;

public partial class ProjectDescriptionControl : UserControl
{
    /// <summary>
    /// Raised when the user clicks the settings icon to edit the description.
    /// MainWindow handles this to open the ProjectSettingsDialog.
    /// </summary>
    public event Action? OpenSettingsRequested;

    private const double DefaultFontSize = 13;
    private const double MinFontSize = 8;
    private const double MaxFontSize = 36;
    private const double FontSizeStep = 2;

    private string _projectPath = "";

    public ProjectDescriptionControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Loads the description for the given project folder from settings.
    /// </summary>
    public void LoadProject(string projectPath)
    {
        _projectPath = projectPath;
        var settings = ProjectSettingsManager.Load(projectPath);
        SetDescription(settings.Description);
        ApplyFontSize(settings.DescriptionFontSize ?? DefaultFontSize);
    }

    /// <summary>
    /// Updates the displayed description.
    /// Used for external sync (e.g. when ProjectSettingsDialog changes the description).
    /// </summary>
    public void SetDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            DescriptionText.Visibility = Visibility.Collapsed;
            PlaceholderText.Visibility = Visibility.Visible;
        }
        else
        {
            DescriptionText.Text = description;
            DescriptionText.Visibility = Visibility.Visible;
            PlaceholderText.Visibility = Visibility.Collapsed;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke();
    }

    private void IncreaseFontSize_Click(object sender, RoutedEventArgs e)
    {
        var newSize = Math.Min(DescriptionText.FontSize + FontSizeStep, MaxFontSize);
        ApplyFontSize(newSize);
        SaveFontSize(newSize);
    }

    private void DecreaseFontSize_Click(object sender, RoutedEventArgs e)
    {
        var newSize = Math.Max(DescriptionText.FontSize - FontSizeStep, MinFontSize);
        ApplyFontSize(newSize);
        SaveFontSize(newSize);
    }

    private void ApplyFontSize(double size)
    {
        DescriptionText.FontSize = size;
        PlaceholderText.FontSize = size;
    }

    private void SaveFontSize(double size)
    {
        var valueToStore = Math.Abs(size - DefaultFontSize) < 0.01 ? (double?)null : size;
        ProjectSettingsManager.Update(_projectPath, s => s.DescriptionFontSize = valueToStore);
    }
}
