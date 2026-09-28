using System.Globalization;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Every rule autopilot decides by, with no WPF, no timers and no sessions attached — the same
/// split <see cref="SendQueue"/> uses, and for the same reason. Autopilot writes into live agent
/// sessions unattended, so "when does it fire" and "what does it say" are exactly the parts that
/// have to be provable at a desk rather than discovered at 3am.
///
/// The window wiring in <c>MainWindow.Autopilot.cs</c> owns the timer, the per-session bookkeeping
/// and the actual send.
/// </summary>
public static class AutopilotPolicy
{
    /// <summary>
    /// Consecutive nudges that produce no tool use before a session is parked for the rest of the
    /// window.
    ///
    /// This counts <i>unproductive</i> nudges, not all of them, and that distinction is the whole
    /// point. A flat ceiling punishes the session that has real work — the one the feature exists
    /// for — while leaving the actual failure untouched: nudge, "I have nothing left to do", nudge,
    /// "I have nothing left to do", all night, burning quota that the other sessions could have
    /// spent. A turn that touched no tools is that answer, so three of them in a row is the session
    /// telling us it is finished. Any productive turn resets the count.
    /// </summary>
    public const int MaxUnproductiveNudges = 3;

    /// <summary>
    /// How long a session must sit at rest before it is nudged. Not zero: a turn that has just
    /// finished is the moment a watching user starts reading it and reaching for the keyboard, and
    /// having the agent talk over them is a worse first impression than a minute's delay. Any
    /// keystroke in the composer cancels the pending nudge outright.
    /// </summary>
    public static readonly TimeSpan IdleDwell = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Floor on the gap between two nudges to the same session, independent of the dwell. Bounds
    /// how fast the unproductive counter can be spent if turns return almost instantly.
    /// </summary>
    public static readonly TimeSpan MinNudgeInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How stale a usage reading may be and still gate a nudge.
    ///
    /// <see cref="UsageService"/> is demand-driven and rate-limited, and it has real failure modes
    /// — a lapsed token, an HTTP 429, no network. Every one of those produces *no number*, and a
    /// missing number must never read as "plenty left": that is precisely how autopilot would start
    /// a large task on an exhausted plan. So the absence of a fresh reading blocks the nudge. Ten
    /// minutes is comfortably longer than the 90-second refresh tick and short enough that a
    /// session's own spending cannot hide inside it.
    /// </summary>
    public static readonly TimeSpan MaxUsageAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True when <paramref name="now"/> falls inside the window.
    ///
    /// A window that wraps past midnight (23:00→04:00) is the ordinary case for this feature, not
    /// an edge case, so it gets the same treatment as any other: start and end are two points on a
    /// circle, and "inside" is the arc from one to the other. Start == end means the whole day,
    /// which is the only sensible reading of a zero-length window here — nobody arms autopilot to
    /// have it never run.
    /// </summary>
    public static bool IsWithinWindow(int startMinutes, int endMinutes, TimeOnly now)
    {
        var start = Normalize(startMinutes);
        var end = Normalize(endMinutes);
        var minute = now.Hour * 60 + now.Minute;

        if (start == end) return true;

        return start < end
            ? minute >= start && minute < end
            : minute >= start || minute < end;
    }

    private static int Normalize(int minutes) => ((minutes % 1440) + 1440) % 1440;

