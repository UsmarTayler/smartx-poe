namespace SmartX.Shared;

/// <summary>Where a device's packets would leave the mesh.</summary>
/// <param name="Gateway">The nearest gateway, by hops.</param>
/// <param name="Hops">How many links away it is.</param>
public sealed record GatewayRoute(string Gateway, int Hops);

/// <summary>For every device that can reach a gateway, the nearest one; and the devices that cannot reach any.</summary>
/// <param name="Routes">Keyed by device id. Gateways are not listed: they are the destination.</param>
/// <param name="CutOff">Devices with no path to any gateway, in id order.</param>
public sealed record Reachability(IReadOnlyDictionary<string, GatewayRoute> Routes, IReadOnlyList<string> CutOff);

/// <summary>A vertex whose failure would split the mesh.</summary>
/// <param name="Id">The vertex.</param>
/// <param name="IsGateway">Whether it is a gateway and not a device.</param>
/// <param name="CutOff">The devices that could reach a gateway and would no longer be able to, in id order.</param>
public sealed record ArticulationPoint(string Id, bool IsGateway, IReadOnlyList<string> CutOff);

/// <summary>
/// Walks a <see cref="MeshGraph"/>: breadth-first from the gateways to find where every device's packets would leave the mesh, and
/// depth-first to find the devices the mesh cannot lose.
/// </summary>
public static class MeshTraversal
{
    /// <summary>
    /// For every device, its nearest gateway and how many hops away it is; and the devices that cannot reach one.
    /// </summary>
    /// <param name="graph">The mesh.</param>
    /// <param name="failed">A vertex to treat as gone, to ask "what if this one failed". Null for the mesh as it is.</param>
    /// <remarks>
    /// <b>Multi-source breadth-first search, O(V + E).</b> Every gateway goes into the <see cref="Queue{T}"/> at distance zero before
    /// the search starts, so the frontier expands from all of them at once. Breadth-first order reaches vertices in order of
    /// distance, so the first time a device is reached is by its nearest gateway, and the hop count at that moment is the shortest.
    /// One search answers the question for every device. Running a separate search from each gateway, or from each device, would
    /// repeat the same work many times over. Each vertex is queued once and each edge examined twice (once from each end), hence
    /// V + E. A device never reached has no path to any gateway: it is cut off. When two gateways are equally near, the one whose id
    /// sorts first wins, because gateways are queued in id order.
    /// </remarks>
    public static Reachability NearestGateways(MeshGraph graph, string? failed = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var reached = new Dictionary<string, GatewayRoute>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var gateway in graph.Vertices.Where(vertex => vertex.IsGateway && vertex.Id != failed))
        {
            reached[gateway.Id] = new GatewayRoute(gateway.Id, 0);
            queue.Enqueue(gateway.Id);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var route = reached[current];

            foreach (var link in graph.NeighboursOf(current))
            {
                if (link.To == failed || reached.ContainsKey(link.To))
                {
                    continue;
                }

                reached[link.To] = new GatewayRoute(route.Gateway, route.Hops + 1);
                queue.Enqueue(link.To);
            }
        }

        var devices = graph.Vertices.Where(vertex => !vertex.IsGateway && vertex.Id != failed).Select(vertex => vertex.Id).ToList();

        return new Reachability(
            devices.Where(reached.ContainsKey).ToDictionary(id => id, id => reached[id], StringComparer.Ordinal),
            [.. devices.Where(id => !reached.ContainsKey(id))]);
    }

    /// <summary>
    /// The vertices whose removal would split the mesh, each with the devices that would lose their route to every gateway.
    /// </summary>
    /// <remarks>
    /// <b>Depth-first search with Tarjan's low-link method, O(V + E).</b> The search numbers vertices in the order it first reaches
    /// them (<c>discovery</c>) and, for each, records the lowest discovery number reachable from its subtree using at most one
    /// edge back up the tree (<c>low</c>). A vertex v is an articulation point if some child c of v has <c>low[c] &gt;= discovery[v]</c>:
    /// nothing in c's subtree can get above v except through v, so removing v strands that subtree. The root is a special case, with no
    /// "above": it is an articulation point only when it has two or more children in the search tree. One pass over each
    /// component finds every articulation point, where removing each vertex in turn and recounting would be O(V × (V + E)).
    /// <para>
    /// <b>Recursion depth.</b> The search recurses once per level of the DFS tree, which is at most V. The mesh is tens to a few
    /// thousand vertices and a frame here is small, so the default stack is far from exhausted; a mesh of hundreds of thousands in a
    /// line would need the explicit-stack form. Neighbours are visited in id order, so the answer does not depend on hash order.
    /// </para>
    /// <para>
    /// The cut-off devices are found by asking <see cref="NearestGateways"/> again with each point removed: O(P × (V + E)) for P
    /// points, which is small because P is small.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ArticulationPoint> ArticulationPoints(MeshGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var before = NearestGateways(graph);

        return [.. FindArticulationVertices(graph).Select(id =>
        {
            graph.TryGetVertex(id, out var vertex);
            var after = NearestGateways(graph, failed: id);
            var cutOff = before.Routes.Keys.Where(device => device != id && !after.Routes.ContainsKey(device)).Order(StringComparer.Ordinal).ToList();

            return new ArticulationPoint(id, vertex.IsGateway, cutOff);
        })];
    }

    /// <summary>The ids of the articulation points of the graph's connected components, in id order. See <see cref="ArticulationPoints"/>.</summary>
    public static IReadOnlyList<string> FindArticulationVertices(MeshGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var discovery = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var points = new HashSet<string>(StringComparer.Ordinal);
        var clock = 0;

        void Visit(string vertex, string? parent)
        {
            discovery[vertex] = low[vertex] = ++clock;
            var children = 0;

            foreach (var link in graph.NeighboursOf(vertex))
            {
                if (link.To == parent)
                {
                    // The edge we came down. There is only one edge per pair, so skipping the parent by id is exact.
                    continue;
                }

                if (discovery.TryGetValue(link.To, out var seen))
                {
                    // A back edge: this subtree can reach something already discovered.
                    low[vertex] = Math.Min(low[vertex], seen);

                    continue;
                }

                children++;
                Visit(link.To, vertex);
                low[vertex] = Math.Min(low[vertex], low[link.To]);

                if (parent is not null && low[link.To] >= discovery[vertex])
                {
                    points.Add(vertex);
                }
            }

            if (parent is null && children > 1)
            {
                points.Add(vertex);
            }
        }

        foreach (var vertex in graph.Vertices)
        {
            if (!discovery.ContainsKey(vertex.Id))
            {
                Visit(vertex.Id, parent: null);
            }
        }

        return [.. points.Order(StringComparer.Ordinal)];
    }
}
