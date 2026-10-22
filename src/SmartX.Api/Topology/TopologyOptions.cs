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

    /// <summary>How many route changes are kept. The oldest is dropped when a new one arrives.</summary>
    public int RouteChangeHistory { get; set; } = 200;

    /// <summary>How long after the first report no route change is recorded.</summary>
    /// <remarks>
    /// The fleet's first reports arrive over a few seconds, and until the last one is in, every device's route is still improving as its
    /// neighbours appear. That is the mesh being drawn, not healing, and recording it would fill the history with startup noise.
    /// </remarks>
    public TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the snapshot is rebuilt when nobody is reading it. Zero turns the refresh off.</summary>
    /// <remarks>
    /// Without this, a route change is only noticed when someone next reads the mesh, and is stamped with that time. A rebuild every few
    /// seconds notices it when it happens, and costs O(V + E) for a mesh this size.
    /// </remarks>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most neighbours one report may list.</summary>
    public int MaxNeighbours { get; set; } = 16;
}
