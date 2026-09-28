using AgentDock.Models;
using AgentDock.Services;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// The rules autopilot fires by. These matter more than most tests in this repo because the feature
/// runs with nobody watching: a mistake here is not a wrong pixel, it is a message written into a
/// live agent session at 3am on a plan that had no quota left to finish it.
/// </summary>
public class AutopilotPolicyTests
{
    // --- the schedule window ---

    [Theory]
    [InlineData(9, 0, true)]    // inside
    [InlineData(8, 0, true)]    // start is inclusive
    [InlineData(17, 0, false)]  // end is exclusive
    [InlineData(7, 59, false)]
    [InlineData(23, 0, false)]
    public void DaytimeWindow_ContainsOnlyItsOwnHours(int hour, int minute, bool expected)
    {
        var inside = AutopilotPolicy.IsWithinWindow(8 * 60, 17 * 60, new TimeOnly(hour, minute));
        Assert.Equal(expected, inside);
    }

    /// <summary>
    /// The overnight window is what this feature is actually for — 23:00 to 04:00 was the user's
    /// own example — so "crosses midnight" is the ordinary path, not a corner case bolted on after.
    /// </summary>
    [Theory]
    [InlineData(23, 0, true)]   // start, inclusive
    [InlineData(23, 59, true)]  // last minute before the wrap
    [InlineData(0, 0, true)]    // midnight itself
    [InlineData(3, 59, true)]   // last minute inside
    [InlineData(4, 0, false)]   // end, exclusive
    [InlineData(12, 0, false)]  // the long way round
    [InlineData(22, 59, false)]
    public void OvernightWindow_WrapsPastMidnight(int hour, int minute, bool expected)
    {
        var inside = AutopilotPolicy.IsWithinWindow(23 * 60, 4 * 60, new TimeOnly(hour, minute));
        Assert.Equal(expected, inside);
    }

    [Fact]
    public void EqualStartAndEnd_MeansAllDay()
    {
        // Nobody arms autopilot in order for it never to run, so a zero-length window reads as
        // "no time restriction" rather than as "never".
        Assert.True(AutopilotPolicy.IsWithinWindow(0, 0, new TimeOnly(3, 0)));
        Assert.True(AutopilotPolicy.IsWithinWindow(9 * 60, 9 * 60, new TimeOnly(21, 0)));
    }

    // --- typing a time into the schedule fields ---

    [Theory]
    [InlineData("23:00", 23 * 60)]
    [InlineData("4:00", 4 * 60)]
    [InlineData("2300", 23 * 60)]
    [InlineData("09:30", 9 * 60 + 30)]
    [InlineData("  23:00  ", 23 * 60)]
    [InlineData("0", 0)]
    public void AcceptsTheShapesSomeoneActuallyTypes(string text, int expected)
    {
        Assert.True(AutopilotPolicy.TryParseTimeOfDay(text, out var minutes));
        Assert.Equal(expected, minutes);
    }

