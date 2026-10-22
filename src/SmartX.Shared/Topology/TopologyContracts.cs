namespace SmartX.Shared;

/// <summary>One neighbour a device can hear.</summary>
/// <param name="Id">The neighbour's MAC address, or a gateway's id.</param>
/// <param name="Rssi">How strongly it hears it, in dBm, from -100 to 0. The mesh scale runs -30 (strong) to -90 (weak).</param>
public sealed record NeighbourLink(string Id, int Rssi);

/// <summary>What a device sends about itself: where it is, and who it can hear.</summary>
/// <param name="X">Its position on the plan, in metres.</param>
/// <param name="Y">Its position on the plan, in metres.</param>
/// <param name="Neighbours">Its neighbour table: every device and gateway in radio range, whole each time.</param>
/// <remarks>
/// The table is the device's whole current view, not a change to apply. A neighbour that has gone is simply absent, so a report
/// replaces the last one and nothing has to be remembered to be removed. Position is reported because a device is installed
/// somewhere and the gateway has no other way to know: it is fixed at installation, and only used to draw the plan.
/// </remarks>
public sealed record NeighbourReport(double X, double Y, IReadOnlyList<NeighbourLink> Neighbours);

/// <summary>A vertex as the topology endpoint describes it.</summary>
/// <param name="Id">The device's MAC address, or the gateway's id.</param>
/// <param name="Name">The room for a device, the gateway's name for a gateway.</param>
/// <param name="ZoneId">The zone, or null for a gateway.</param>
/// <param name="X">Position on the plan, in metres.</param>
/// <param name="Y">Position on the plan, in metres.</param>
/// <param name="Band">The trust band, or null for a gateway.</param>
/// <param name="Trust">The trust score, 0 to 1.</param>
/// <param name="IsGateway">Whether it is a gateway.</param>
public sealed record TopologyNode(string Id, string Name, string? ZoneId, double X, double Y, TrustBand? Band, double Trust, bool IsGateway);

/// <summary>A link as the topology endpoint describes it.</summary>
/// <param name="A">One end.</param>
/// <param name="B">The other end.</param>
/// <param name="Rssi">Signal strength in dBm, the weaker of what the two ends reported.</param>
/// <param name="Cost">The routing cost of the link. See <c>LinkCost</c>.</param>
public sealed record TopologyEdge(string A, string B, int Rssi, double Cost);

/// <summary>A device the mesh knows about and has left out.</summary>
/// <param name="Id">The device.</param>
/// <param name="Reason"><c>Fault</c> when its trust is in the Fault band, <c>Stale</c> when it has not reported for three intervals.</param>
public sealed record ExcludedNode(string Id, string Reason);

/// <summary>The mesh as it is now.</summary>
/// <param name="BuiltAt">When this snapshot was built.</param>
/// <param name="Nodes">Every device and gateway in the mesh, in id order.</param>
/// <param name="Edges">Every link, once.</param>
/// <param name="Excluded">Devices that have reported but are left out, and why.</param>
public sealed record TopologyView(
    DateTimeOffset BuiltAt,
    IReadOnlyList<TopologyNode> Nodes,
    IReadOnlyList<TopologyEdge> Edges,
    IReadOnlyList<ExcludedNode> Excluded);

/// <summary>A device's way out of the mesh.</summary>
/// <param name="Id">The device.</param>
/// <param name="Gateway">The nearest gateway by hops.</param>
/// <param name="Hops">How many links to it.</param>
public sealed record DeviceRoute(string Id, string Gateway, int Hops);

/// <summary>Every device's nearest gateway, and the devices with none.</summary>
/// <param name="BuiltAt">When the snapshot this was computed from was built.</param>
/// <param name="Routes">One per reachable device, in id order.</param>
/// <param name="CutOff">Devices with no path to any gateway.</param>
public sealed record ReachabilityView(DateTimeOffset BuiltAt, IReadOnlyList<DeviceRoute> Routes, IReadOnlyList<string> CutOff);

