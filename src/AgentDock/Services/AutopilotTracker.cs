using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>What the window observed about one managed session at one instant.</summary>
/// <param name="IsAtRest">
/// True only when the session has genuinely stopped and nothing is waiting on a human.
///
/// "Waiting for user input" is not one state, and conflating them is how this feature would do
/// damage: <c>Idle</c> with subagents still draining is the exact case autopilot exists to fix and
/// the exact case where nudging corrupts the turn; a pending permission prompt or an
/// <c>AskUserQuestion</c> is a session waiting for a decision autopilot is forbidden to make; a
/// non-empty send queue already has something lined up. The caller folds all of those in.
/// </param>
/// <param name="ToolUseCount">
/// Monotonic count of tool-use blocks the session has emitted. Compared against its value at the
/// last nudge to decide whether that nudge produced work — see
/// <see cref="AutopilotPolicy.MaxUnproductiveNudges"/>.
/// </param>
/// <param name="HumanMessageCount">
/// Monotonic count of messages the *user* sent. Any increase means a human is at the keyboard,
/// which un-parks the session and clears the unproductive count: whatever autopilot concluded about
/// this session is out of date the moment someone gives it new instructions.
/// </param>
public readonly record struct AutopilotSnapshot(
    bool IsAtRest,
    long ToolUseCount,
    long HumanMessageCount);

/// <summary>Why a session is not being nudged right now. Surfaced in the badge tooltip.</summary>
public enum AutopilotHold
{
    /// <summary>Nudge now.</summary>
    None,

    /// <summary>Session is mid-turn, has background work, or is waiting on a human.</summary>
    Busy,

    /// <summary>At rest, but not yet for <see cref="AutopilotPolicy.IdleDwell"/>.</summary>
    Settling,

    /// <summary>Nudged too recently — <see cref="AutopilotPolicy.MinNudgeInterval"/>.</summary>
    TooSoon,

    /// <summary>Quota is below the floor, or no fresh usage reading is available.</summary>
    Budget,

    /// <summary>Answered "nothing to do" too many times running; done until a human returns.</summary>
    Parked,
}

/// <summary>
/// Per-session bookkeeping for autopilot: dwell timing, the nudge interval, and the
/// consecutive-unproductive count that eventually parks a finished session.
///
/// Pure and clock-injected — every decision is a function of the snapshot and the time passed in,
/// so the whole of the "when does it fire" question is testable without a session, a timer or a
/// window. <c>MainWindow.Autopilot.cs</c> supplies the observations and performs the send.
/// </summary>
public sealed class AutopilotTracker
{
    private DateTime? _restingSinceUtc;
    private DateTime? _lastNudgeUtc;
    private long _toolUseAtNudge;
    private long _lastHumanMessageCount;
    private bool _awaitingResult;
    private bool _seenFirstSnapshot;

    /// <summary>Consecutive nudges that produced no tool use. Reset by any productive turn, and by
    /// the user sending anything.</summary>
    public int UnproductiveStreak { get; private set; }

    /// <summary>True once the session has declined to find work
    /// <see cref="AutopilotPolicy.MaxUnproductiveNudges"/> times running.</summary>
    public bool IsParked { get; private set; }

    /// <summary>Total nudges sent to this session in the current window, for the summary line.</summary>
    public int NudgeCount { get; private set; }

    /// <summary>Why the last <see cref="Evaluate"/> declined to nudge.</summary>
    public AutopilotHold LastHold { get; private set; } = AutopilotHold.Busy;

    /// <summary>
    /// Clears everything except the nudge total. Called when the window opens, so a session parked
    /// last night gets a fresh hearing tonight — the repository will have moved on, and a judgement
    /// about "nothing left to do" does not survive that.
    /// </summary>
    public void ResetForNewWindow()
    {
        _restingSinceUtc = null;
        _lastNudgeUtc = null;
        _awaitingResult = false;
        UnproductiveStreak = 0;
        IsParked = false;
        NudgeCount = 0;
        LastHold = AutopilotHold.Busy;
    }

    /// <summary>
    /// Folds one observation in and says whether to nudge now.
    ///
    /// Productivity is settled here rather than at send time: the result of a nudge is only known
    /// once the turn it started has come back to rest, so the tool-use comparison happens on the
    /// first at-rest snapshot after a nudge, before that same snapshot is considered for the next
    /// one.
    /// </summary>
    public bool Evaluate(AutopilotSnapshot snapshot, DateTime nowUtc, AutopilotBudget budget)
    {
        // A human message un-parks unconditionally: someone gave this session new instructions, so
        // whatever autopilot concluded about it no longer holds. The first snapshot only records
        // the baseline — there is nothing before it to have increased from.
        if (_seenFirstSnapshot && snapshot.HumanMessageCount > _lastHumanMessageCount)
        {
            UnproductiveStreak = 0;
            IsParked = false;
            _awaitingResult = false;
        }

        _lastHumanMessageCount = snapshot.HumanMessageCount;
        _seenFirstSnapshot = true;

        if (!snapshot.IsAtRest)
        {
            _restingSinceUtc = null;
            LastHold = AutopilotHold.Busy;
            return false;
        }

        // The turn we started has finished. Grade it before deciding anything else.
        if (_awaitingResult)
        {
            _awaitingResult = false;

            if (snapshot.ToolUseCount > _toolUseAtNudge)
            {
                UnproductiveStreak = 0;
            }
            else if (++UnproductiveStreak >= AutopilotPolicy.MaxUnproductiveNudges)
            {
                IsParked = true;
            }
        }

        if (IsParked)
        {
            LastHold = AutopilotHold.Parked;
            return false;
        }

        _restingSinceUtc ??= nowUtc;

        if (nowUtc - _restingSinceUtc.Value < AutopilotPolicy.IdleDwell)
        {
            LastHold = AutopilotHold.Settling;
            return false;
        }

        if (_lastNudgeUtc is { } last && nowUtc - last < AutopilotPolicy.MinNudgeInterval)
        {
            LastHold = AutopilotHold.TooSoon;
            return false;
        }

        // Budget last, so the cheaper gates above shed most calls before this one is consulted and
        // the hold reason reported is the most specific one that applies.
        if (budget == AutopilotBudget.Exhausted)
        {
            LastHold = AutopilotHold.Budget;
            return false;
        }

        _lastNudgeUtc = nowUtc;
        _toolUseAtNudge = snapshot.ToolUseCount;
        _awaitingResult = true;
        _restingSinceUtc = null;
        NudgeCount++;
        LastHold = AutopilotHold.None;
        return true;
    }

    /// <summary>
    /// Drops the dwell timer without touching the streak or the park state. Called when the user
    /// types into the composer: they are clearly about to say something, so the pending nudge goes
    /// back to the end of its wait rather than landing on top of them.
    /// </summary>
    public void CancelPendingNudge() => _restingSinceUtc = null;
}
