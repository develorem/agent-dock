using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentDock.Models;
using AgentDock.Services;

namespace AgentDock.Windows;

/// <summary>
/// Collects what autopilot needs before it is armed: which sessions it manages, the window it runs
/// in, and whether that window reopens daily. Per-project prompts are edited from each repo's own
/// toolbar, not here — see <c>ProjectToolbar</c>.
/// </summary>
public partial class AutopilotDialog : Window
{
    /// <summary>One project offered for selection. <paramref name="HasSession"/> only drives the
    /// hint beside the name — a project whose session has not started yet can still be selected,
    /// because it may well be running by the time the window opens tonight.</summary>
    public sealed record ProjectChoice(string FolderPath, string DisplayName, bool HasSession);

    private readonly List<(ProjectChoice Choice, CheckBox Box)> _rows = [];
    private readonly List<TextBlock> _groupHeaders = [];
    private readonly Dictionary<string, string> _basePromptOverrides;

    public AutopilotSettings? Outcome { get; private set; }

    private AutopilotDialog(AutopilotSettings current, IReadOnlyList<ProjectChoice> projects)
    {
        InitializeComponent();

        // This dialog only sets the window and the roster — per-project prompts are edited from
        // each repo's own toolbar, so whatever is already saved here is carried forward untouched.
        _basePromptOverrides = new Dictionary<string, string>(current.PromptOverrides, StringComparer.OrdinalIgnoreCase);

        StartBox.Text = FormatMinutes(current.StartMinutes);
        EndBox.Text = FormatMinutes(current.EndMinutes);
        RepeatCheck.IsChecked = current.RepeatDaily;

        AddGroup("Active now", projects.Where(p => p.HasSession), current);
        AddGroup("Not running", projects.Where(p => !p.HasSession), current);

        UpdateWindowHint();
        UpdateStartEnabled();
    }

    private void AddGroup(string title, IEnumerable<ProjectChoice> projects, AutopilotSettings current)
    {
        var ordered = projects.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count == 0) return;

        var header = new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, _groupHeaders.Count == 0 ? 0 : 10, 0, 4),
        };
        header.SetResourceReference(ForegroundProperty, "HelpMutedForeground");
        _groupHeaders.Add(header);
        ProjectList.Items.Add(header);

        foreach (var project in ordered)
        {
            var box = new CheckBox
            {
                Content = project.HasSession
                    ? project.DisplayName
                    : $"{project.DisplayName}  (no session yet)",
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 2),
                IsChecked = current.ProjectPaths.Contains(project.FolderPath, StringComparer.OrdinalIgnoreCase),
            };
            box.SetResourceReference(ForegroundProperty, "HelpForeground");
            box.Checked += (_, _) => UpdateStartEnabled();
            box.Unchecked += (_, _) => UpdateStartEnabled();

            _rows.Add((project, box));
            ProjectList.Items.Add(box);
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();

        foreach (var (choice, box) in _rows)
            box.Visibility = filter.Length == 0
                || choice.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        // A header stays visible only while at least one row below it (up to the next header) is.
        for (var i = 0; i < _groupHeaders.Count; i++)
        {
            var header = _groupHeaders[i];
            var headerIndex = ProjectList.Items.IndexOf(header);
            var nextHeaderIndex = i + 1 < _groupHeaders.Count
                ? ProjectList.Items.IndexOf(_groupHeaders[i + 1])
                : ProjectList.Items.Count;

            var anyVisible = false;
            for (var j = headerIndex + 1; j < nextHeaderIndex; j++)
                if (ProjectList.Items[j] is CheckBox { Visibility: Visibility.Visible })
                {
                    anyVisible = true;
                    break;
                }

            header.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public static AutopilotSettings? Show(
        Window owner, AutopilotSettings current, IReadOnlyList<ProjectChoice> projects)
    {
        var dialog = new AutopilotDialog(current, projects) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Outcome : null;
    }

    private static string FormatMinutes(int minutes) =>
        $"{minutes / 60 % 24:00}:{minutes % 60:00}";

    /// <summary>Parsing lives in <see cref="AutopilotPolicy"/> so it is covered by tests — what
    /// counts as a valid time is a rule, not a detail of this dialog.</summary>
    private static bool TryParseMinutes(string text, out int minutes)
        => AutopilotPolicy.TryParseTimeOfDay(text, out minutes);

    private void Time_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateWindowHint();
        UpdateStartEnabled();
    }

    /// <summary>
    /// Spells the window back out in words, including its length. A user typing 23:00 and 04:00
    /// should be able to see at a glance that they asked for five hours and not nineteen, which is
    /// the mistake a wrapping window invites.
    /// </summary>
    private void UpdateWindowHint()
    {
        if (WindowHint == null) return;

        if (!TryParseMinutes(StartBox.Text, out var start) || !TryParseMinutes(EndBox.Text, out var end))
        {
            WindowHint.Text = "24-hour time, e.g. 23:00 and 04:00. A window that runs past midnight is fine.";
            return;
        }

        var length = end > start ? end - start : 1440 - start + end;
        if (start == end) length = 1440;

        var hours = length / 60;
        var minutes = length % 60;
        var duration = minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
        var wraps = end <= start && start != end ? ", running past midnight" : "";

        WindowHint.Text = $"Autopilot will run for {duration}{wraps}.";
    }

    private void UpdateStartEnabled()
    {
        if (StartButton == null) return;

        StartButton.IsEnabled =
            _rows.Any(r => r.Box.IsChecked == true)
            && TryParseMinutes(StartBox.Text, out _)
            && TryParseMinutes(EndBox.Text, out _);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseMinutes(StartBox.Text, out var start) || !TryParseMinutes(EndBox.Text, out var end))
        {
            ShowError("Enter both times as 24-hour times, for example 23:00 and 04:00.");
            return;
        }

        var selected = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Choice.FolderPath).ToList();
        if (selected.Count == 0)
        {
            ShowError("Select at least one session for autopilot to manage.");
            return;
        }

        Outcome = new AutopilotSettings
        {
            StartMinutes = start,
            EndMinutes = end,
            RepeatDaily = RepeatCheck.IsChecked == true,
            ProjectPaths = selected,
            PromptOverrides = _basePromptOverrides,
        };

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
