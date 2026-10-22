namespace SmartX.Shared;

/// <summary>A minimum spanning forest of the mesh: one tree per gateway, covering every device that can reach a gateway.</summary>
/// <param name="Edges">The links kept, each once, in id order.</param>
/// <param name="TotalWeight">The sum of the costs of <paramref name="Edges"/>.</param>
/// <param name="WeightByGateway">The total cost of each gateway's tree, keyed by gateway id. Every gateway has an entry; an unused one is 0.</param>
/// <param name="Parent">For each vertex, the next vertex on its way to the gateway along the tree. A gateway has no entry.</param>
/// <param name="GatewayOf">For each vertex in the forest (gateways included), the gateway at the root of its tree.</param>
public sealed record SpanningForest(
    IReadOnlyList<MeshEdge> Edges,
    double TotalWeight,
    IReadOnlyDictionary<string, double> WeightByGateway,
    IReadOnlyDictionary<string, string> Parent,
    IReadOnlyDictionary<string, string> GatewayOf);

/// <summary>A device's path to its gateway.</summary>
/// <param name="Device">The device.</param>
/// <param name="Path">Every vertex from the device to the gateway, both included.</param>
/// <param name="Gateway">The gateway the path ends at.</param>
/// <param name="Cost">The sum of the costs of the links along the path.</param>
public sealed record MeshRoute(string Device, IReadOnlyList<string> Path, string Gateway, double Cost)
{
    /// <summary>How many links the path crosses.</summary>
    public int Hops => Path.Count - 1;
}

/// <summary>
/// Spanning trees and shortest paths over a <see cref="MeshGraph"/>: Prim's and Kruskal's algorithms for the cheapest way to connect
/// every device to a gateway, and Dijkstra's for the cheapest way for each device on its own.
/// </summary>
/// <remarks>
/// <b>What a minimum spanning tree is for here.</b> The network's backbone: the set of links that connects every device to a gateway
/// at the lowest total cost, so the least radio power and airtime is spent keeping the whole mesh connected. It is not the cheapest
/// route for each device. A device's cheapest route might use a link that the cheapest <i>network</i> leaves out, because dropping that
/// link saves more across the other devices than it costs this one. <see cref="ShortestCosts"/> exists to show that difference, not to
/// route by: a device is routed along the tree.
/// </remarks>
public static class MeshSpanning
{
    /// <summary>The id of the virtual root. Not a valid MAC address or gateway id, so it cannot collide with a real vertex.</summary>
    private const string VirtualRoot = "\u0001virtual-root";

    private readonly record struct Candidate(string From, string To, int Rssi, double Cost);

    /// <summary>
    /// Prim's algorithm, with a virtual root joined to every gateway.
    /// </summary>
    /// <remarks>
    /// <b>The algorithm.</b> Start with one vertex in the tree and repeatedly add the cheapest link from the tree to a vertex outside it.
    /// Candidate links wait in a <see cref="PriorityQueue{TElement, TPriority}"/> keyed by cost. This is the <i>lazy</i> version: when a
    /// vertex joins, all its links are queued, including ones to vertices already in the tree, and such an entry is simply skipped when it
    /// is popped, instead of being found and updated in the queue (a heap cannot do that cheaply). Each of the E links is queued at most
    /// twice (once from each end) and each queue operation costs O(log E), so the whole thing is <b>O(E log E)</b>, which is O(E log V)
    /// because E is at most V².
    /// <para>
    /// <b>Several gateways, the standard trick.</b> Prim's grows one tree from one start. To get one tree <i>per gateway</i> that together
    /// cost the least, add a virtual root joined to every gateway at cost 0 and run Prim's from it: the root reaches every gateway for
    /// free, so each gateway is the first thing added beneath it, and every device then joins whichever gateway's tree reaches it most
    /// cheaply. Dropping the root and its free links leaves a forest with one tree per gateway. Because the root links cost nothing, the
    /// forest's weight equals the spanning tree's, and it is minimum among all forests that give each gateway its own tree. No new
    /// algorithm is needed; the problem is reshaped to fit the one that exists.
    /// </para>
    /// <para>
    /// Devices with no path to any gateway (<see cref="MeshTraversal.NearestGateways"/> calls them cut off) are never reached from the
    /// root, so they are not in the result.
    /// </para>
    /// </remarks>
    public static SpanningForest Prim(MeshGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var gateways = graph.Vertices.Where(vertex => vertex.IsGateway).Select(vertex => vertex.Id).ToList();
        var inTree = new HashSet<string>(StringComparer.Ordinal) { VirtualRoot };
        var queue = new PriorityQueue<Candidate, double>();
        var kept = new List<MeshEdge>();

        foreach (var gateway in gateways)
        {
            queue.Enqueue(new Candidate(VirtualRoot, gateway, 0, 0), 0);
        }

        while (queue.TryDequeue(out var candidate, out _))
        {
            // Lazy deletion: a stale entry is a link to a vertex that joined by a cheaper one in the meantime.
            if (!inTree.Add(candidate.To))
            {
                continue;
            }

            if (candidate.From != VirtualRoot)
            {
                kept.Add(new MeshEdge(candidate.From, candidate.To, candidate.Rssi, candidate.Cost));
            }

            foreach (var link in graph.NeighboursOf(candidate.To))
            {
                if (!inTree.Contains(link.To))
                {
                    queue.Enqueue(new Candidate(candidate.To, link.To, link.Rssi, link.Cost), link.Cost);
                }
            }
        }

        return Describe(graph, kept);
    }

