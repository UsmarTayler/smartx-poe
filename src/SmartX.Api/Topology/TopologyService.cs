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
/// Reachability and articulation points are computed the first time they are asked for, then kept, so a page that asks for the
/// graph, the routes and the weak points does the traversals once per snapshot and not once per request.
/// </para>
/// </remarks>
internal sealed class TopologySnapshot
{
    private readonly Lazy<Reachability> _reachability;
    private readonly Lazy<IReadOnlyList<ArticulationPoint>> _articulation;

    public TopologySnapshot(long version, DateTimeOffset builtAt, MeshGraph graph, IReadOnlyList<ExcludedNode> excluded)
    {
        Version = version;
        BuiltAt = builtAt;
        Graph = graph;
        Excluded = excluded;
        _reachability = new Lazy<Reachability>(() => MeshTraversal.NearestGateways(graph), LazyThreadSafetyMode.ExecutionAndPublication);
        _articulation = new Lazy<IReadOnlyList<ArticulationPoint>>(() => MeshTraversal.ArticulationPoints(graph), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Which state of the reports this was built from.</summary>
    public long Version { get; }

    public DateTimeOffset BuiltAt { get; }

    public MeshGraph Graph { get; }

    public IReadOnlyList<ExcludedNode> Excluded { get; }

    public Reachability Reachability => _reachability.Value;

    public IReadOnlyList<ArticulationPoint> ArticulationPoints => _articulation.Value;
}

/// <summary>
/// Holds what devices have reported about their neighbours and turns it into the mesh graph.
/// </summary>
/// <remarks>
/// A device's report replaces its last one. A device leaves the graph when it has been silent for
/// <see cref="TopologyOptions.StaleAfterIntervals"/> intervals, or when its trust is in the Fault band: a route planned through a
/// device that is not reporting, or is not believed, is a route that fails. Both are checked against the injected
/// <see cref="TimeProvider"/> and the trust engine each time a snapshot is built.
/// </remarks>
internal sealed class TopologyService(ITrustEngine trust, TimeProvider time, IOptions<TopologyOptions> options)
{
    private readonly TopologyOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceReport> _reports = new(StringComparer.Ordinal);

    private TopologySnapshot? _current;
    private long _version;

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
                Volatile.Write(ref _current, built);

                return built;
            }
        }
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
}
