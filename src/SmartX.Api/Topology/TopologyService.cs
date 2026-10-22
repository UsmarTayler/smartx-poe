using Microsoft.Extensions.Options;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Topology;

/// <summary>
/// One built view of the mesh: the graph, who was left out of it, and the answers worked out from it.
/// </summary>
/// <remarks>
/// <b>Immutable, and why.</b> Reports arrive on request threads while pages read the mesh and traversals walk it. If readers
/// traversed the table reports are written into, every traversal would need a lock held for its whole O(V + E) walk, and a
/// report would wait behind a slow reader, or the reader would see a graph with half of a report applied. Instead the table is
/// written under a short lock and a <i>new</i> snapshot is built from a copy of it; readers pick up whichever snapshot is current
/// and traverse it without locks, because nothing can change it. The cost is memory for the old snapshot until its last reader
/// finishes, which is small and which the garbage collector pays.
/// <para>
/// Reachability, articulation points, the spanning forest and the shortest costs are computed the first time they are asked for,
/// then kept, so a page that asks for the graph, the routes and the weak points does each traversal once per snapshot and not once
/// per request. The spanning forest belongs to the snapshot it was computed from, so it can never disagree with the graph it is
/// shown beside.
/// </para>
/// </remarks>
internal sealed class TopologySnapshot
{
    private readonly Lazy<Reachability> _reachability;
    private readonly Lazy<IReadOnlyList<ArticulationPoint>> _articulation;
    private readonly Lazy<SpanningForest> _forest;
    private readonly Lazy<IReadOnlyDictionary<string, double>> _shortest;
    private readonly Lazy<IReadOnlyDictionary<string, MeshRoute>> _routes;

