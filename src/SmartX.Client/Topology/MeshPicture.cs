using SmartX.Shared;

namespace SmartX.Client.Topology;

/// <summary>One device or gateway as the drawing and the table describe it.</summary>
/// <param name="Id">The device's MAC address, or the gateway's id.</param>
/// <param name="Name">Its label.</param>
/// <param name="ZoneId">Its zone, or null for a gateway.</param>
/// <param name="X">Position on the plan, in metres.</param>
/// <param name="Y">Position on the plan, in metres.</param>
/// <param name="IsGateway">Whether it is a gateway.</param>
/// <param name="Band">Its trust band, or null for a gateway or a device that has been left out.</param>
/// <param name="IsWeakPoint">Whether its failure would split the mesh.</param>
/// <param name="CutOffDevices">If it is a weak point, how many devices would lose every route if it failed.</param>
/// <param name="IsCutOff">Whether it has no path to any gateway right now.</param>
/// <param name="LeftOutReason">Why it is not in the graph at all (Stale, Fault, Taken offline by operator), or null if it is.</param>
/// <param name="Gateway">Its nearest gateway, if it has a route.</param>
/// <param name="Hops">How many links to that gateway.</param>
public sealed record PictureNode(
    string Id,
    string Name,
    string? ZoneId,
    double X,
    double Y,
    bool IsGateway,
    TrustBand? Band,
    bool IsWeakPoint,
    int CutOffDevices,
    bool IsCutOff,
    string? LeftOutReason,
    string? Gateway,
    int? Hops)
{
    /// <summary>Whether it is out of the mesh for any reason: left out of the graph, or in it with no way to a gateway.</summary>
    public bool IsUnavailable => IsCutOff || LeftOutReason is not null;

    /// <summary>The reason it is unavailable, in words.</summary>
    public string? Reason => LeftOutReason ?? (IsCutOff ? "No path to any gateway" : null);
}

/// <summary>One link in the drawing.</summary>
/// <param name="A">One end's id.</param>
/// <param name="B">The other end's id.</param>
/// <param name="X1">Where the first end is.</param>
/// <param name="Y1">Where the first end is.</param>
/// <param name="X2">Where the second end is.</param>
/// <param name="Y2">Where the second end is.</param>
/// <param name="Rssi">Signal strength in dBm.</param>
/// <param name="Cost">Routing cost.</param>
/// <param name="InTree">Whether the spanning tree keeps this link.</param>
public sealed record PictureLink(string A, string B, double X1, double Y1, double X2, double Y2, int Rssi, double Cost, bool InTree);

/// <summary>
/// The mesh as it is drawn: every node at its place, every link, and the facts about each that the drawing and the table both show.
/// </summary>
/// <remarks>
/// Built from the four things the API returns (the graph, the spanning tree, reachability and the weak points) so the drawing and the
/// table are made from one object and cannot disagree. Plain data and arithmetic, with nothing of the browser in it, which is what
/// makes it testable without rendering. The drawing is in the plan's own units, metres, and the SVG's <c>viewBox</c> does the scaling,
/// so the picture resizes with its container and nothing here knows how big the window is.
/// </remarks>
public sealed class MeshPicture
{
    /// <summary>Space left round the outermost nodes, in metres, so a node on the edge is not clipped.</summary>
    public const double Margin = 8;

    private MeshPicture(IReadOnlyList<PictureNode> nodes, IReadOnlyList<PictureLink> links, double minX, double minY, double width, double height)
    {
        Nodes = nodes;
        Links = links;
        MinX = minX;
        MinY = minY;
        Width = width;
        Height = height;
    }

    public IReadOnlyList<PictureNode> Nodes { get; }

    public IReadOnlyList<PictureLink> Links { get; }

    /// <summary>The SVG <c>viewBox</c>: left, top, width and height, in metres.</summary>
    public string ViewBox => FormattableString.Invariant($"{MinX:0.##} {MinY:0.##} {Width:0.##} {Height:0.##}");

    public double MinX { get; }

    public double MinY { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>The empty picture, for before anything has loaded.</summary>
    public static MeshPicture Empty { get; } = new([], [], 0, 0, 100, 60);

    /// <summary>A node by id, or null.</summary>
    public PictureNode? Find(string? id) => id is null ? null : Nodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The links that a route runs along, as unordered pairs of ids, for highlighting.</summary>
    public static HashSet<(string, string)> PairsOf(IReadOnlyList<string> path)
    {
        var pairs = new HashSet<(string, string)>();

        for (var i = 0; i < path.Count - 1; i++)
        {
            pairs.Add(Normal(path[i], path[i + 1]));
        }

        return pairs;
    }

    /// <summary>Whether a link is one of a set of pairs, in either direction.</summary>
    public static bool Contains(HashSet<(string, string)> pairs, string a, string b) => pairs.Contains(Normal(a, b));

    private static (string, string) Normal(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>Builds the picture from what the API returned.</summary>
    public static MeshPicture Build(TopologyView topology, MstView mst, ReachabilityView reachability, ArticulationView weakPoints)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(mst);
        ArgumentNullException.ThrowIfNull(reachability);
        ArgumentNullException.ThrowIfNull(weakPoints);

        var tree = mst.Edges.Select(edge => Normal(edge.A, edge.B)).ToHashSet();
        var weak = weakPoints.Points.ToDictionary(point => point.Id, point => point.CutOff.Count, StringComparer.Ordinal);
        var cutOff = reachability.CutOff.ToHashSet(StringComparer.Ordinal);
        var routes = reachability.Routes.ToDictionary(route => route.Id, StringComparer.Ordinal);

        var nodes = new List<PictureNode>();

        foreach (var node in topology.Nodes)
        {
            routes.TryGetValue(node.Id, out var route);

            nodes.Add(new PictureNode(
                node.Id, node.Name, node.ZoneId, node.X, node.Y, node.IsGateway, node.Band,
                weak.ContainsKey(node.Id), weak.GetValueOrDefault(node.Id), cutOff.Contains(node.Id), null,
                node.IsGateway ? node.Id : route?.Gateway, node.IsGateway ? 0 : route?.Hops));
        }

        // A device that has been left out is drawn where it last was, so an operator who took it offline can see it and bring it back.
        foreach (var left in topology.Excluded)
        {
            nodes.Add(new PictureNode(left.Id, left.Name.Length > 0 ? left.Name : left.Id, null, left.X, left.Y, false, null, false, 0, false, left.Reason, null, null));
        }

        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var links = topology.Edges
            .Where(edge => byId.ContainsKey(edge.A) && byId.ContainsKey(edge.B))
            .Select(edge => new PictureLink(
                edge.A, edge.B, byId[edge.A].X, byId[edge.A].Y, byId[edge.B].X, byId[edge.B].Y, edge.Rssi, edge.Cost, tree.Contains(Normal(edge.A, edge.B))))
            .OrderBy(link => link.InTree)   // the tree is drawn last, on top of the faint links
            .ToList();

        if (nodes.Count == 0)
        {
            return Empty;
        }

        var minX = nodes.Min(node => node.X) - Margin;
        var minY = nodes.Min(node => node.Y) - Margin;
        var width = Math.Max(nodes.Max(node => node.X) + Margin - minX, 20);
        var height = Math.Max(nodes.Max(node => node.Y) + Margin - minY, 20);

        return new MeshPicture(nodes, links, minX, minY, width, height);
    }
}
