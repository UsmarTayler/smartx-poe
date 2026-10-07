namespace SmartX.Api.Realtime;

/// <summary>
/// Configuration for pushing to clients: how often batched topics flush, and the limits that keep the push path
/// bounded however many devices report.
/// </summary>
public sealed class RealtimeOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Realtime";

    /// <summary>
    /// How often the batched and coalesced topics (readings, trust, alert repeat counts) are flushed.
    /// </summary>
    /// <remarks>
    /// A quarter of a second: well inside what a person perceives as live, and long enough that a burst of
    /// readings or trust changes becomes one message. Alerts, command changes and critical readings do not wait
    /// for it.
    /// </remarks>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often pipeline statistics are pushed to clients that asked for them.</summary>
    public TimeSpan PipelineInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The most readings held for the next flush. Past this, readings are dropped and counted.
    /// </summary>
    /// <remarks>
    /// A bound, not a target. It is what keeps a flush that cannot keep up, or a burst far beyond normal, from
    /// growing memory without limit; the count of what was dropped travels in the batch, so a stream that is
    /// thinning says so. At the 250 ms default, 10,000 is forty thousand readings a second.
    /// </remarks>
    public int MaxBufferedReadings { get; set; } = 10_000;

    /// <summary>
    /// How far a node's trust score must move before it is worth a message.
    /// </summary>
    /// <remarks>
    /// The heartbeat monitor re-evaluates every node every few seconds, and decay nudges every score a little
    /// each time. Without a threshold that is every node, every sweep, for changes nobody can see on a gauge. A
    /// node is sent at once if its band, its reason or its recent counts changed, whatever its score did.
    /// </remarks>
    public double TrustScoreEpsilon { get; set; } = 0.005;

    /// <summary>How many critical readings a snapshot of the alerts topic carries.</summary>
    public int RecentPriorityAlerts { get; set; } = 20;

    /// <summary>How many commands a snapshot of the commands topic carries.</summary>
    public int SnapshotCommands { get; set; } = 100;

    /// <summary>The most immediate messages queued at once. Past this, the newest are dropped and logged.</summary>
    public int MaxQueuedMessages { get; set; } = 20_000;
}