    public TopologySnapshot(long version, DateTimeOffset builtAt, MeshGraph graph, IReadOnlyList<ExcludedNode> excluded)
    {
        Version = version;
        BuiltAt = builtAt;
        Graph = graph;
        Excluded = excluded;
        _reachability = new Lazy<Reachability>(() => MeshTraversal.NearestGateways(graph), LazyThreadSafetyMode.ExecutionAndPublication);
        _articulation = new Lazy<IReadOnlyList<ArticulationPoint>>(() => MeshTraversal.ArticulationPoints(graph), LazyThreadSafetyMode.ExecutionAndPublication);
        _forest = new Lazy<SpanningForest>(() => MeshSpanning.Prim(graph), LazyThreadSafetyMode.ExecutionAndPublication);
        _shortest = new Lazy<IReadOnlyDictionary<string, double>>(() => MeshSpanning.ShortestCosts(graph), LazyThreadSafetyMode.ExecutionAndPublication);
        _routes = new Lazy<IReadOnlyDictionary<string, MeshRoute>>(
            () => graph.Vertices
                .Where(vertex => !vertex.IsGateway)
                .Select(vertex => MeshSpanning.RouteOf(graph, _forest.Value, vertex.Id))
                .OfType<MeshRoute>()
                .ToDictionary(route => route.Device, StringComparer.Ordinal),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Which state of the reports this was built from.</summary>
    public long Version { get; }

    public DateTimeOffset BuiltAt { get; }

    public MeshGraph Graph { get; }

    public IReadOnlyList<ExcludedNode> Excluded { get; }

    public Reachability Reachability => _reachability.Value;

    public IReadOnlyList<ArticulationPoint> ArticulationPoints => _articulation.Value;

    /// <summary>The minimum spanning forest, one tree per gateway.</summary>
    public SpanningForest Forest => _forest.Value;

    /// <summary>Each device's cheapest possible cost to a gateway, ignoring the tree. For comparison with the tree's route.</summary>
    public IReadOnlyDictionary<string, double> ShortestCosts => _shortest.Value;

    /// <summary>Every routable device's route along the tree. A cut-off device has no entry.</summary>
    public IReadOnlyDictionary<string, MeshRoute> Routes => _routes.Value;
}

/// <summary>A device's route changed when the mesh was rebuilt.</summary>
/// <param name="At">When the rebuild noticed, from the injected clock.</param>
/// <param name="Device">The device whose route changed.</param>
/// <param name="Name">Its label.</param>
/// <param name="OldPath">Its route before, device first and gateway last. Null if it had none.</param>
/// <param name="NewPath">Its route after. Null if it has none now: it was left out of the graph or cut off.</param>
/// <param name="Reason">What changed, in words, such as <c>GATEWAY-2 excluded: Stale</c>.</param>
internal sealed record RouteChange(DateTimeOffset At, string Device, string Name, IReadOnlyList<string>? OldPath, IReadOnlyList<string>? NewPath, string Reason);

/// <summary>
/// Holds what devices have reported about their neighbours and turns it into the mesh graph.
/// </summary>
/// <remarks>
/// A device's report replaces its last one. A device leaves the graph when it has been silent for
/// <see cref="TopologyOptions.StaleAfterIntervals"/> intervals, or when its trust is in the Fault band: a route planned through a
/// device that is not reporting, or is not believed, is a route that fails. Both are checked against the injected
/// <see cref="TimeProvider"/> and the trust engine each time a snapshot is built.
/// <para>
/// <b>Self-healing.</b> The mesh heals by itself because routes are never stored: they are worked out again from each snapshot. What
/// this adds is a record of it. When a snapshot is built it is compared with the one before, and every device whose route is
/// different gets a <see cref="RouteChange"/> saying what happened. The last <see cref="TopologyOptions.RouteChangeHistory"/> are kept
/// in a bounded ring buffer, because a mesh that is flapping must not be able to grow the record without limit.
/// </para>
/// </remarks>
internal sealed class TopologyService(ITrustEngine trust, TimeProvider time, IOptions<TopologyOptions> options)
{
    private readonly TopologyOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceReport> _reports = new(StringComparer.Ordinal);
    private readonly TelemetryRingBuffer<RouteChange> _changes = new(Math.Max(1, options.Value.RouteChangeHistory));

    // Each device the mesh has ever known, and the route it had in the last snapshot (null if it had none). Compared with the next
    // snapshot to find what changed. Only touched with the lock held.
    private readonly Dictionary<string, IReadOnlyList<string>?> _lastPaths = new(StringComparer.Ordinal);

    // Devices that have had a route at least once. A device getting its first route as the mesh fills in is starting up, not healing.
    private readonly HashSet<string> _routedBefore = new(StringComparer.Ordinal);

    private TopologySnapshot? _current;
    private long _version;
    private DateTimeOffset? _firstReportAt;

    private sealed record DeviceReport(string Name, string ZoneId, NeighbourReport Report, DateTimeOffset ReportedAt);

    /// <summary>Records a device's neighbour table, replacing its last.</summary>
    /// <remarks>
    /// This only marks the snapshot out of date. It does not rebuild it: the whole fleet reports within a few seconds of each other,
    /// and rebuilding the graph per report would do the same O(V + E) work dozens of times for one view. The next read rebuilds once.
    /// </remarks>
    public void Submit(string macAddress, string name, string zoneId, NeighbourReport report)
    {
        lock (_gate)
        {
            _firstReportAt ??= time.GetUtcNow();
            _reports[macAddress] = new DeviceReport(name, zoneId, report, time.GetUtcNow());
            _version++;
        }
    }

    /// <summary>The current snapshot. Rebuilt when reports have changed or it has grown too old; otherwise returned without locking.</summary>
    public TopologySnapshot Current
    {
        get
        {
            var snapshot = Volatile.Read(ref _current);

            if (snapshot is not null && snapshot.Version == Volatile.Read(ref _version) && time.GetUtcNow() - snapshot.BuiltAt < _options.SnapshotMaxAge)
            {
                return snapshot;
            }

            lock (_gate)
            {
                // Someone else may have rebuilt it while this waited for the lock.
                if (_current is { } rebuilt && rebuilt.Version == _version && time.GetUtcNow() - rebuilt.BuiltAt < _options.SnapshotMaxAge)
                {
                    return rebuilt;
                }

                var built = Build();
                RecordRouteChanges(built);
                Volatile.Write(ref _current, built);

                return built;
            }
        }
    }

    /// <summary>The most recent route changes, newest first.</summary>
    public IReadOnlyList<RouteChange> RouteChanges(int limit)
    {
        lock (_gate)
        {
            return [.. _changes.Reverse().Take(limit)];
        }
    }

    /// <summary>Whether this device has ever reported, and the label it reported under.</summary>
    public bool TryGetName(string macAddress, out string name)
    {
        lock (_gate)
        {
            if (_reports.TryGetValue(macAddress, out var report))
            {
                name = report.Name;

                return true;
            }
        }

        name = string.Empty;

        return false;
    }

    /// <summary>Called with the lock held, so the reports cannot change while it reads them.</summary>
    private TopologySnapshot Build()
    {
        var now = time.GetUtcNow();
        var staleAfter = _options.ReportInterval * _options.StaleAfterIntervals;
        var vertices = MeshGateways.All
            .Select(gateway => new MeshVertex(gateway.Id, gateway.Name, null, gateway.X, gateway.Y, true, null, 1))
            .ToList();
        var excluded = new List<ExcludedNode>();
        var claims = new List<LinkClaim>();

        foreach (var (mac, entry) in _reports.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (now - entry.ReportedAt > staleAfter)
            {
                excluded.Add(new ExcludedNode(mac, "Stale"));

                continue;
            }

            var known = trust.TryGet(mac, out var score);

            if (known && score!.Band == TrustBand.Fault)
            {
                excluded.Add(new ExcludedNode(mac, "Fault"));

                continue;
            }

            vertices.Add(new MeshVertex(
                mac,
                entry.Name,
                entry.ZoneId,
                entry.Report.X,
                entry.Report.Y,
                false,
                known ? score!.Band : TrustBand.Unproven,
                known ? score!.Score : TrustFormula.PriorScore));

            claims.AddRange(entry.Report.Neighbours.Select(link => new LinkClaim(mac, link.Id, link.Rssi)));
        }

        return new TopologySnapshot(_version, now, MeshGraph.FromClaims(vertices, claims, _options.PenaltyFactor), excluded);
    }

    /// <summary>
    /// Compares a new snapshot's routes with the last one's and records every device whose route is different.
    /// </summary>
    /// <remarks>
    /// The first snapshot has nothing to be compared with, a device seen for the first time has no earlier route, and a device
    /// getting its first route is starting up, so none of those is a change. A route that is the same list of vertices is not a change however much its cost has drifted: trust scores move all the
    /// time, and a change recorded for every wobble would bury the ones that matter. Called with the lock held.
    /// </remarks>
    private void RecordRouteChanges(TopologySnapshot built)
    {
        var routes = built.Routes;

        // While the fleet's first reports are still arriving the mesh is being drawn, not damaged: every device's route improves as its
        // neighbours appear. Routes are tracked from the start but nothing is recorded until the mesh has had SettleTime to fill in.
        var settling = _firstReportAt is not { } first || built.BuiltAt - first < _options.SettleTime;

        foreach (var (mac, entry) in _reports.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            IReadOnlyList<string>? newPath = routes.TryGetValue(mac, out var route) ? route.Path : null;

            if (!settling && _lastPaths.TryGetValue(mac, out var oldPath) && !SamePath(oldPath, newPath) && (oldPath is not null || _routedBefore.Contains(mac)))
            {
                _changes.Add(new RouteChange(built.BuiltAt, mac, entry.Name, oldPath, newPath, ReasonFor(mac, oldPath, newPath, built)));
            }

            _lastPaths[mac] = newPath;

            if (newPath is not null)
            {
                _routedBefore.Add(mac);
            }
        }
    }

    private static bool SamePath(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null || right is null ? left is null && right is null : left.SequenceEqual(right, StringComparer.Ordinal);

    /// <summary>Says what caused a route to change: the first vertex on the old route that is no longer in the graph, and why it left.</summary>
    private static string ReasonFor(string device, IReadOnlyList<string>? oldPath, IReadOnlyList<string>? newPath, TopologySnapshot now)
    {
        if (oldPath is null)
        {
            return $"{device} can reach a gateway again";
        }

        var missing = oldPath.FirstOrDefault(id => !now.Graph.Contains(id));

        if (missing is not null)
        {
            var why = now.Excluded.FirstOrDefault(excluded => excluded.Id == missing)?.Reason ?? "no longer reporting";
            var text = $"{missing} excluded: {why}";

            return newPath is null && missing != device ? $"{text}; {device} cut off" : text;
        }

        return newPath is null ? $"{device} cut off: a link on its route was lost" : "a link on the route changed";
    }
}