    /// <summary>
    /// Parses a 24-hour time typed into the schedule fields, as minutes from midnight.
    ///
    /// Accepts "23:00", "23000" as "2300", and a bare "9" — the fields are typed at speed by
    /// somebody setting up an overnight run, and rejecting a shape that has exactly one sensible
    /// reading is pedantry rather than validation.
    /// </summary>
    public static bool TryParseTimeOfDay(string? text, out int minutes)
    {
        minutes = 0;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        // "%H" rather than "H": a format string one character long is read as a *standard* format
        // specifier, and there is no standard "H", so the bare form silently never matches. Typing
        // "9" for nine o'clock would be rejected with nothing on screen explaining why.
        string[] formats = ["HH:mm", "H:mm", "HHmm", "%H"];

        foreach (var format in formats)
        {
            if (!TimeOnly.TryParseExact(
                    text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                continue;
            }

            minutes = parsed.Hour * 60 + parsed.Minute;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Buckets the remaining quota.
    ///
    /// Gated on the <i>worst</i> of the 5-hour and 7-day windows, because they run out
    /// independently and the one on screen is not reliably the binding one: 80% of the 5-hour left
    /// against 2% of the weekly is a real combination, and starting a substantial task there is the
    /// "runs out mid-task and leaves an unknown state" failure the floor exists to prevent.
    ///
    /// The per-model weekly windows are deliberately not consulted. They only bind for sessions
    /// actually on that model, and treating an exhausted Opus allowance as a reason to stop nudging
    /// a Sonnet session would park work that had budget to run.
    ///
    /// A summary with no usable figure returns <see cref="AutopilotBudget.Exhausted"/> — fail
    /// closed, per <see cref="MaxUsageAge"/>.
    /// </summary>
    public static AutopilotBudget ClassifyBudget(UsageSummary? summary)
    {
        if (RemainingPercent(summary) is not { } remaining)
            return AutopilotBudget.Exhausted;

        // Boundaries are inclusive downward: "more than half left" is what licenses a large task, so
        // exactly half does not.
        return remaining switch
        {
            < 5 => AutopilotBudget.Exhausted,
            < 20 => AutopilotBudget.Small,
            <= 50 => AutopilotBudget.Moderate,
            _ => AutopilotBudget.Large,
        };
    }

    /// <summary>
    /// Remaining percentage on whichever gated window is closest to running out. Null when no
    /// window reported a figure at all, which is the fail-closed case.
    ///
    /// A window that is absent is treated as not binding rather than as unknown. Only one of the
    /// two is reliably present on every plan, and refusing to nudge because a *second* window went
    /// unreported would disable the feature for anyone whose plan only publishes the first — a
    /// strictly worse outcome than gating on the figure actually in hand.
    /// </summary>
    public static double? RemainingPercent(UsageSummary? summary)
    {
        double? used = null;

        foreach (var window in new[] { summary?.FiveHour, summary?.SevenDay })
        {
            if (window?.Utilization is not { } utilization) continue;
            used = used is { } current ? Math.Max(current, utilization) : utilization;
        }

        return used is { } worst ? Math.Max(0, 100 - worst) : null;
    }

    /// <summary>
    /// The default nudge. Editable per-machine via the dialog; the budget line is appended
    /// separately because it comes from live usage rather than from the user.
    ///
    /// Three things in here are load-bearing rather than stylistic:
    ///
    /// <list type="bullet">
    /// <item>The ranked preference for <i>resuming</i> work. The case this feature was built for is
    /// an agent that finishes a phase, says it is ready for the next one, and then waits — so the
    /// most valuable instruction is almost never "find something", it is "do the thing you already
    /// said you would do".</item>
    /// <item>The ban on anything needing a decision. This prompt is generic across every session it
    /// manages, so it cannot know what is safe to choose; the only honest rule is that the agent
    /// must not choose anything a human would otherwise have been asked about.</item>
    /// <item>The closing escape hatch. Without an explicit, cheap way to answer "nothing", an agent
    /// asked to find work will find work — and inventing busywork unattended is worse than an idle
    /// session. It also makes the empty answer detectable, since a turn that declines uses no tools
    /// and so registers as unproductive.</item>
    /// </list>
    /// </summary>
    public const string DefaultPrompt = """
        You're running unattended. No one is watching this session, and no one will answer a
        question until morning.

        Pick up the most valuable piece of work you can finish on your own, preferring in this
        order:

        1. Something you already started, or explicitly said you would do next.
        2. Work implied by what is already here — a missing test, a half-updated doc, a case you
           noted and skipped.
        3. Something justified by the repository itself — a failing test, a TODO, a clear
           inconsistency.

        Only take on work that is fully determined by what already exists. Nothing that needs a
        decision from me: no design choices, no product calls, no naming or scope preferences, and
        nothing where you would normally stop and ask. Do not guess at an answer you would have
        asked for.

        Stay inside this repository. Do not push, open pull requests, deploy, or message anyone.

        Leave the tree in a state I can walk into: work committed, tests passing, and a short note
        of what you did and what you deliberately left alone.

        If there is genuinely nothing that meets this bar, say so in one line and stop. Inventing
        work is worse than doing none.
        """;

    /// <summary>The budget-specific sentence appended to the prompt.</summary>
    public static string BudgetLine(AutopilotBudget budget) => budget switch
    {
        AutopilotBudget.Large =>
            "Budget: there is plenty of quota left this session. A substantial piece of work is fine.",
        AutopilotBudget.Moderate =>
            "Budget: moderate. Pick something you can carry to a finished state in one sitting — " +
            "don't start a large refactor.",
        AutopilotBudget.Small =>
            "Budget: low. Pick something small and self-contained that you can finish and commit " +
            "well within it. If the smallest useful step is still large, stop instead.",
        _ => "",
    };

    /// <summary>
    /// The complete message sent to a session. Returns null for
    /// <see cref="AutopilotBudget.Exhausted"/> — at that point the correct action is silence, and
    /// returning null rather than an empty string makes a caller that forgets to check fail loudly.
    /// </summary>
    public static string? ComposeNudge(AutopilotBudget budget, string? promptOverride = null)
    {
        if (budget == AutopilotBudget.Exhausted) return null;

        var body = string.IsNullOrWhiteSpace(promptOverride)
            ? DefaultPrompt
            : promptOverride.Trim();

        return $"{body}\n\n{BudgetLine(budget)}";
    }
}
