namespace SmartX.Shared;

/// <summary>
/// What one ingestion lane has been doing.
/// </summary>
/// <param name="Lane">"standard" or "critical".</param>
/// <param name="Depth">Readings waiting in the lane right now.</param>
/// <param name="Processed">Readings the worker has taken from it since start-up.</param>
/// <param name="AverageWaitMilliseconds">Mean time between being accepted and being processed.</param>
/// <param name="MaximumWaitMilliseconds">The longest any single reading waited.</param>
public sealed record LaneStats(
    string Lane,
    int Depth,
    long Processed,
    double AverageWaitMilliseconds,
    double MaximumWaitMilliseconds);

/// <summary>One reading waiting to be taken by the worker, in the position it will be taken in.</summary>
/// <param name="Position">1 is the reading the worker takes next.</param>
/// <param name="Lane">"standard" or "critical".</param>
/// <param name="MacAddress">The device that sent it.</param>
/// <param name="WaitingMilliseconds">How long it has been waiting so far.</param>
public sealed record QueuedItem(int Position, string Lane, string MacAddress, double WaitingMilliseconds);

/// <summary>
/// A snapshot of the ingestion pipeline, for the dashboard and for proving the critical lane bypasses
/// the standard one.
/// </summary>
/// <param name="Lanes">One entry per lane, critical first.</param>
/// <param name="Capacity">The configured maximum depth of the standard lane.</param>
/// <param name="RejectedWhenFull">How many readings were refused with 503 because the lane was full.</param>
/// <param name="SampledAt">When this snapshot was taken.</param>
/// <param name="NextUp">
/// The next few readings the worker will take, whichever lane they are in, in the order it will take them. Optional so a snapshot
/// from an older server, or one built without it, still reads: null means the server did not say.
/// </param>
/// <param name="MaxConsecutiveCritical">
/// How many critical readings the worker takes in a row before it takes one standard reading, so the standard lane is never starved.
/// Zero when the server did not say.
/// </param>
public sealed record PipelineStats(
    IReadOnlyList<LaneStats> Lanes,
    int Capacity,
    long RejectedWhenFull,
    DateTimeOffset SampledAt,
    IReadOnlyList<QueuedItem>? NextUp = null,
    int MaxConsecutiveCritical = 0)
{
    /// <summary>Readings waiting across every lane.</summary>
    public int TotalDepth => Lanes.Sum(lane => lane.Depth);

    /// <summary>Readings processed across every lane since start-up.</summary>
    public long TotalProcessed => Lanes.Sum(lane => lane.Processed);
}
