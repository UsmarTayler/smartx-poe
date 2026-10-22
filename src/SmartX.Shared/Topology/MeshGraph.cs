namespace SmartX.Shared;

/// <summary>One vertex of the mesh: a device or a gateway.</summary>
/// <param name="Id">The device's MAC address, or a gateway's fixed id.</param>
/// <param name="Name">A label for a person: the room for a device, the gateway's name.</param>
/// <param name="ZoneId">The deployment zone, or null for a gateway.</param>
/// <param name="X">Position on the plan, in metres.</param>
/// <param name="Y">Position on the plan, in metres.</param>
/// <param name="IsGateway">Whether this is a gateway, the place a packet is trying to reach.</param>
/// <param name="Band">The trust band, or null for a gateway, which is infrastructure and is not scored.</param>
/// <param name="Trust">The trust score from 0 to 1. A gateway is 1.</param>
public sealed record MeshVertex(string Id, string Name, string? ZoneId, double X, double Y, bool IsGateway, TrustBand? Band, double Trust);

/// <summary>An undirected link between two vertices.</summary>
/// <param name="A">One end.</param>
/// <param name="B">The other end.</param>
/// <param name="Rssi">Signal strength in dBm, from -30 (strong) to -90 (weak).</param>
/// <param name="Cost">What it costs to send a packet across, from <see cref="LinkCost"/>. Lower is better.</param>
public sealed record MeshEdge(string A, string B, int Rssi, double Cost);

/// <summary>One end's view of a link: where it goes and what it costs. An entry in an adjacency list.</summary>
public readonly record struct MeshLink(string To, int Rssi, double Cost);

/// <summary>A neighbour as a device reported it: <paramref name="Reporter"/> heard <paramref name="Neighbour"/> at <paramref name="Rssi"/>.</summary>
public readonly record struct LinkClaim(string Reporter, string Neighbour, int Rssi);

/// <summary>
/// The mesh as a graph: vertices (devices and gateways) joined by undirected, weighted edges, held as an adjacency list.
/// </summary>
/// <remarks>
/// <b>Why an adjacency list.</b> A mesh is sparse: each device hears two to four neighbours, so E is a small multiple of V. An
/// adjacency matrix would spend V² cells to record those few links, and walking a device's neighbours would scan a whole row of
/// mostly empty cells. With a <c>Dictionary&lt;string, List&lt;MeshLink&gt;&gt;</c> the storage is O(V + E), finding a vertex is O(1),
/// and visiting its neighbours costs exactly its degree, which is what makes a full traversal O(V + E).
/// <para>
/// <b>Immutable.</b> Everything is built in the constructor and nothing can change afterwards. That is deliberate: the API builds a
/// new graph when reports change and swaps it in, and readers traverse whichever one they were handed. A traversal that ran while
/// the graph was being edited could see half a change, so the alternative to immutability is a lock held for the whole walk.
/// Neighbour lists are sorted by id so a traversal visits in the same order every time.
/// </para>
/// </remarks>
public sealed class MeshGraph
{
    private static readonly IReadOnlyList<MeshLink> NoLinks = [];

    private readonly Dictionary<string, MeshVertex> _vertices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MeshLink>> _adjacency = new(StringComparer.Ordinal);

