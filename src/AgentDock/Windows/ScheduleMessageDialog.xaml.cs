using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AgentDock.Windows;

/// <summary>
/// Two-mode dialog for the chat "schedule a message" feature.
///
/// <para><b>Compose</b> mode (<see cref="ShowCompose"/>) shows the drafted message
/// and an hours/minutes delay (default 1 hour); the user clicks Schedule or Cancel.</para>
///
/// <para><b>Manage</b> mode (<see cref="ShowManage"/>) is opened by clicking the clock
/// while a message is already scheduled: it shows the message and a live countdown,
/// and lets the user cancel the pending send.</para>
/// </summary>
public partial class ScheduleMessageDialog : Window
{
    /// <summary>The chosen delay when the user clicks Schedule in compose mode.</summary>
    public TimeSpan Delay { get; private set; }

    /// <summary>True when the user clicked "Cancel Schedule" in manage mode.</summary>
    public bool CancelRequested { get; private set; }

    private DispatcherTimer? _countdownTimer;
    private DateTime _fireAtUtc;

    private ScheduleMessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the compose dialog. Returns the chosen delay, or null if cancelled.
    /// </summary>
    public static TimeSpan? ShowCompose(Window owner, string messageText)
    {
        var dialog = new ScheduleMessageDialog { Owner = owner };
        dialog.MessagePreview.Text = messageText;
        dialog.HoursBox.Text = "1";
        dialog.MinutesBox.Text = "0";
        dialog.UpdateComposeHint();
        dialog.Loaded += (_, _) =>
        {
            dialog.HoursBox.Focus();
            dialog.HoursBox.SelectAll();
        };
        return dialog.ShowDialog() == true ? dialog.Delay : null;
    }

    /// <summary>
    /// Shows the manage dialog for an already-scheduled message. Returns true if the
    /// user asked to cancel the schedule.
    /// </summary>
    public static bool ShowManage(Window owner, string messageText, DateTime fireAtUtc)
    {
        var dialog = new ScheduleMessageDialog { Owner = owner };
        dialog.Title = "Scheduled Message";
        dialog.TitleText.Text = "Scheduled Message";
        dialog.MessagePreview.Text = messageText;
        dialog._fireAtUtc = fireAtUtc;

        dialog.ComposeSection.Visibility = Visibility.Collapsed;
        dialog.ComposeButtons.Visibility = Visibility.Collapsed;
        dialog.ManageSection.Visibility = Visibility.Visible;
        dialog.ManageButtons.Visibility = Visibility.Visible;
        dialog.FireAtText.Text = $"Scheduled for {fireAtUtc.ToLocalTime():t}";

        dialog.StartCountdown();
        dialog.ShowDialog();
        return dialog.CancelRequested;
    }

    // --- Compose mode ---

    private static readonly Regex NonDigit = new("[^0-9]", RegexOptions.Compiled);

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = NonDigit.IsMatch(e.Text);

    private void Duration_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposeHint();

    private (int hours, int minutes) ParseDuration()
    {
        int.TryParse(HoursBox.Text, out var hours);
        int.TryParse(MinutesBox.Text, out var minutes);
        // Normalise overflowing minutes into hours so "90 minutes" reads sensibly.
        hours += minutes / 60;
        minutes %= 60;
        return (hours, minutes);
    }

    private void UpdateComposeHint()
    {
        if (ComposeHint == null) return;
        var (hours, minutes) = ParseDuration();
        var total = new TimeSpan(hours, minutes, 0);
        if (total <= TimeSpan.Zero)
        {
            ComposeHint.Text = "Enter a delay of at least one minute.";
            return;
        }
        var fireAt = DateTime.Now + total;
        ComposeHint.Text = $"Will send at {fireAt:t} ({DescribeDuration(total)} from now).";
    }

    private static string DescribeDuration(TimeSpan span)
    {
        var parts = new List<string>();
        if (span.Hours > 0 || span.Days > 0)
            parts.Add($"{(int)span.TotalHours}h");
        if (span.Minutes > 0)
            parts.Add($"{span.Minutes}m");
        return parts.Count > 0 ? string.Join(" ", parts) : "0m";
    }

    private void ScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var (hours, minutes) = ParseDuration();
        var total = new TimeSpan(hours, minutes, 0);
        if (total <= TimeSpan.Zero)
        {
            ComposeHint.Text = "Enter a delay of at least one minute.";
            HoursBox.Focus();
            return;
        }
        Delay = total;
        DialogResult = true;
    }

    // --- Manage mode ---

    private void StartCountdown()
    {
        UpdateRemaining();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => UpdateRemaining();
        _countdownTimer.Start();
    }

    private void UpdateRemaining()
    {
        var remaining = _fireAtUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            RemainingText.Text = "Sending now…";
            return;
        }
        RemainingText.Text = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
    }

    private void CancelScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested = true;
        DialogResult = false;
    }

    // --- Shared ---

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    protected override void OnClosed(EventArgs e)
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
        base.OnClosed(e);
    }
}
