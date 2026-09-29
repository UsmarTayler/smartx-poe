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

/// <summary>
/// A snapshot of the ingestion pipeline, for the dashboard and for proving the critical lane bypasses
/// the standard one.
/// </summary>
/// <param name="Lanes">One entry per lane, critical first.</param>
/// <param name="Capacity">The configured maximum depth of the standard lane.</param>
/// <param name="RejectedWhenFull">How many readings were refused with 503 because the lane was full.</param>
/// <param name="SampledAt">When this snapshot was taken.</param>
public sealed record PipelineStats(
    IReadOnlyList<LaneStats> Lanes,
    int Capacity,
    long RejectedWhenFull,
    DateTimeOffset SampledAt)
{
    /// <summary>Readings waiting across every lane.</summary>
    public int TotalDepth => Lanes.Sum(lane => lane.Depth);

    /// <summary>Readings processed across every lane since start-up.</summary>
    public long TotalProcessed => Lanes.Sum(lane => lane.Processed);
}
