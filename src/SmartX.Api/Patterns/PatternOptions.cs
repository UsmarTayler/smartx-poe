namespace SmartX.Api.Patterns;

/// <summary>
/// Everything about learning operator habits that can reasonably be tuned: how soon an action counts as a response, how
/// much evidence a pattern needs, how fast old habits fade, and where the watch levels sit.
/// </summary>
/// <remarks>
/// Bound from the "Patterns" section through <c>IOptions</c>, and validated at startup. The defaults below only apply when
/// configuration says nothing, and each carries the reason it is what it is.
/// </remarks>
public sealed class PatternOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Patterns";

    /// <summary>How soon after a trigger an action counts as following it.</summary>
    /// <remarks>
    /// <b>Ten minutes.</b> Long enough for an operator to notice an alert, think, and go to the device, which is the whole
    /// response. Short enough that an unrelated visit later in the shift is not mistaken for it. It also bounds the cost of
    /// learning: an action is compared with every trigger inside this window and no others.
    /// </remarks>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The fewest times, weighted, that an action must have followed a trigger before it counts as a pattern.</summary>
    /// <remarks>
    /// <b>Three.</b> One is an anecdote and two is a coincidence that happens by chance more often than people expect. Three
    /// separate occasions is the smallest number that starts to look like a habit. Weighted, so three views count and nine
    /// searches do not, since a search is a weaker signal than a view (see <see cref="SearchWeight"/>).
    /// <para>
    /// Support fades with <see cref="HalfLife"/>, so "three" means three that are still fresh: a habit seen three times last
    /// week is worth about two now, and needs another visit to count. A two percent allowance keeps three pairings from
    /// failing the test the minute after they are made.
    /// </para>
    /// </remarks>
    public double MinimumSupport { get; set; } = 3;

    /// <summary>The share of <see cref="MinimumSupport"/> that a pattern's support must reach, to allow for a count that has only just faded.</summary>
    /// <remarks>
    /// Support is a faded count, so three pairings a minute ago add up to 2.9998 and not 3. Without a little give, "three"
    /// would mean "three, all of them brand new", and a pattern that had earned its place would drop out in the time it takes
    /// to make a cup of tea. Two percent is about three hours of fading at a week's half-life: a count that has faded by less
    /// than that is still the count it was.
    /// </remarks>
    public double SupportTolerance { get; set; } = 0.98;

    /// <summary>How many times more often than chance an action must follow a trigger to count as a pattern.</summary>
    /// <remarks>
    /// <b>1.5.</b> Lift is how likely the action is after the trigger divided by how likely it is in any window of the same
    /// length. At 1 the trigger tells you nothing: the operator does that anyway. At 1.5 the trigger makes the action half
    /// again as likely as it would otherwise be, which is a clear effect that a handful of observations can still show. Lower
    /// admits noise; much higher needs more history than a short shift produces. Confidence alone cannot do this job: a pump
    /// page that is open all day follows every trigger with confidence near 1, and lift is what exposes that as meaningless.
    /// <para>
    /// "Anyway" is the share of time that falls within a window of an occurrence of the action. A burst is one window and not
    /// many, constant activity covers everything, and an action seen now and then covers a window each time. So an engine
    /// that has watched for a few windows cannot yet show much lift for anything: it needs enough time to see what is
    /// ordinary, which is hours at the default window, and a short window shortens that.
    /// </para>
    /// </remarks>
    public double MinimumLift { get; set; } = 1.5;

    /// <summary>How long until a pairing counts half as much as a fresh one.</summary>
    /// <remarks>
    /// Seven days, so what an operator did last week still matters and what they did last month mostly does not. Operators
    /// change how they work (a new shift pattern, a replaced pump), and a pattern that never faded would keep recommending
    /// the old habit. The same exponential decay as the trust engine, on a longer clock because habits move slower than faults.
    /// </remarks>
    public TimeSpan HalfLife { get; set; } = TimeSpan.FromDays(7);

    /// <summary>What a search that narrowed to a few devices counts for, against one for a device opened or a command sent.</summary>
    /// <remarks>
    /// A search is half an intention: the operator looked for the device but did not open it. It is evidence, and weaker
    /// evidence than a deliberate click, so it counts less and is kept under its own action kind.
    /// </remarks>
    public double SearchWeight { get; set; } = 0.4;

    /// <summary>What an action counts for when the operator did it by accepting a suggestion, against one for an action of their own.</summary>
    /// <remarks>
    /// <b>Zero, because of the self-fulfilling prophecy.</b> If the engine suggests "turn on Pump A", and the operator
    /// clicks it because it was suggested, counting that click as another time Pump A followed the trigger would make the
    /// pattern stronger, which makes the suggestion rank higher, which gets it clicked more. The engine would be training on
    /// its own advice, and a pattern that began as a coincidence would become a habit in the data without anyone having had it.
    /// An action taken on the system's advice says the operator trusted the system, not that they had the habit, so by default
    /// it teaches the engine nothing: it is not counted as following the trigger, and not counted as an occurrence of the
    /// action either, because that would raise the baseline and push lift down. Raise it to let accepted suggestions count
    /// for part of an action of the operator's own.
    /// </remarks>
    public double AcceptedSuggestionWeight { get; set; }

    /// <summary>The most devices a search can return and still be read as being about them.</summary>
    /// <remarks>
    /// "Pump A" finds one or two devices, and that is a search for them. A search for everything finds forty-eight, and
    /// crediting all forty-eight as things the operator wanted would teach the engine that they want everything.
    /// </remarks>
    public int MaxSearchResultsForAction { get; set; } = 3;

    /// <summary>The most triggers held in the window at once. The oldest are dropped past it.</summary>
    /// <remarks>
    /// The bound on the cost of an action, which is proportional to the triggers in the window. A mesh in the middle of an
    /// incident can raise thousands in ten minutes, and an action must not slow down to match.
    /// </remarks>
    public int MaxTriggersInWindow { get; set; } = 5000;

    /// <summary>The most (trigger, action) pairs the engine remembers. Past it, the most faded are forgotten.</summary>
    /// <remarks>
    /// A bound on memory. Counts fade, but a faded count still occupies its slot, and a large mesh meeting many distinct
    /// triggers and many distinct actions could grow the table for ever. When the table is over this, entries that have faded
    /// to almost nothing are dropped first, then progressively less faded ones, until it fits. What is lost is by
    /// construction what mattered least.
    /// </remarks>
    public int MaxLearnedPairs { get; set; } = 250_000;

    /// <summary>Where the low watch level sits, as a fraction of the sensor's registered range.</summary>
    public double WatchLowFraction { get; set; } = 0.20;

    /// <summary>Where the high watch level sits, as a fraction of the sensor's registered range.</summary>
    public double WatchHighFraction { get; set; } = 0.80;

    /// <summary>
    /// How far back past a watch level a reading must come before the level can fire again, as a fraction of the range.
    /// </summary>
    /// <remarks>
    /// Hysteresis. A reading hovering around 20% would otherwise cross it on every packet. It fires on crossing below 20%
    /// and is not armed again until the reading is back above 25%, so one dip is one trigger however much it wobbles. This
    /// is how real IoT alarms avoid flapping.
    /// </remarks>
    public double RearmMargin { get; set; } = 0.05;

    /// <summary>How many levels up the zone tree a pattern generalises to.</summary>
    /// <remarks>
    /// The depth of the zone a device is grouped under at the general level. In the demo hierarchy depth 1 is the zone
    /// beneath the facility, so a sensor in Sub-Zone B and one directly in Zone 1 are both "in Zone 1", and one in Zone 4 is
    /// not. A device already shallower than this is grouped under its own zone.
    /// </remarks>
    public int GeneralZoneDepth { get; set; } = 1;
}