    [Fact]
    public void AcceptsABareHour()
    {
        // Regression guard. A one-character format string is read as a *standard* format specifier,
        // and there is no standard "H" — so the obvious spelling silently matches nothing, and
        // typing "9" for nine o'clock leaves the Enable button disabled with no explanation. The
        // fix is "%H", and nothing about the symptom would point you at it.
        Assert.True(AutopilotPolicy.TryParseTimeOfDay("9", out var minutes));
        Assert.Equal(9 * 60, minutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("25:00")]
    [InlineData("11pm")]
    [InlineData("half eleven")]
    public void RejectsWhatItCannotReadUnambiguously(string? text)
    {
        Assert.False(AutopilotPolicy.TryParseTimeOfDay(text, out _));
    }

    // --- the budget gate ---

    private static UsageSummary Usage(double? fiveHour, double? sevenDay = null) => new()
    {
        FiveHour = fiveHour is { } f ? new UsageWindow { Utilization = f } : null,
        SevenDay = sevenDay is { } s ? new UsageWindow { Utilization = s } : null,
    };

    [Theory]
    [InlineData(0, AutopilotBudget.Large)]
    [InlineData(49, AutopilotBudget.Large)]
    [InlineData(50, AutopilotBudget.Moderate)]
    [InlineData(80, AutopilotBudget.Moderate)]
    [InlineData(81, AutopilotBudget.Small)]
    [InlineData(95, AutopilotBudget.Small)]
    [InlineData(96, AutopilotBudget.Exhausted)]
    [InlineData(100, AutopilotBudget.Exhausted)]
    public void Budget_TracksRemainingQuota(double utilization, AutopilotBudget expected)
    {
        // Utilization is percent *used*; the thresholds are on what is left.
        Assert.Equal(expected, AutopilotPolicy.ClassifyBudget(Usage(utilization)));
    }

    [Fact]
    public void Budget_GatesOnTheWorstWindow_NotTheOneOnScreen()
    {
        // The combination that makes this necessary: the 5-hour figure in the title bar looks
        // healthy while the weekly allowance is nearly gone. Reading only the former is how
        // autopilot would start a substantial task it has no room to finish.
        var summary = Usage(fiveHour: 10, sevenDay: 98);

        Assert.Equal(AutopilotBudget.Exhausted, AutopilotPolicy.ClassifyBudget(summary));
    }

    [Fact]
    public void Budget_FailsClosedWhenThereIsNoReading()
    {
        // Every usage failure mode — lapsed token, HTTP 429, offline — produces no number, and a
        // missing number must never be read as "plenty left".
        Assert.Equal(AutopilotBudget.Exhausted, AutopilotPolicy.ClassifyBudget(null));
        Assert.Equal(AutopilotBudget.Exhausted, AutopilotPolicy.ClassifyBudget(new UsageSummary()));
        Assert.Equal(AutopilotBudget.Exhausted, AutopilotPolicy.ClassifyBudget(Usage(null)));
    }

    // --- the prompt ---

    [Fact]
    public void ExhaustedBudget_ProducesNoMessageAtAll()
    {
        // Not an empty string: below the floor the correct behaviour is silence, and returning null
        // makes a caller that forgets to check fail loudly instead of sending a blank turn.
        Assert.Null(AutopilotPolicy.ComposeNudge(AutopilotBudget.Exhausted));
    }

    [Theory]
    [InlineData(AutopilotBudget.Large, "substantial")]
    [InlineData(AutopilotBudget.Moderate, "one sitting")]
    [InlineData(AutopilotBudget.Small, "small and self-contained")]
    public void NudgeCarriesABudgetLineMatchingTheTier(AutopilotBudget budget, string expected)
    {
        var nudge = AutopilotPolicy.ComposeNudge(budget);

        Assert.NotNull(nudge);
        Assert.Contains(expected, nudge);
    }

    [Fact]
    public void DefaultNudge_CarriesTheThreeInstructionsThatMakeItSafe()
    {
        var nudge = AutopilotPolicy.ComposeNudge(AutopilotBudget.Large)!;

        // Resume before invent — the case the feature was built for is an agent that already said
        // what it would do next and then stopped.
        Assert.Contains("already started", nudge);

        // No decisions on the user's behalf. The prompt is generic across every managed session, so
        // it cannot know which choices are safe; the only honest rule is that it makes none.
        Assert.Contains("no design choices", nudge);

        // The escape hatch. Without it, an agent asked to find work will find work, and inventing
        // busywork unattended is worse than an idle session.
        Assert.Contains("nothing that meets this bar", nudge);
    }

    [Fact]
    public void CustomPrompt_ReplacesTheBodyButNotTheBudgetLine()
    {
        // The budget line is derived from live usage, so it is not the user's to pin down.
        var nudge = AutopilotPolicy.ComposeNudge(AutopilotBudget.Small, "Carry on with the migration.")!;

        Assert.Contains("Carry on with the migration.", nudge);
        Assert.DoesNotContain("already started", nudge);
        Assert.Contains("small and self-contained", nudge);
    }

    [Fact]
    public void BlankCustomPrompt_FallsBackToTheDefault()
    {
        var nudge = AutopilotPolicy.ComposeNudge(AutopilotBudget.Large, "   ")!;

        Assert.Contains("already started", nudge);
    }
}
