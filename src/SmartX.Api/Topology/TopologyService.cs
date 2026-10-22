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

    /// <summary>
    /// Which revision of the drawing this is. It moves only when something a person would see differs from the snapshot before:
    /// see <see cref="TopologyService"/>. Set once, by the service, before the snapshot is published.
    /// </summary>
    public long Revision { get; internal set; }

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
/// <param name="Id">Its number, rising by one with each change.</param>
/// <param name="At">When the rebuild noticed, from the injected clock.</param>
/// <param name="Device">The device whose route changed.</param>
/// <param name="Name">Its label.</param>
/// <param name="OldPath">Its route before, device first and gateway last. Null if it had none.</param>
/// <param name="NewPath">Its route after. Null if it has none now: it was left out of the graph or cut off.</param>
/// <param name="Reason">What changed, in words, such as <c>GATEWAY-2 excluded: Stale</c>.</param>
internal sealed record RouteChange(long Id, DateTimeOffset At, string Device, string Name, IReadOnlyList<string>? OldPath, IReadOnlyList<string>? NewPath, string Reason)
{
    /// <summary>The change as the API and the hub send it.</summary>
    public RouteChangeView ToView() => new(Id, At, Device, Name, OldPath, NewPath, Reason);
}

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

    // Devices an operator has taken offline, and whether they have been brought back. A device taken offline is left out of the graph
    // at once, not after three missed reports; one brought back stays out until it next reports, so it cannot reappear on the strength
    // of a report it sent before it went. Only touched with the lock held.
    private readonly Dictionary<string, bool> _offline = new(StringComparer.Ordinal);

    private TopologySnapshot? _current;
    private long _version;
    private long _changeId;
    private long _revision;
    private string _signature = string.Empty;
    private DateTimeOffset? _firstReportAt;

    private sealed record DeviceReport(string Name, string ZoneId, NeighbourReport Report, DateTimeOffset ReportedAt);

    /// <summary>
    /// Raised, with the lock held, when a new snapshot is a new revision. Handlers must return at once and take no locks of their own.
    /// </summary>
    public event Action? RevisionChanged;

    /// <summary>The newest route change's number, or 0 if there has been none.</summary>
    public long LatestChangeId
    {
        get
        {
            lock (_gate)
            {
                return _changeId;
            }
        }
    }

    /// <summary>
    /// Leaves a device out of the graph straight away, because an operator has taken it offline.
    /// </summary>
    /// <remarks>
    /// Waiting for it to go stale would take three report intervals, ninety seconds by default, to show what an operator has just
    /// done. The reason it gives is the operator's, not a timeout's.
    /// </remarks>
    public void TakeOffline(string macAddress)
    {
        lock (_gate)
        {
            _offline[macAddress] = false;
            _version++;
        }
    }

    /// <summary>The operator has brought a device back. It rejoins the graph with its next report, not before.</summary>
    public void BringBack(string macAddress)
    {
        lock (_gate)
        {
            if (_offline.ContainsKey(macAddress))
            {
                _offline[macAddress] = true;
            }
        }
    }

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

            // A device brought back rejoins with this report.
            if (_offline.TryGetValue(macAddress, out var broughtBack) && broughtBack)
            {
                _offline.Remove(macAddress);
            }

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

                var revisionBefore = _revision;
                var built = Build();
                RecordRouteChanges(built);
                built.Revision = RevisionOf(built);
                Volatile.Write(ref _current, built);

                if (built.Revision != revisionBefore)
                {
                    RevisionChanged?.Invoke();
                }

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

    /// <summary>The route changes recorded after change number <paramref name="afterId"/>, newest first.</summary>
    public IReadOnlyList<RouteChange> RouteChangesAfter(long afterId)
    {
        lock (_gate)
        {
            return [.. _changes.Reverse().TakeWhile(change => change.Id > afterId)];
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
            if (_offline.ContainsKey(mac))
            {
                excluded.Add(new ExcludedNode(mac, "Taken offline by operator", entry.Name, entry.Report.X, entry.Report.Y));

                continue;
            }

            if (now - entry.ReportedAt > staleAfter)
            {
                excluded.Add(new ExcludedNode(mac, "Stale", entry.Name, entry.Report.X, entry.Report.Y));

                continue;
            }

            var known = trust.TryGet(mac, out var score);

            if (known && score!.Band == TrustBand.Fault)
            {
                excluded.Add(new ExcludedNode(mac, "Fault", entry.Name, entry.Report.X, entry.Report.Y));

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
                known ? Math.Round(score!.Score, 1) : TrustFormula.PriorScore));

            claims.AddRange(entry.Report.Neighbours.Select(link => new LinkClaim(mac, link.Id, link.Rssi)));
        }

        return new TopologySnapshot(_version, now, MeshGraph.FromClaims(vertices, claims, _options.PenaltyFactor), excluded);
    }

    /// <summary>
    /// The revision this snapshot is: the one before it if it looks the same to a person, otherwise the next.
    /// </summary>
    /// <remarks>
    /// What counts as looking the same is the drawing: which devices are in the graph and in what band, which links there are, which
    /// of them are in the spanning tree, and who is left out and why. Trust scores, signal strengths and costs are not in it, because
    /// they move all the time and a page that redrew for every drift would never keep still. (The scores that set a link's cost are
    /// also rounded to a tenth when the graph is built, so the tree does not flip between near-equal routes with every wobble.)
    /// </remarks>
    private long RevisionOf(TopologySnapshot built)
    {
        var text = new System.Text.StringBuilder();

        foreach (var vertex in built.Graph.Vertices)
        {
            text.Append(vertex.Id).Append(':').Append(vertex.Band).Append(';');
        }

        text.Append('|');

        foreach (var edge in built.Graph.Edges)
        {
            text.Append(edge.A).Append('-').Append(edge.B).Append(';');
        }

        text.Append('|');

        foreach (var edge in built.Forest.Edges)
        {
            text.Append(edge.A).Append('-').Append(edge.B).Append(';');
        }

        text.Append('|');

        foreach (var excluded in built.Excluded)
        {
            text.Append(excluded.Id).Append(':').Append(excluded.Reason).Append(';');
        }

        var signature = text.ToString();

        if (!string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            _signature = signature;
            _revision++;
        }

        return _revision;
    }

    /// <summary>
    /// Compares a new snapshot's routes with the last one's and records every device whose route is different.
    /// </summary>
    /// <remarks>
    /// The first snapshot has nothing to be compared with, a device seen for the first time has no earlier route, and a device
    /// getting its first route is starting up, so none of those is a change.
    /// <para>
    /// <b>Only a broken route is a change.</b> The tree is worked out again from every snapshot, and trust scores move all the time, so
    /// when two routes cost nearly the same the tree flips between them with each wobble. The first version of this recorded every
    /// flip: seventy-nine changes in forty seconds on a healthy mesh, every one of them "a link on the route changed". A route is only
    /// healing if what it was made of has gone, a device left the graph or a link was lost, so a new path is recorded only when the old
    /// one no longer holds. The path is still tracked when it changes without breaking, so the next comparison is with the current one.
    /// </para>
    /// Called with the lock held.
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

            if (!settling && _lastPaths.TryGetValue(mac, out var oldPath) && !SamePath(oldPath, newPath)
                && (oldPath is null ? _routedBefore.Contains(mac) : !PathHolds(oldPath, built.Graph)))
            {
                _changes.Add(new RouteChange(++_changeId, built.BuiltAt, mac, entry.Name, oldPath, newPath, ReasonFor(mac, oldPath, newPath, built)));
            }

            _lastPaths[mac] = newPath;

            if (newPath is not null)
            {
                _routedBefore.Add(mac);
            }
        }
    }

    /// <summary>Whether every vertex and every link on a path is still in the graph.</summary>
    private static bool PathHolds(IReadOnlyList<string> path, MeshGraph graph)
    {
        for (var i = 0; i < path.Count; i++)
        {
            if (!graph.Contains(path[i]) || (i > 0 && !graph.NeighboursOf(path[i - 1]).Any(link => link.To == path[i])))
            {
                return false;
            }
        }

        return true;
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

        // Every vertex is still there, so a link between two of them has gone: the first one on the old route that no longer exists.
        var lost = Enumerable.Range(1, oldPath.Count - 1).First(i => !now.Graph.NeighboursOf(oldPath[i - 1]).Any(link => link.To == oldPath[i]));
        var link = $"link {oldPath[lost - 1]} to {oldPath[lost]} lost";

        return newPath is null ? $"{link}; {device} cut off" : link;
    }
}