/// <summary>Checks the pattern options at startup, so a nonsensical value stops the API with a message naming it.</summary>
internal static class PatternOptionsValidation
{
    public static IEnumerable<string> Problems(PatternOptions options)
    {
        if (options.Window <= TimeSpan.Zero)
        {
            yield return "Patterns:Window must be positive.";
        }

        if (options.MinimumSupport <= 0)
        {
            yield return "Patterns:MinimumSupport must be positive.";
        }

        if (options.SupportTolerance is <= 0 or > 1)
        {
            yield return "Patterns:SupportTolerance must be above 0 and at most 1.";
        }

        if (options.MinimumLift < 1)
        {
            yield return "Patterns:MinimumLift must be at least 1; below 1 an action follows a trigger less than chance.";
        }

        if (options.HalfLife <= TimeSpan.Zero)
        {
            yield return "Patterns:HalfLife must be positive.";
        }

        if (options.SearchWeight is <= 0 or > 1)
        {
            yield return "Patterns:SearchWeight must be above 0 and at most 1.";
        }

        if (options.AcceptedSuggestionWeight is < 0 or > 1)
        {
            yield return "Patterns:AcceptedSuggestionWeight must be between 0 and 1.";
        }

        if (options.MaxSearchResultsForAction < 1)
        {
            yield return "Patterns:MaxSearchResultsForAction must be at least 1.";
        }

        if (options.MaxTriggersInWindow < 1)
        {
            yield return "Patterns:MaxTriggersInWindow must be at least 1.";
        }

        if (options.WatchLowFraction is <= 0 or >= 1
            || options.WatchHighFraction is <= 0 or >= 1
            || options.WatchLowFraction >= options.WatchHighFraction)
        {
            yield return "Patterns:WatchLowFraction and WatchHighFraction must be between 0 and 1, with the low level below the high.";
        }

        if (options.RearmMargin < 0
            || options.WatchLowFraction + options.RearmMargin >= options.WatchHighFraction - options.RearmMargin)
        {
            yield return "Patterns:RearmMargin cannot be negative and must leave the two re-arm levels apart.";
        }

        if (options.MaxLearnedPairs < 1)
        {
            yield return "Patterns:MaxLearnedPairs must be at least 1.";
        }

        if (options.GeneralZoneDepth < 0)
        {
            yield return "Patterns:GeneralZoneDepth cannot be negative.";
        }
    }
}

