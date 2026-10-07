using SmartX.Shared;

namespace SmartX.Api.Ingestion;

/// <summary>
/// One rule that makes a reading critical: a limit it must not cross for a given category and unit.
/// </summary>
/// <remarks>
/// A rule names both a category and a unit because the same category reports in several units, and the
/// limits are not comparable across them: 180 is an emergency in kW and nothing at all in W.
/// <para>
/// The limits sit <i>inside</i> the range sensors are registered for, which is the whole point. A reading
/// outside its registered range is invalid and is rejected at the edge; it never reaches a queue. A critical
/// reading is a perfectly valid one that happens to be alarming, and that is what has to bypass the backlog.
/// </para>
/// </remarks>
/// <param name="Category">The kind of device the rule applies to.</param>
/// <param name="Unit">The unit it applies to, matched exactly.</param>
/// <param name="Above">Readings above this are critical. Null when the rule has no upper limit.</param>
/// <param name="Below">Readings below this are critical. Null when the rule has no lower limit.</param>
/// <param name="Severity">
/// Lower is more urgent. The priority queue orders by this first, so a rule can be escalated without
/// touching code.
/// </param>
/// <param name="Reason">Said in the log and the alert: "power spike", "moisture crash".</param>
public sealed record CriticalRule(
    SensorCategory Category,
    string Unit,
    double? Above = null,
    double? Below = null,
    int Severity = 1,
    string Reason = "critical threshold crossed");

/// <summary>
/// Configuration for the ingestion queues: how deep they go, when the worker must let a normal reading
/// through, and which readings count as critical.
/// </summary>
public sealed class IngestionOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Ingestion";

    /// <summary>The most readings the standard lane will hold before refusing more.</summary>
    /// <remarks>
    /// The bound is the point of the queue. Without one, a gateway that cannot keep up simply grows its
    /// backlog until the process dies, having accepted thousands of readings it never processed. Refusing
    /// with 503 tells the device to come back, which is a thing a device knows how to do.
    /// </remarks>
    public int MaxQueueDepth { get; set; } = 10_000;

    /// <summary>The most readings the critical lane will hold.</summary>
    /// <remarks>
    /// Smaller, and bounded separately, so a flood of alerts cannot consume the capacity that ordinary
    /// telemetry needs — and so a full standard lane never blocks an alert.
    /// </remarks>
    public int MaxCriticalQueueDepth { get; set; } = 1_000;

    /// <summary>What to put in the Retry-After header when a lane is full.</summary>
    public int RetryAfterSeconds { get; set; } = 5;

    /// <summary>
    /// How many critical readings the worker may process in a row before it must take one normal reading.
    /// </summary>
    /// <remarks>
    /// The anti-starvation valve. Strict priority is defensible right up until criticals never stop
    /// arriving, at which point the standard lane is never drained and ordinary telemetry is lost while the
    /// dashboard looks busy. Twenty keeps alerts effectively immediate — at the measured drain rate the
    /// detour costs a normal reading's processing time, under a millisecond — while guaranteeing the
    /// standard lane always moves.
    /// </remarks>
    public int MaxConsecutiveCritical { get; set; } = 20;

    /// <summary>
    /// How long the worker pauses after each reading it processes. Zero, and meant to stay zero outside a demonstration.
    /// </summary>
    /// <remarks>
    /// The real worker drains the queue faster than the simulated fleet can fill it, so the standard lane is empty and the
    /// critical lane has nothing to overtake. That is the right behaviour and a poor demonstration. This gives the worker a
    /// per-reading cost, as a slower store or a busier machine would, so a backlog can be built on purpose and the priority
    /// lane can be watched jumping it. It is configuration and not a code path of its own: the worker is otherwise
    /// unchanged, and nothing is slowed unless this is set.
    /// </remarks>
    public TimeSpan DemoProcessingDelay { get; set; } = TimeSpan.Zero;

    /// <summary>The rules that classify a reading as critical.</summary>
    public IList<CriticalRule> CriticalRules { get; set; } =
    [
        new(SensorCategory.PowerConsumption, "W", Above: 5000, Severity: 1, Reason: "power spike"),
        new(SensorCategory.PowerConsumption, "kW", Above: 180, Severity: 1, Reason: "power spike"),
        new(SensorCategory.Environmental, "%RH", Below: 35, Severity: 1, Reason: "moisture crash"),
        new(SensorCategory.Environmental, "°C", Above: 40, Below: -27, Severity: 2, Reason: "temperature excursion"),
        new(SensorCategory.Environmental, "ppm", Above: 2200, Severity: 2, Reason: "air quality"),
    ];
}
