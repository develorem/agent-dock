using System.Collections.ObjectModel;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// A chat's outbox: an ordered list of messages waiting to be sent to Claude. This is
/// the pure ordering/timing core — no WPF, no timers, no dispatch. The
/// <c>AiChatControl</c> owns the timer that ticks it, the session-idle check, and the
/// actual send + bubble creation; keeping the rules here makes them unit-testable
/// (see <c>SendQueueTests</c>).
///
/// <para><b>Semantics — strict FIFO.</b> Only the head is ever eligible. The head is
/// dispatchable when the session is idle (caller's responsibility) and its
/// <see cref="QueuedMessage.NotBeforeUtc"/> — if any — has passed. A not-yet-due
/// scheduled item therefore holds everything behind it: insertion order is execution
/// order. That is what makes "schedule A for later, then queue B behind it" run A then
/// B, rather than letting B jump ahead.</para>
///
/// <para><see cref="Items"/> is an <see cref="ObservableCollection{T}"/> so the queue
/// panel can bind to it directly; mutate only on the UI thread (all callers do).</para>
/// </summary>
public sealed class SendQueue
{
    public ObservableCollection<QueuedMessage> Items { get; } = [];

    public int Count => Items.Count;
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Raised whenever the queue's contents change (enqueue / remove / clear /
    /// dequeue) so the host can refresh panel visibility and the tab-strip indicator.</summary>
    public event Action? Changed;

    public void Enqueue(QueuedMessage message)
    {
        Items.Add(message);
        Changed?.Invoke();
    }

    public bool Remove(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        Items.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (Items.Count == 0) return;
        Items.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Returns the head message if it is ready to dispatch at <paramref name="nowUtc"/>
    /// (no time gate, or the gate has passed), else null. Does NOT check session state
    /// and does NOT remove the item — the caller verifies the session is idle and then
    /// calls <see cref="DequeueHead"/> once it has actually dispatched.
    /// </summary>
    public QueuedMessage? PeekReady(DateTime nowUtc)
    {
        if (Items.Count == 0) return null;
        var head = Items[0];
        if (head.NotBeforeUtc is DateTime fireAt && nowUtc < fireAt) return null;
        return head;
    }

    /// <summary>Removes and returns the head, or null when empty.</summary>
    public QueuedMessage? DequeueHead()
    {
        if (Items.Count == 0) return null;
        var head = Items[0];
        Items.RemoveAt(0);
        Changed?.Invoke();
        return head;
    }

    /// <summary>
    /// The earliest future scheduled fire time in the queue, or null when nothing is
    /// time-gated in the future. Drives the tab-strip clock glyph (a scheduled item is
    /// the only thing that persists at idle — plain queued items drain immediately).
    /// </summary>
    public DateTime? NextScheduledFireUtc
    {
        get
        {
            DateTime? earliest = null;
            foreach (var m in Items)
                if (m.NotBeforeUtc is DateTime t && (earliest is null || t < earliest))
                    earliest = t;
            return earliest;
        }
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].Id == id) return i;
        return -1;
    }
}
