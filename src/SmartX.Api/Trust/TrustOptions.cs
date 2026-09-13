namespace SmartX.Api.Trust;

/// <summary>
/// Everything about trust scoring that an operator might reasonably want to tune.
/// </summary>
/// <remarks>
/// Bound from the "Trust" section of configuration through <c>IOptions</c>. The initial values
/// below only apply when configuration says nothing; appsettings.json states them explicitly so the
/// running values are visible without reading code.
/// </remarks>
public sealed class TrustOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Trust";

    /// <summary>Added to <c>s</c> for each schema-valid, in-range packet.</summary>
    public double SuccessWeight { get; set; } = 1;

    /// <summary>Added to <c>f</c> for a reading outside its registered range.</summary>
    /// <remarks>The mildest failure: the device is alive and speaking the right language.</remarks>
    public double RangeViolationWeight { get; set; } = 1;

    /// <summary>Added to <c>f</c> for a payload of the wrong type.</summary>
    /// <remarks>
    /// Twice a range violation. A drifting value might be a real extreme; a wrong type never is —
    /// it means firmware or configuration disagrees with the registration.
    /// </remarks>
    public double SchemaFailureWeight { get; set; } = 2;

    /// <summary>Added to <c>f</c> for each expected reporting interval that passes in silence.</summary>
    /// <remarks>
    /// The heaviest, because silence is the failure that hides itself. A bad packet at least proves
    /// the node is there; a missing one proves nothing, and on a conventional chart it just looks
    /// like a line that stopped.
    /// </remarks>
    public double MissedHeartbeatWeight { get; set; } = 3;

    /// <summary>The age at which an outcome counts half as much as a fresh one.</summary>
    /// <remarks>
    /// <b>Ten minutes</b>, chosen against two failure modes. Too short, and a node that failed badly
    /// is forgiven within a couple of heartbeats — a flapping device would bounce between bands every
    /// minute and the dashboard ordering would never settle long enough to act on. Too long, and a
    /// node that had a bad hour this morning would still sit near the top of the list this
    /// afternoon, burying whatever is failing now.
    /// <para>
    /// At ten minutes a failure counts half after 10 minutes, a quarter after 20, and 1/64 after an
    /// hour, so a node that misbehaved an hour ago and has been clean since has effectively
    /// recovered. Against the default one-minute heartbeat that is ten reporting intervals per
    /// half-life: long enough to be sure a problem is real, short enough that recovery shows within
    /// the span of an operator's attention.
    /// </para>
    /// </remarks>
    public TimeSpan HalfLife { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How many recent outcomes each node retains for scoring.</summary>
    /// <remarks>
    /// Recency comes from two mechanisms, and whichever is tighter wins. This window bounds the
    /// evidence by count, which caps memory for a node reporting every second. The
    /// <see cref="HalfLife"/> bounds it by time, which still ages the evidence for a slow reporter —
    /// and for a silent one, where no new outcome arrives to push old ones out.
    /// </remarks>
    public int OutcomeWindow { get; set; } = 256;

    /// <summary>How many trust samples each node keeps for its sparkline.</summary>
    public int HistoryLength { get; set; } = 120;

    /// <summary>The minimum spacing between sparkline samples.</summary>
    /// <remarks>
    /// Without a floor, a node sending ten packets a second would fill its whole history in twelve
    /// seconds with near-identical points. A band change is always sampled regardless, so the
    /// sparkline never misses the moment a node fell or recovered.
    /// </remarks>
    public TimeSpan HistorySampleInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many recent samples each node carries in the trust list, for its sparkline.</summary>
    /// <remarks>
    /// Thirty samples at the ten-second spacing is about five minutes of trend — enough to see a node
    /// falling — while keeping the list response small when dozens of nodes are polled every few
    /// seconds. The full series of <see cref="HistoryLength"/> samples stays available per node.
    /// </remarks>
    public int SnapshotHistoryPoints { get; set; } = 30;

    /// <summary>The lowest score still counted as nominal.</summary>
    public double NominalThreshold { get; set; } = 0.75;

    /// <summary>The lowest score not yet counted as a fault.</summary>
    public double FaultThreshold { get; set; } = 0.50;

    /// <summary>The heartbeat interval assumed for a sensor that did not declare one.</summary>
    public TimeSpan DefaultHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the heartbeat monitor sweeps for silent nodes.</summary>
    /// <remarks>
    /// Much shorter than any realistic heartbeat, so a missed interval is noticed within seconds.
    /// A penalty is applied once per missed interval, never once per sweep, so sweeping often does
    /// not punish harder.
    /// </remarks>
    public TimeSpan HeartbeatCheckInterval { get; set; } = TimeSpan.FromSeconds(5);
}
