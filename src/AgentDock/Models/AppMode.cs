using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDock.Models;

/// <summary>
/// What this copy of Agent Dock is currently doing. Exactly one mode is in force at a time, which
/// is why this is an enum rather than a set of independent flags.
///
/// Before this existed the same information was spread across <c>_remoteServer.IsRunning</c>,
/// <c>_remoteClient.State</c> and <c>_remoteGroupId</c>, and every place that needed to know "can
/// I do X here" re-derived it with its own pairwise combination of those three. Adding a fourth
/// state to that arrangement would have meant auditing every one of those sites; naming the states
/// instead means a new mode is one enum member and one switch arm.
/// </summary>
public enum AppMode
{
    /// <summary>Ordinary local use. Nothing is locked, nothing is scheduled, nothing is published.</summary>
    Standard,

    /// <summary>Hosting this machine's sessions for another Agent Dock. Agent input is locked here.</summary>
    Server,

    /// <summary>
    /// Standard, plus a schedule: inside the nominated window, selected sessions that come to rest
    /// are nudged to carry on by themselves. Outside the window this behaves exactly like
    /// <see cref="Standard"/> — nothing is disabled and the user drives normally.
    /// </summary>
    Autopilot,

    /// <summary>
    /// Driving another machine's sessions. The odd one out: it is the only mode that can end
    /// without the user choosing to leave it (the link drops, or the host takes back control), so
    /// anything that arms a mode has to cope with the mode changing underneath it.
    /// </summary>
    Remote,
}

/// <summary>
/// How much of the plan's quota is left, bucketed into the four cases the nudge prompt
/// distinguishes. Derived from <see cref="UsageSummary"/> — see <c>AutopilotPolicy</c>.
/// </summary>
public enum AutopilotBudget
{
    /// <summary>Under 5% left. No nudge is sent at all: a turn that runs out mid-edit leaves the
    /// tree in a state nobody asked for and nobody is awake to fix.</summary>
    Exhausted,

    /// <summary>5–20% left. Small, self-contained work only.</summary>
    Small,

    /// <summary>20–50% left. One sitting's worth; no large refactors.</summary>
    Moderate,

    /// <summary>Over 50% left. Substantial work is fine.</summary>
    Large,
}

/// <summary>
/// Everything the user chose in the Autopilot dialog. Persisted as one JSON blob in app settings
/// so the daily-repeat schedule survives a restart — arming autopilot at 5pm for an 11pm start is
/// useless if closing the app forgets it.
/// </summary>
public sealed class AutopilotSettings
{
    /// <summary>Window start, as minutes from local midnight. 23:00 is 1380.</summary>
    [JsonPropertyName("startMinutes")]
    public int StartMinutes { get; set; } = 23 * 60;

    /// <summary>
    /// Window end, as minutes from local midnight. May be less than <see cref="StartMinutes"/> —
    /// 23:00→04:00 is the expected shape, not an edge case.
    /// </summary>
    [JsonPropertyName("endMinutes")]
    public int EndMinutes { get; set; } = 4 * 60;

    /// <summary>When true the window reopens every day; when false autopilot disarms itself after
    /// the first window closes.</summary>
    [JsonPropertyName("repeatDaily")]
    public bool RepeatDaily { get; set; } = true;

    /// <summary>
    /// Whether autopilot was left armed. Persisted so a daily schedule survives a restart —
    /// without this, "repeat every day" would only mean "repeat until you next close the app",
    /// which is not a daily schedule in any sense the user would recognise.
    ///
    /// Only ever restored alongside <see cref="RepeatDaily"/>. A one-shot window is a decision
    /// about tonight, and silently reinstating it whenever the app happens to launch would be
    /// arming a mode that writes into live sessions on the strength of a choice made days ago.
    /// </summary>
    [JsonPropertyName("armed")]
    public bool Armed { get; set; }

    /// <summary>
    /// Folder paths of the projects autopilot manages. Paths rather than <c>ProjectInfo</c>
    /// references because this outlives the window: a project closed and reopened, or a workspace
    /// reloaded, is the same project to the user and should still be selected.
    /// </summary>
    [JsonPropertyName("projectPaths")]
    public List<string> ProjectPaths { get; set; } = [];

    /// <summary>
    /// User-edited replacement for the built-in nudge, or null to use the default. The budget line
    /// is appended either way — it is derived from live usage, not something the user can pin.
    /// </summary>
    [JsonPropertyName("promptOverride")]
    public string? PromptOverride { get; set; }

    /// <summary>Formats the window for a badge or a menu item: "11:00pm–4:00am".</summary>
    public string DescribeWindow() =>
        $"{FormatMinutes(StartMinutes)}–{FormatMinutes(EndMinutes)}";

    private static string FormatMinutes(int minutes)
    {
        var time = new TimeOnly(minutes / 60 % 24, minutes % 60);
        return time.ToString("h:mmtt").ToLowerInvariant();
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Reads persisted settings, falling back to defaults on anything unreadable. A corrupt blob
    /// must not stop the app arming autopilot — the user can always re-pick in the dialog.
    /// </summary>
    public static AutopilotSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AutopilotSettings();

        try
        {
            return JsonSerializer.Deserialize<AutopilotSettings>(json) ?? new AutopilotSettings();
        }
        catch (JsonException)
        {
            return new AutopilotSettings();
        }
    }
}