    /// <summary>
    /// Kruskal's algorithm, the independent check on <see cref="Prim"/>.
    /// </summary>
    /// <remarks>
    /// <b>The algorithm.</b> Sort every link by cost and take them cheapest first, keeping a link unless its two ends are already
    /// connected (which would close a cycle). A <see cref="DisjointSet"/> answers "already connected?" in near-constant time. Sorting
    /// costs O(E log E) and dominates; the union-find work is O(E α(V)), where α is the inverse Ackermann function, below 5 for any size
    /// that fits in memory. Where Prim's grows one tree outward, Kruskal's grows many fragments and merges them, so the two arrive at
    /// the answer by unrelated routes, which is what makes agreement between them evidence that both are right. They can pick different
    /// trees when links tie, but never trees of different weight.
    /// <para>
    /// The gateways are joined to each other up front, which is the virtual root of <see cref="Prim"/> seen from the other side:
    /// treating all gateways as one vertex is the same as joining them to a root at cost 0. Links that touch a device with no path to a
    /// gateway are left out, so the same devices are excluded as in <see cref="Prim"/>.
    /// </para>
    /// </remarks>
    public static SpanningForest Kruskal(MeshGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var cutOff = MeshTraversal.NearestGateways(graph).CutOff.ToHashSet(StringComparer.Ordinal);
        var sets = new DisjointSet(graph.Vertices.Select(vertex => vertex.Id));
        string? firstGateway = null;

        foreach (var gateway in graph.Vertices.Where(vertex => vertex.IsGateway))
        {
            if (firstGateway is null)
            {
                firstGateway = gateway.Id;
            }
            else
            {
                sets.Union(firstGateway, gateway.Id);
            }
        }

        var kept = new List<MeshEdge>();

        foreach (var edge in graph.Edges
            .Where(edge => !cutOff.Contains(edge.A) && !cutOff.Contains(edge.B))
            .OrderBy(edge => edge.Cost)
            .ThenBy(edge => edge.A, StringComparer.Ordinal)
            .ThenBy(edge => edge.B, StringComparer.Ordinal))
        {
            if (sets.Union(edge.A, edge.B))
            {
                kept.Add(edge);
            }
        }

        return Describe(graph, kept);
    }

    /// <summary>
    /// The cheapest total cost from each device to its nearest gateway, by Dijkstra's algorithm run from all gateways at once.
    /// </summary>
    /// <remarks>
    /// <b>The algorithm.</b> Like Prim's, but a vertex is queued at the cost of the whole path to it, not of the last link. Pop the
    /// cheapest unsettled vertex, settle it, and offer each neighbour the path through it; a neighbour already offered something cheaper
    /// ignores the offer. Lazy deletion again: a worse offer is left in the heap and skipped when popped. O(E log E). It needs
    /// non-negative costs, which <see cref="LinkCost"/> guarantees (never below 1). A virtual root is not needed: all gateways start at
    /// cost 0.
    /// </remarks>
    /// <returns>Cost by device id. A device with no path to a gateway has no entry.</returns>
    public static IReadOnlyDictionary<string, double> ShortestCosts(MeshGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, double>();

        foreach (var gateway in graph.Vertices.Where(vertex => vertex.IsGateway))
        {
            best[gateway.Id] = 0;
            queue.Enqueue(gateway.Id, 0);
        }

        while (queue.TryDequeue(out var vertex, out var cost))
        {
            if (!settled.Add(vertex))
            {
                continue;
            }

            foreach (var link in graph.NeighboursOf(vertex))
            {
                var through = cost + link.Cost;

                if (!best.TryGetValue(link.To, out var known) || through < known)
                {
                    best[link.To] = through;
                    queue.Enqueue(link.To, through);
                }
            }
        }

        return best;
    }

