using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AgentDock.Models;

/// <summary>
/// One message waiting in a chat's send queue (its "outbox"). Queued items are
/// dispatched to Claude in strict FIFO order — one after the previous turn returns —
/// which is what lets the user line up several tasks while the agent is busy.
///
/// <para><see cref="NotBeforeUtc"/> optionally gates an item behind a wall-clock time:
/// <c>null</c> means "send as soon as this reaches the head of the queue and the
/// session is idle" (a plain <b>queued</b> message); a value means "…and not before
/// this time" (a <b>scheduled</b> message). This single shape covers both features and
/// composes: a timed item followed by plain items runs the timed one first (when it
/// comes due), then the rest — e.g. "schedule for +1h, then queue a follow-up after
/// it returns".</para>
///
/// Implements INPC only for <see cref="StatusText"/>, the live "Queued / Scheduled ·
/// 04:59" label the queue panel row shows; everything else is set once at enqueue.
/// </summary>
public sealed class QueuedMessage : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();

    public required string Text { get; init; }

    /// <summary>Image payloads to send with the message; empty for text-only.</summary>
    public IReadOnlyList<ImageAttachment> Attachments { get; init; } = [];

    /// <summary>Frozen thumbnails for the sent user bubble; null when text-only.</summary>
    public IReadOnlyList<ImageSource>? Thumbnails { get; init; }

    /// <summary>UTC time before which this must not be sent; null = send when it
    /// reaches the head of the queue.</summary>
    public DateTime? NotBeforeUtc { get; init; }

    public bool IsScheduled => NotBeforeUtc.HasValue;

    /// <summary>Single-line, length-capped preview of the message for the queue row.</summary>
    public string Preview => BuildPreview(Text);

    private string _statusText = "";
    /// <summary>Live status shown in the queue panel ("Queued", "Scheduled · 04:59",
    /// "Scheduled · due"). Refreshed by the control's per-second queue timer.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Recomputes <see cref="StatusText"/> for the current time.</summary>
    public void RefreshStatus(DateTime nowUtc)
    {
        if (NotBeforeUtc is DateTime fireAt)
        {
            var remaining = fireAt - nowUtc;
            StatusText = remaining > TimeSpan.Zero
                ? $"Scheduled · {FormatCountdown(remaining)}"
                : "Scheduled · due";
        }
        else
        {
            StatusText = "Queued";
        }
    }

    private static string FormatCountdown(TimeSpan remaining)
        => remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";

    private static string BuildPreview(string text)
    {
        // Collapse to one line so a multi-line draft doesn't blow out the row height.
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const int cap = 100;
        return oneLine.Length <= cap ? oneLine : oneLine[..cap] + "…";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
