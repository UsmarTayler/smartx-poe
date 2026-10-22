namespace SmartX.Api.Topology;

/// <summary>Configuration for the mesh graph.</summary>
public sealed class TopologyOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Topology";

    /// <summary>How often a device is expected to report its neighbours. The simulator's <c>NeighbourReportInterval</c> should match.</summary>
    public TimeSpan ReportInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many intervals of silence make a device stale and take it out of the graph.</summary>
    /// <remarks>
    /// Three, as for heartbeats: one missed report is a dropped packet and two is a busy radio, but three in a row is a device that
    /// has stopped. Its links go with it, because a table nobody refreshes is a claim nobody stands behind.
    /// </remarks>
    public int StaleAfterIntervals { get; set; } = 3;

    /// <summary>How hard low trust pushes a link's cost up. See <see cref="SmartX.Shared.LinkCost"/>.</summary>
    public double PenaltyFactor { get; set; } = SmartX.Shared.LinkCost.DefaultPenaltyFactor;

    /// <summary>The oldest a snapshot may be before the next read rebuilds it.</summary>
    /// <remarks>
    /// A report rebuilds the snapshot at the next read, but a device can also go stale, or its trust can fall into Fault, with no
    /// report arriving to say so. A short maximum age catches those, and keeps a busy page from rebuilding on every request.
    /// </remarks>
    public TimeSpan SnapshotMaxAge { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The most neighbours one report may list.</summary>
    public int MaxNeighbours { get; set; } = 16;
}