/// <summary>Settings for investigations and the verdicts that follow them.</summary>
public sealed class InvestigationOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Investigations";

    /// <summary>How many investigations are kept. The oldest closed ones go first.</summary>
    public int MaxRetained { get; set; } = 200;

    /// <summary>The most readings kept in an investigation, the latest in its window.</summary>
    public int MaxReadings { get; set; } = 60;

    /// <summary>What a node's weight for a failure is multiplied by each time an operator dismisses a fault caused by it.</summary>
    /// <remarks>
    /// Small steps, so a single false alarm nudges detection and does not rewrite it. Three dismissals take 1.0 to about 0.61.
    /// </remarks>
    public double DismissFactor { get; set; } = 0.85;

    /// <summary>The lowest a failure's scale can reach for a node, however many times it is dismissed.</summary>
    /// <remarks>
    /// <b>One half.</b> The bound that keeps feedback from becoming a way to silence a node. At half weight a node that is
    /// really failing still crosses into fault, only a little later; at zero it never would. Detection can be dulled and
    /// cannot be turned off, per node, and the adjustment is reversed by changing the verdict.
    /// </remarks>
    public double SensitivityFloor { get; set; } = 0.5;
}

internal static class InvestigationOptionsValidation
{
    public static IEnumerable<string> Problems(InvestigationOptions options)
    {
        if (options.MaxRetained < 1)
        {
            yield return "Investigations:MaxRetained must be at least 1.";
        }

        if (options.MaxReadings < 0)
        {
            yield return "Investigations:MaxReadings cannot be negative.";
        }

        if (options.DismissFactor is <= 0 or >= 1)
        {
            yield return "Investigations:DismissFactor must be above 0 and below 1.";
        }

        if (options.SensitivityFloor is <= 0 or > 1)
        {
            yield return "Investigations:SensitivityFloor must be above 0 and at most 1: zero would let a node be silenced.";
        }
    }
}