    /// <summary>Builds a graph. Edges with an end that is not a vertex, joining a vertex to itself, or repeating a pair are ignored.</summary>
    public MeshGraph(IEnumerable<MeshVertex> vertices, IEnumerable<MeshEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(edges);

        foreach (var vertex in vertices)
        {
            if (_vertices.TryAdd(vertex.Id, vertex))
            {
                _adjacency[vertex.Id] = [];
            }
        }

        var seen = new HashSet<(string, string)>();
        var kept = new List<MeshEdge>();

        foreach (var edge in edges)
        {
            if (edge.A == edge.B || !_vertices.ContainsKey(edge.A) || !_vertices.ContainsKey(edge.B))
            {
                continue;
            }

            // One canonical order per pair, so (A, B) and (B, A) are the same edge.
            var (low, high) = string.CompareOrdinal(edge.A, edge.B) < 0 ? (edge.A, edge.B) : (edge.B, edge.A);

            if (!seen.Add((low, high)))
            {
                continue;
            }

            kept.Add(new MeshEdge(low, high, edge.Rssi, edge.Cost));
            _adjacency[low].Add(new MeshLink(high, edge.Rssi, edge.Cost));
            _adjacency[high].Add(new MeshLink(low, edge.Rssi, edge.Cost));
        }

        foreach (var links in _adjacency.Values)
        {
            links.Sort((left, right) => string.CompareOrdinal(left.To, right.To));
        }

        kept.Sort((left, right) =>
        {
            var byA = string.CompareOrdinal(left.A, right.A);

            return byA != 0 ? byA : string.CompareOrdinal(left.B, right.B);
        });

        Edges = kept;
    }

    /// <summary>Every vertex, in id order.</summary>
    public IEnumerable<MeshVertex> Vertices => _vertices.Values.OrderBy(vertex => vertex.Id, StringComparer.Ordinal);

    /// <summary>Every edge once, with the lower id as <see cref="MeshEdge.A"/>, in id order.</summary>
    public IReadOnlyList<MeshEdge> Edges { get; }

    /// <summary>How many vertices there are.</summary>
    public int VertexCount => _vertices.Count;

    /// <summary>Whether a vertex with this id is in the graph.</summary>
    public bool Contains(string id) => _vertices.ContainsKey(id);

    /// <summary>Finds a vertex.</summary>
    public bool TryGetVertex(string id, out MeshVertex vertex) => _vertices.TryGetValue(id, out vertex!);

    /// <summary>The links of one vertex, in id order. Empty for a vertex that is not in the graph.</summary>
    public IReadOnlyList<MeshLink> NeighboursOf(string id) => _adjacency.TryGetValue(id, out var links) ? links : NoLinks;

    /// <summary>
    /// Turns what devices reported into a graph.
    /// </summary>
    /// <param name="vertices">The vertices that are in the mesh. A claim that names anything else is dropped.</param>
    /// <param name="claims">Each is one device saying it can hear another.</param>
    /// <param name="penaltyFactor">How hard a low-trust end pushes the cost of its links up. See <see cref="LinkCost"/>.</param>
    /// <remarks>
    /// <b>The link rule.</b> A link exists if <i>either</i> end reports it. A device that has gone quiet cannot tell anyone it can
    /// still hear its neighbour, and a neighbour that hears it is enough evidence the radio path is there, so requiring both
    /// ends would delete links to a device the moment it missed a report. When both ends do report it, the <i>weaker</i> of the two
    /// readings is used: a radio link is only as good as its worse direction, because the acknowledgement has to come back.
    /// </remarks>
    public static MeshGraph FromClaims(IEnumerable<MeshVertex> vertices, IEnumerable<LinkClaim> claims, double penaltyFactor)
    {
        var list = vertices.ToList();
        var byId = list.ToDictionary(vertex => vertex.Id, StringComparer.Ordinal);
        var weakest = new Dictionary<(string, string), int>();

        foreach (var (reporter, neighbour, rssi) in claims)
        {
            if (reporter == neighbour || !byId.ContainsKey(reporter) || !byId.ContainsKey(neighbour))
            {
                continue;
            }

            var key = string.CompareOrdinal(reporter, neighbour) < 0 ? (reporter, neighbour) : (neighbour, reporter);

            // Lower (more negative) dBm is weaker.
            weakest[key] = weakest.TryGetValue(key, out var existing) ? Math.Min(existing, rssi) : rssi;
        }

        var edges = weakest.Select(pair => new MeshEdge(
            pair.Key.Item1,
            pair.Key.Item2,
            pair.Value,
            LinkCost.Cost(pair.Value, Math.Min(byId[pair.Key.Item1].Trust, byId[pair.Key.Item2].Trust), penaltyFactor)));

        return new MeshGraph(list, edges);
    }
}