    /// <summary>A device's path to its gateway along the spanning forest, or null when it is not in the forest (cut off, or unknown).</summary>
    public static MeshRoute? RouteOf(MeshGraph graph, SpanningForest forest, string device)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(forest);

        if (!forest.GatewayOf.TryGetValue(device, out var gateway) || device == gateway)
        {
            return null;
        }

        var path = new List<string> { device };
        var cost = 0d;

        for (var at = device; forest.Parent.TryGetValue(at, out var next); at = next)
        {
            cost += graph.NeighboursOf(at).First(link => link.To == next).Cost;
            path.Add(next);
        }

        return new MeshRoute(device, path, gateway, cost);
    }

    /// <summary>Works out per-gateway trees, totals and parents from a set of kept links.</summary>
    private static SpanningForest Describe(MeshGraph graph, List<MeshEdge> kept)
    {
        kept.Sort((left, right) =>
        {
            var byA = string.CompareOrdinal(left.A, right.A);

            return byA != 0 ? byA : string.CompareOrdinal(left.B, right.B);
        });

        var adjacent = new Dictionary<string, List<MeshEdge>>(StringComparer.Ordinal);

        foreach (var edge in kept)
        {
            foreach (var end in new[] { edge.A, edge.B })
            {
                if (!adjacent.TryGetValue(end, out var list))
                {
                    adjacent[end] = list = [];
                }

                list.Add(edge);
            }
        }

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var gatewayOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var gateway in graph.Vertices.Where(vertex => vertex.IsGateway))
        {
            gatewayOf[gateway.Id] = gateway.Id;
            weights[gateway.Id] = 0;
            queue.Enqueue(gateway.Id);
        }

        // The forest is a set of trees, each containing exactly one gateway, so walking out from the gateways visits every vertex once.
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var edge in adjacent.GetValueOrDefault(current) ?? [])
            {
                var other = edge.A == current ? edge.B : edge.A;

                if (gatewayOf.ContainsKey(other))
                {
                    continue;
                }

                parent[other] = current;
                gatewayOf[other] = gatewayOf[current];
                weights[gatewayOf[current]] += edge.Cost;
                queue.Enqueue(other);
            }
        }

        return new SpanningForest(kept, kept.Sum(edge => edge.Cost), weights, parent, gatewayOf);
    }
}

/// <summary>
/// A union-find (disjoint-set) structure: which vertices are already connected, in near-constant time.
/// </summary>
/// <remarks>
/// Two refinements make it nearly O(1) per operation. <b>Path compression</b>: while finding a set's root, every vertex passed is
/// pointed straight at the root, so the next lookup is short. <b>Union by rank</b>: when joining two sets, the shallower tree hangs
/// beneath the deeper one, so trees stay shallow. Either alone gives O(log V) amortised; together the cost is O(α(V)), the inverse
/// Ackermann function, which is below 5 for any V that fits in memory.
/// </remarks>
public sealed class DisjointSet
{
    private readonly Dictionary<string, string> _parent;
    private readonly Dictionary<string, int> _rank;

    /// <summary>Starts with every id in a set of its own.</summary>
    public DisjointSet(IEnumerable<string> ids)
    {
        _parent = ids.ToDictionary(id => id, id => id, StringComparer.Ordinal);
        _rank = _parent.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        Count = _parent.Count;
    }

    /// <summary>How many separate sets there are.</summary>
    public int Count { get; private set; }

    /// <summary>The representative of the set an id is in.</summary>
    public string Find(string id)
    {
        var root = id;

        while (_parent[root] != root)
        {
            root = _parent[root];
        }

        // Path compression: everything on the way up now points at the root.
        while (_parent[id] != root)
        {
            var next = _parent[id];
            _parent[id] = root;
            id = next;
        }

        return root;
    }

    /// <summary>Joins the sets of two ids. False when they were already one set.</summary>
    public bool Union(string a, string b)
    {
        var (rootA, rootB) = (Find(a), Find(b));

        if (rootA == rootB)
        {
            return false;
        }

        // Union by rank: the shallower tree goes under the deeper.
        if (_rank[rootA] < _rank[rootB])
        {
            (rootA, rootB) = (rootB, rootA);
        }

        _parent[rootB] = rootA;

        if (_rank[rootA] == _rank[rootB])
        {
            _rank[rootA]++;
        }

        Count--;

        return true;
    }

    /// <summary>Whether two ids are in the same set.</summary>
    public bool Connected(string a, string b) => Find(a) == Find(b);
}