/// <summary>A device or gateway whose failure would split the mesh.</summary>
/// <param name="Id">The vertex.</param>
/// <param name="Name">Its label.</param>
/// <param name="IsGateway">Whether it is a gateway.</param>
/// <param name="CutOff">The devices that would lose every route to a gateway if it failed.</param>
public sealed record ArticulationPointView(string Id, string Name, bool IsGateway, IReadOnlyList<string> CutOff);

/// <summary>Everything the mesh cannot afford to lose.</summary>
/// <param name="BuiltAt">When the snapshot this was computed from was built.</param>
/// <param name="Points">Each articulation point, in id order. Empty means no single failure splits the mesh.</param>
public sealed record ArticulationView(DateTimeOffset BuiltAt, IReadOnlyList<ArticulationPointView> Points);

/// <summary>One gateway's tree in the spanning forest.</summary>
/// <param name="Gateway">The gateway at the root.</param>
/// <param name="Name">Its label.</param>
/// <param name="Weight">The total cost of the links in its tree.</param>
/// <param name="Devices">How many devices the tree serves.</param>
public sealed record MstTreeView(string Gateway, string Name, double Weight, int Devices);

/// <summary>The backbone: the cheapest set of links that connects every reachable device to a gateway.</summary>
/// <param name="BuiltAt">When the snapshot this was computed from was built.</param>
/// <param name="Edges">The links kept, each once.</param>
/// <param name="Trees">One per gateway.</param>
/// <param name="TotalWeight">The sum of the trees' weights.</param>
/// <param name="CutOff">Devices with no path to any gateway, which are not in the tree.</param>
public sealed record MstView(
    DateTimeOffset BuiltAt,
    IReadOnlyList<TopologyEdge> Edges,
    IReadOnlyList<MstTreeView> Trees,
    double TotalWeight,
    IReadOnlyList<string> CutOff);

/// <summary>One stop on a route.</summary>
/// <param name="Id">The device's MAC address, or the gateway's id.</param>
/// <param name="Name">Its label.</param>
public sealed record RouteHop(string Id, string Name);

/// <summary>A device's route to its gateway along the spanning tree.</summary>
/// <param name="BuiltAt">When the snapshot this was computed from was built.</param>
/// <param name="Device">The device asked about.</param>
/// <param name="CutOff">True when it has no route: it is cut off, or has been left out of the graph.</param>
/// <param name="Reason">When cut off, why: <c>Stale</c>, <c>Fault</c>, <c>No path to any gateway</c> or <c>No neighbour report yet</c>.</param>
/// <param name="Gateway">The gateway the route ends at.</param>
/// <param name="HopCount">How many links it crosses.</param>
/// <param name="MstCost">The cost of the route along the tree.</param>
/// <param name="ShortestCost">
/// The cheapest cost from this device to any gateway by any path, from Dijkstra's algorithm. Never below <paramref name="MstCost"/>
/// when the tree's gateway is the nearest, and often lower: the tree minimises the cost of the whole network, not of each device.
/// </param>
/// <param name="Hops">Every stop, the device first and the gateway last.</param>
public sealed record RouteView(
    DateTimeOffset BuiltAt,
    string Device,
    bool CutOff,
    string? Reason,
    string? Gateway,
    int? HopCount,
    double? MstCost,
    double? ShortestCost,
    IReadOnlyList<RouteHop> Hops);

/// <summary>A device's route changing, as the history records it.</summary>
/// <param name="At">When the mesh was rebuilt and the change noticed.</param>
/// <param name="Device">The device.</param>
/// <param name="Name">Its label.</param>
/// <param name="OldPath">Its route before, device first. Null if it had none.</param>
/// <param name="NewPath">Its route after. Null if it has none now.</param>
/// <param name="Reason">What caused it, such as <c>GATEWAY-2 excluded: Stale</c>.</param>
public sealed record RouteChangeView(
    DateTimeOffset At,
    string Device,
    string Name,
    IReadOnlyList<string>? OldPath,
    IReadOnlyList<string>? NewPath,
    string Reason);
