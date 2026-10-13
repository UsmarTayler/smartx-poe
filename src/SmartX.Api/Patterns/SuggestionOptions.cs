namespace SmartX.Api.Patterns;

/// <summary>
/// How suggestions are ranked, how many are shown, and what "Not useful" does. Bound from the "Suggestions" section.
/// </summary>
/// <remarks>
/// The ranking is documented where it is computed, on <see cref="SuggestionService"/>. The defaults here are the weights that
/// formula uses, each with the reason it is what it is.
/// </remarks>
public sealed class SuggestionOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Suggestions";

    /// <summary>How many suggestions are kept and shown: the top of the ranking.</summary>
    /// <remarks>Five. A panel of twenty is a list to be read; five is a decision to be made.</remarks>
    public int MaxSuggestions { get; set; } = 5;

    /// <summary>The weight of lift: how much more likely than usual the action is after this trigger.</summary>
    /// <remarks>
    /// The largest weight. Lift is the one number that separates a habit from a coincidence, and it is what the whole
    /// engine exists to compute. Scaled by its logarithm against <see cref="LiftCap"/>, because "twice as likely" and "four
    /// times as likely" are the same distance apart as "four" and "eight".
    /// </remarks>
    public double LiftWeight { get; set; } = 0.35;

    /// <summary>The weight of confidence: how reliably the action follows.</summary>
    public double ConfidenceWeight { get; set; } = 0.25;

    /// <summary>The weight of the trigger's severity: a fault outranks a threshold crossing.</summary>
    /// <remarks>
    /// Equal to confidence. Two patterns that are equally well supported are not equally urgent when one answers a node
    /// falling into Fault and the other a moisture reading drifting low.
    /// </remarks>
    public double SeverityWeight { get; set; } = 0.25;

    /// <summary>The weight of the trigger's recency: fresher triggers rank higher.</summary>
    /// <remarks>
    /// The smallest, because it only breaks ties between suggestions otherwise equal. A suggestion does not become wrong as
    /// it ages, it becomes less likely to still be wanted, and the pattern window already ends it.
    /// </remarks>
    public double RecencyWeight { get; set; } = 0.15;

    /// <summary>The lift at which the lift term is full. Lift above it counts as much as lift at it.</summary>
    public double LiftCap { get; set; } = 10;

    /// <summary>How severe each kind of trigger is, from 0 to 1.</summary>
    public SeverityOptions Severity { get; set; } = new();

    /// <summary>The most live triggers held at once. The oldest are dropped past it.</summary>
    /// <remarks>Bounds the cost of a recomputation, which is proportional to the live triggers. A flood is the case it is for.</remarks>
    public int MaxLiveTriggers { get; set; } = 500;

    /// <summary>How many devices one similar-devices suggestion is turned into at most.</summary>
    /// <remarks>
    /// "A pump in Zone 1" has to become something that can be clicked, which is a particular pump. Offering all of them would
    /// fill the panel with one idea, so the first few, in address order, are offered.
    /// </remarks>
    public int MaxDevicesPerGeneralSuggestion { get; set; } = 3;

    /// <summary>How often live triggers are checked for having aged out, and the ranking refreshed.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>What a pattern's weight is multiplied by each time an operator marks one of its suggestions "Not useful".</summary>
    /// <remarks>The same rule as an investigation's Dismiss: small steps, a floor, and reversible.</remarks>
    public double NotUsefulFactor { get; set; } = 0.85;

    /// <summary>The lowest a pattern's weight can go, however often it is marked "Not useful".</summary>
    /// <remarks>
    /// One half, as for investigations. Feedback can quieten a pattern and cannot silence it, so a habit that is real but
    /// annoying on one afternoon still shows up when it matters.
    /// </remarks>
    public double NotUsefulFloor { get; set; } = 0.5;

    /// <summary>How many dismissed suggestions are kept so the mark can be taken back.</summary>
    public int MaxDismissedRetained { get; set; } = 20;

    /// <summary>How long a suggestion's acceptance waits for the action it announced before it is forgotten.</summary>
    /// <remarks>
    /// Accepting registers that the next matching action is the operator following advice. If the action is then refused
    /// (a zone command that needs confirming, say), the registration must not linger and mislabel a later action of their own.
    /// </remarks>
    public TimeSpan AcceptanceExpiry { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>How severe each kind of trigger is, from 0 (negligible) to 1 (the most urgent there is).</summary>
public sealed class SeverityOptions
{
    /// <summary>A reading crossed a watch level. Worth a look; the sensor is behaving, the world moved.</summary>
    public double ThresholdCrossing { get; set; } = 0.4;

    /// <summary>An alert was raised.</summary>
    public double AlertRaised { get; set; } = 0.6;

    /// <summary>A node's trust fell into Degrading.</summary>
    public double Degrading { get; set; } = 0.6;

    /// <summary>A reading was critical.</summary>
    public double CriticalReading { get; set; } = 0.9;

    /// <summary>A node's trust fell into Fault.</summary>
    public double Fault { get; set; } = 1.0;
}

/// <summary>Checks the suggestion options at startup.</summary>
internal static class SuggestionOptionsValidation
{
    public static IEnumerable<string> Problems(SuggestionOptions options)
    {
        if (options.MaxSuggestions < 1)
        {
            yield return "Suggestions:MaxSuggestions must be at least 1.";
        }

        if (options.LiftWeight < 0 || options.ConfidenceWeight < 0 || options.SeverityWeight < 0 || options.RecencyWeight < 0
            || options.LiftWeight + options.ConfidenceWeight + options.SeverityWeight + options.RecencyWeight <= 0)
        {
            yield return "Suggestions: the four ranking weights cannot be negative and cannot all be zero.";
        }

        if (options.LiftCap <= 1)
        {
            yield return "Suggestions:LiftCap must be above 1.";
        }

        if (options.MaxLiveTriggers < 1 || options.MaxDevicesPerGeneralSuggestion < 1 || options.MaxDismissedRetained < 0)
        {
            yield return "Suggestions:MaxLiveTriggers and MaxDevicesPerGeneralSuggestion must be at least 1.";
        }

        if (options.SweepInterval <= TimeSpan.Zero || options.AcceptanceExpiry <= TimeSpan.Zero)
        {
            yield return "Suggestions:SweepInterval and AcceptanceExpiry must be positive.";
        }

        if (options.NotUsefulFactor is <= 0 or >= 1)
        {
            yield return "Suggestions:NotUsefulFactor must be above 0 and below 1.";
        }

        if (options.NotUsefulFloor is <= 0 or > 1)
        {
            yield return "Suggestions:NotUsefulFloor must be above 0 and at most 1: zero would let a pattern be silenced.";
        }

        var severities = new[]
        {
            options.Severity.ThresholdCrossing, options.Severity.AlertRaised, options.Severity.Degrading,
            options.Severity.CriticalReading, options.Severity.Fault,
        };

        if (severities.Any(value => value is < 0 or > 1))
        {
            yield return "Suggestions:Severity values must be between 0 and 1.";
        }
    }
}
