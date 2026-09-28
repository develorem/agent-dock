using AgentDock.Models;
using AgentDock.Services;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// When autopilot fires, and when it refuses to. The tracker is clock-injected precisely so this
/// can be asserted at a desk rather than discovered overnight.
/// </summary>
public class AutopilotTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc);

    /// <summary>A session at rest with no tool use and no human activity yet.</summary>
    private static AutopilotSnapshot AtRest(long tools = 0, long human = 0) => new(true, tools, human);

    private static AutopilotSnapshot Busy(long tools = 0, long human = 0) => new(false, tools, human);

    /// <summary>Past the dwell, so the only thing under test is whatever the caller varies.</summary>
    private static DateTime AfterDwell => T0 + AutopilotPolicy.IdleDwell + TimeSpan.FromSeconds(1);

    [Fact]
    public void DoesNotNudgeBeforeTheDwellHasPassed()
    {
        // A turn that has just finished is the moment a watching user starts reading it. Talking
        // over them is a worse first impression than a minute's wait.
        var tracker = new AutopilotTracker();

        Assert.False(tracker.Evaluate(AtRest(), T0, AutopilotBudget.Large));
        Assert.False(tracker.Evaluate(AtRest(), T0.AddSeconds(30), AutopilotBudget.Large));
        Assert.Equal(AutopilotHold.Settling, tracker.LastHold);

        Assert.True(tracker.Evaluate(AtRest(), AfterDwell, AutopilotBudget.Large));
    }

    [Fact]
    public void NeverNudgesASessionThatIsNotAtRest()
    {
        // IsAtRest folds in everything that must hold: mid-turn, subagents still draining, a
        // permission prompt, a pending question, a non-empty queue.
        var tracker = new AutopilotTracker();

        Assert.False(tracker.Evaluate(Busy(), T0, AutopilotBudget.Large));
        Assert.False(tracker.Evaluate(Busy(), AfterDwell, AutopilotBudget.Large));
        Assert.Equal(AutopilotHold.Busy, tracker.LastHold);
    }

    [Fact]
    public void BecomingBusyRestartsTheDwell()
    {
        // Otherwise a session that flickered at rest would bank dwell time it never actually spent
        // idle, and get nudged the instant it next paused.
        var tracker = new AutopilotTracker();

        tracker.Evaluate(AtRest(), T0, AutopilotBudget.Large);
        tracker.Evaluate(Busy(), T0.AddSeconds(30), AutopilotBudget.Large);

        Assert.False(tracker.Evaluate(AtRest(), AfterDwell, AutopilotBudget.Large));
    }

    [Fact]
    public void TypingInTheComposerPushesThePendingNudgeBack()
    {
        var tracker = new AutopilotTracker();
        tracker.Evaluate(AtRest(), T0, AutopilotBudget.Large);

        tracker.CancelPendingNudge();

        Assert.False(tracker.Evaluate(AtRest(), AfterDwell, AutopilotBudget.Large));
        Assert.True(tracker.Evaluate(AtRest(), AfterDwell + AutopilotPolicy.IdleDwell, AutopilotBudget.Large));
    }

    [Fact]
    public void ExhaustedBudgetHoldsTheNudge()
    {
        var tracker = new AutopilotTracker();

        // Baseline first: the dwell is measured from when the tracker saw the session settle.
        tracker.Evaluate(AtRest(), T0, AutopilotBudget.Exhausted);

        Assert.False(tracker.Evaluate(AtRest(), AfterDwell, AutopilotBudget.Exhausted));
        Assert.Equal(AutopilotHold.Budget, tracker.LastHold);
    }

    [Fact]
    public void HonoursTheMinimumGapBetweenNudges()
    {
        var tracker = new AutopilotTracker();
        tracker.Evaluate(AtRest(), T0, AutopilotBudget.Large);
        Assert.True(tracker.Evaluate(AtRest(), AfterDwell, AutopilotBudget.Large));

        // A turn that ran, did real work, and came back almost immediately. It settles for a full
        // dwell, so the only thing still holding the next nudge is the interval floor — which is
        // the point: without it a session whose turns return in seconds could spend its whole
        // unproductive allowance in under a minute.
        tracker.Evaluate(Busy(tools: 5), AfterDwell.AddSeconds(5), AutopilotBudget.Large);
        tracker.Evaluate(AtRest(tools: 5), AfterDwell.AddSeconds(10), AutopilotBudget.Large);

        var settled = AfterDwell.AddSeconds(10) + AutopilotPolicy.IdleDwell + TimeSpan.FromSeconds(1);
        Assert.True(settled - AfterDwell < AutopilotPolicy.MinNudgeInterval);

        Assert.False(tracker.Evaluate(AtRest(tools: 5), settled, AutopilotBudget.Large));
        Assert.Equal(AutopilotHold.TooSoon, tracker.LastHold);
    }

    // --- the futile loop, which is the whole reason there is a ceiling at all ---

    /// <summary>
    /// Drives one full cycle: the nudge, then the turn coming back at rest with the given tool
    /// count. Times are advanced past both the dwell and the minimum interval.
    /// </summary>
    private static DateTime RunCycle(AutopilotTracker tracker, DateTime now, long toolsAfter, long toolsBefore)
    {
        // The dwell runs from the first moment the tracker *observes* the session at rest, not from
        // some earlier time it was not watching, so every cycle establishes that baseline before
        // advancing the clock past it.
        tracker.Evaluate(AtRest(toolsBefore), now, AutopilotBudget.Large);

        var nudgeAt = now + AutopilotPolicy.IdleDwell + TimeSpan.FromSeconds(1);
        Assert.True(tracker.Evaluate(AtRest(toolsBefore), nudgeAt, AutopilotBudget.Large));

        // The turn runs, then returns to rest.
        tracker.Evaluate(Busy(toolsAfter), nudgeAt.AddSeconds(5), AutopilotBudget.Large);

        var settledAt = nudgeAt + AutopilotPolicy.MinNudgeInterval + TimeSpan.FromSeconds(1);
        tracker.Evaluate(AtRest(toolsAfter), settledAt, AutopilotBudget.Large);

        return settledAt;
    }

    [Fact]
    public void ThreeConsecutiveTurnsThatDoNothingParkTheSession()
    {
        // The failure this guards: nudge, "I have nothing left to do", nudge, "I have nothing left
        // to do", all night — burning quota the other sessions could have spent. A turn that
        // touched no tools is that answer.
        var tracker = new AutopilotTracker();
        var now = T0;

        for (var i = 0; i < AutopilotPolicy.MaxUnproductiveNudges; i++)
            now = RunCycle(tracker, now, toolsAfter: 0, toolsBefore: 0);

        Assert.True(tracker.IsParked);
        Assert.Equal(AutopilotPolicy.MaxUnproductiveNudges, tracker.NudgeCount);

        Assert.False(tracker.Evaluate(AtRest(), now.AddHours(1), AutopilotBudget.Large));
        Assert.Equal(AutopilotHold.Parked, tracker.LastHold);
    }

    [Fact]
    public void AProductiveTurnResetsTheStreak()
    {
        // The counter is on *unproductive* nudges, not on all of them. A flat ceiling would punish
        // exactly the session the feature exists for — the one still finding real work to do.
        var tracker = new AutopilotTracker();
        var now = T0;
        long tools = 0;

        for (var i = 0; i < 10; i++)
        {
            now = RunCycle(tracker, now, toolsAfter: tools + 3, toolsBefore: tools);
            tools += 3;
        }

        Assert.False(tracker.IsParked);
        Assert.Equal(0, tracker.UnproductiveStreak);
        Assert.Equal(10, tracker.NudgeCount);
    }

    [Fact]
    public void TwoQuietTurnsThenAProductiveOneDoesNotPark()
    {
        var tracker = new AutopilotTracker();
        var now = T0;

        now = RunCycle(tracker, now, toolsAfter: 0, toolsBefore: 0);
        now = RunCycle(tracker, now, toolsAfter: 0, toolsBefore: 0);
        Assert.Equal(2, tracker.UnproductiveStreak);

        RunCycle(tracker, now, toolsAfter: 7, toolsBefore: 0);

        Assert.False(tracker.IsParked);
        Assert.Equal(0, tracker.UnproductiveStreak);
    }

    [Fact]
    public void AHumanMessageUnparksTheSession()
    {
        // Whatever autopilot concluded about a session stops being true the moment someone gives it
        // new instructions.
        var tracker = new AutopilotTracker();
        var now = T0;

        for (var i = 0; i < AutopilotPolicy.MaxUnproductiveNudges; i++)
            now = RunCycle(tracker, now, toolsAfter: 0, toolsBefore: 0);

        Assert.True(tracker.IsParked);

        tracker.Evaluate(AtRest(human: 1), now.AddMinutes(30), AutopilotBudget.Large);

        Assert.False(tracker.IsParked);
        Assert.Equal(0, tracker.UnproductiveStreak);
    }

    [Fact]
    public void TheFirstSnapshotDoesNotCountAsAHumanArriving()
    {
        // A session with prior conversation starts with a non-zero count. Treating that baseline as
        // an arrival would be harmless here but would mask a real reset later.
        var tracker = new AutopilotTracker();

        Assert.False(tracker.Evaluate(AtRest(human: 12), T0, AutopilotBudget.Large));
        Assert.True(tracker.Evaluate(AtRest(human: 12), AfterDwell, AutopilotBudget.Large));
    }

    [Fact]
    public void ANewWindowGivesAParkedSessionAFreshHearing()
    {
        // A park is a verdict about a moment, not about a session. Overnight the repository gains
        // commits and new failures, so last night's "nothing left to do" says nothing about tonight.
        var tracker = new AutopilotTracker();
        var now = T0;

        for (var i = 0; i < AutopilotPolicy.MaxUnproductiveNudges; i++)
            now = RunCycle(tracker, now, toolsAfter: 0, toolsBefore: 0);

        Assert.True(tracker.IsParked);

        tracker.ResetForNewWindow();

        Assert.False(tracker.IsParked);
        Assert.Equal(0, tracker.NudgeCount);
        Assert.False(tracker.Evaluate(AtRest(), now.AddDays(1), AutopilotBudget.Large));
        Assert.True(tracker.Evaluate(AtRest(), now.AddDays(1) + AutopilotPolicy.IdleDwell + TimeSpan.FromSeconds(1),
            AutopilotBudget.Large));
    }
}
