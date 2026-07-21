using AgentDock.Models;
using AgentDock.Services;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Tests for <see cref="SendQueue"/> — the pure ordering/timing core of the chat
/// outbox. Covers strict-FIFO dispatch eligibility (<see cref="SendQueue.PeekReady"/>),
/// time-gated (scheduled) items, and the tab-strip clock feed.
///
/// HOW TO ADD A CASE: if the queue dispatches in the wrong order or at the wrong time,
/// reproduce it here with fixed UTC times (never DateTime.UtcNow — keep it deterministic).
/// </summary>
public class SendQueueTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static QueuedMessage Msg(string text, DateTime? notBefore = null)
        => new() { Text = text, NotBeforeUtc = notBefore };

    [Fact]
    public void PeekReady_EmptyQueue_ReturnsNull()
    {
        var q = new SendQueue();
        Assert.Null(q.PeekReady(T0));
    }

    [Fact]
    public void PeekReady_PlainMessage_IsImmediatelyReady()
    {
        var q = new SendQueue();
        q.Enqueue(Msg("a"));
        Assert.Equal("a", q.PeekReady(T0)?.Text);
    }

    [Fact]
    public void PeekReady_ScheduledInFuture_NotReadyUntilDue()
    {
        var q = new SendQueue();
        q.Enqueue(Msg("later", T0.AddMinutes(10)));

        Assert.Null(q.PeekReady(T0));                       // before fire time
        Assert.Null(q.PeekReady(T0.AddMinutes(9)));         // still before
        Assert.Equal("later", q.PeekReady(T0.AddMinutes(10))?.Text); // exactly due
        Assert.Equal("later", q.PeekReady(T0.AddMinutes(11))?.Text); // past due
    }

    [Fact]
    public void PeekReady_StrictFifo_NotYetDueHeadBlocksImmediateItemBehindIt()
    {
        var q = new SendQueue();
        q.Enqueue(Msg("scheduled", T0.AddHours(1))); // head: not due for an hour
        q.Enqueue(Msg("queued"));                    // behind it: no time gate

        // The plain "queued" message must NOT jump ahead — the head gates everything.
        Assert.Null(q.PeekReady(T0));

        // Once the head is due, it's the one that dispatches first.
        Assert.Equal("scheduled", q.PeekReady(T0.AddHours(1))?.Text);
    }

    [Fact]
    public void DequeueHead_PopsInInsertionOrder()
    {
        var q = new SendQueue();
        q.Enqueue(Msg("first"));
        q.Enqueue(Msg("second"));

        Assert.Equal("first", q.DequeueHead()?.Text);
        Assert.Equal("second", q.DequeueHead()?.Text);
        Assert.Null(q.DequeueHead());
        Assert.True(q.IsEmpty);
    }

    [Fact]
    public void Remove_ById_RemovesTheRightItem()
    {
        var q = new SendQueue();
        var a = Msg("a");
        var b = Msg("b");
        q.Enqueue(a);
        q.Enqueue(b);

        Assert.True(q.Remove(a.Id));
        Assert.Equal(1, q.Count);
        Assert.Equal("b", q.Items[0].Text);
        Assert.False(q.Remove(a.Id)); // already gone
    }

    [Fact]
    public void Changed_FiresOnEnqueueRemoveDequeueAndClear()
    {
        var q = new SendQueue();
        var count = 0;
        q.Changed += () => count++;

        var m = Msg("a");
        q.Enqueue(m);      // 1
        q.DequeueHead();   // 2
        q.Enqueue(m);      // 3
        q.Remove(m.Id);    // 4
        q.Enqueue(m);      // 5
        q.Clear();         // 6

        Assert.Equal(6, count);
    }

    [Fact]
    public void Clear_OnEmptyQueue_DoesNotFireChanged()
    {
        var q = new SendQueue();
        var fired = false;
        q.Changed += () => fired = true;
        q.Clear();
        Assert.False(fired);
    }

    [Fact]
    public void NextScheduledFireUtc_ReturnsEarliestFutureTime()
    {
        var q = new SendQueue();
        q.Enqueue(Msg("plain"));                       // no time gate
        q.Enqueue(Msg("late", T0.AddHours(2)));
        q.Enqueue(Msg("soon", T0.AddMinutes(30)));

        Assert.Equal(T0.AddMinutes(30), q.NextScheduledFireUtc);
    }

    [Fact]
    public void NextScheduledFireUtc_NullWhenNothingScheduled()
    {
        var q = new SendQueue();
        q.Enqueue(Msg("a"));
        q.Enqueue(Msg("b"));
        Assert.Null(q.NextScheduledFireUtc);
    }

    [Fact]
    public void RefreshStatus_PlainMessage_ReadsQueued()
    {
        var m = Msg("a");
        m.RefreshStatus(T0);
        Assert.Equal("Queued", m.StatusText);
    }

    [Fact]
    public void RefreshStatus_ScheduledMessage_ShowsCountdownThenDue()
    {
        var m = Msg("a", T0.AddMinutes(5));

        m.RefreshStatus(T0);
        Assert.Equal("Scheduled · 05:00", m.StatusText);

        m.RefreshStatus(T0.AddMinutes(5));
        Assert.Equal("Scheduled · due", m.StatusText);
    }

    [Fact]
    public void RefreshStatus_LongDelay_ShowsHours()
    {
        var m = Msg("a", T0.AddHours(1).AddMinutes(2).AddSeconds(3));
        m.RefreshStatus(T0);
        Assert.Equal("Scheduled · 01:02:03", m.StatusText);
    }

    [Fact]
    public void Preview_CollapsesNewlinesAndTruncates()
    {
        var m = Msg("line one\nline two");
        Assert.Equal("line one line two", m.Preview);

        var longText = Msg(new string('x', 200));
        Assert.Equal(101, longText.Preview.Length); // 100 chars + ellipsis
        Assert.EndsWith("…", longText.Preview);
    }
}
