namespace SmartX.Shared;

/// <summary>A gateway: a fixed point of the mesh where packets leave it. Infrastructure, not a scored device.</summary>
/// <param name="Id">The fixed id devices report it under.</param>
/// <param name="Name">A label.</param>
/// <param name="X">Position on the plan, in metres.</param>
/// <param name="Y">Position on the plan, in metres.</param>
public sealed record MeshGateway(string Id, string Name, double X, double Y);

/// <summary>The three gateways of the demo deployment. Fixed, so a device and the API agree on them without being told.</summary>
public static class MeshGateways
{
    /// <summary>The gateways, in id order.</summary>
    public static IReadOnlyList<MeshGateway> All { get; } =
    [
        new("GATEWAY-1", "Gateway 1 – Production", 30, 32),
        new("GATEWAY-2", "Gateway 2 – Cold Store", 55, 65),
        new("GATEWAY-3", "Gateway 3 – Dock and Offices", 135, 40),
    ];

    /// <summary>Whether an id names a gateway.</summary>
    public static bool IsGateway(string id) => All.Any(gateway => string.Equals(gateway.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One neighbour a device will report, from that device's point of view.</summary>
/// <param name="To">A device's MAC address or a gateway's id.</param>
/// <param name="Rssi">What this device hears it at, in dBm.</param>
public sealed record PlannedLink(string To, int Rssi);

/// <summary>Where every device sits and who it can hear, decided once from a seed.</summary>
public sealed class MeshPlan
{
    private readonly Dictionary<string, (double X, double Y)> _positions;
    private readonly Dictionary<string, List<PlannedLink>> _tables;

    internal MeshPlan(Dictionary<string, (double X, double Y)> positions, Dictionary<string, List<PlannedLink>> tables)
    {
        _positions = positions;
        _tables = tables;
    }

    /// <summary>Where a device or gateway is, or null if the plan does not know it.</summary>
    public (double X, double Y)? PositionOf(string id) => _positions.TryGetValue(id, out var position) ? position : null;

    /// <summary>What a device would report if everything were up: its neighbours and how strongly it hears each.</summary>
    public IReadOnlyList<PlannedLink> TableOf(string macAddress) => _tables.TryGetValue(macAddress, out var table) ? table : [];

    /// <summary>The devices in the plan.</summary>
    public IEnumerable<string> Devices => _tables.Keys;
}

/// <summary>
/// Lays the fleet out on a plan and decides who can hear whom. The simulator uses it to know what to report; the tests use it to
/// know what a sensible mesh looks like.
/// </summary>
/// <remarks>
/// <b>Fixed seed, same mesh every start.</b> Everything random here comes from one <see cref="Random"/> built from the seed (and a
/// stable hash for per-link jitter, because <c>string.GetHashCode</c> differs between runs), so the picture on a marker's screen is
/// the picture the demo script describes.
/// <para>
/// <b>How the links are chosen.</b> Each device hears its two to four nearest devices and the nearest gateway if one is within
/// <see cref="GatewayRange"/>. That alone leaves zones as islands, so the nearest pair of islands is then joined, repeatedly, until
/// there is one network (each join is reported by one of its two ends). Those joins are single links, so the devices at their ends
/// are single points of failure: left in on purpose, because a mesh with no weak spot would have nothing for the articulation-point
/// search to find.
/// </para>
/// <para>
/// Signal strength falls linearly from -30 dBm at the antenna to -90 dBm at <see cref="MaxRange"/> and stays there beyond it, with
/// up to 3 dB of jitter per direction, so two ends of one link report slightly different values and the weaker-end rule has
/// something to decide.
/// </para>
/// </remarks>
public static class MeshLayout
{
    /// <summary>The seed the demo fleet is laid out from.</summary>
    public const int DefaultSeed = 1729;

    /// <summary>How far a gateway reaches, in metres.</summary>
    public const double GatewayRange = 30;

    /// <summary>The distance at which a link is as weak as the scale goes, in metres.</summary>
    public const double MaxRange = 40;

    /// <summary>Where each zone's devices are scattered around, in metres on the plan. Facility A on the left, facility B on the right.</summary>
    private static readonly Dictionary<string, (double X, double Y)> ZoneCentres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sub-zone-b"] = (15, 15),
        ["sub-zone-c"] = (15, 45),
        ["zone-1"] = (38, 30),
        ["sub-zone-d"] = (65, 15),
        ["sub-zone-e"] = (65, 45),
        ["zone-2"] = (85, 30),
        ["sub-zone-f"] = (15, 80),
        ["sub-zone-g"] = (42, 80),
        ["zone-3"] = (28, 95),
        ["zone-4"] = (115, 15),
        ["sub-zone-h"] = (140, 15),
        ["zone-5"] = (115, 50),
        ["sub-zone-j"] = (140, 50),
        ["sub-zone-k"] = (165, 50),
        ["zone-6"] = (160, 85),
    };

    /// <summary>Lays out a fleet.</summary>
    public static MeshPlan Create(IEnumerable<DemoDevice> devices, int seed = DefaultSeed)
    {
        var fleet = devices.ToList();
        var random = new Random(seed);
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);

        foreach (var gateway in MeshGateways.All)
        {
            positions[gateway.Id] = (gateway.X, gateway.Y);
        }

        foreach (var device in fleet)
        {
            var zone = device.Registration.ZoneId;
            var centre = ZoneCentres.TryGetValue(zone, out var known) ? known : (X: 100d, Y: 100d);
            var angle = random.NextDouble() * 2 * Math.PI;
            var radius = 2 + (random.NextDouble() * 8);

            positions[device.MacAddress] = (
                Math.Round(centre.X + (radius * Math.Cos(angle)), 1),
                Math.Round(centre.Y + (radius * Math.Sin(angle)), 1));
        }

        var tables = fleet.ToDictionary(device => device.MacAddress, _ => new List<PlannedLink>(), StringComparer.Ordinal);
        var joined = new UnionFind(positions.Keys);

        void Hear(string reporter, string heard)
        {
            if (tables[reporter].Any(link => link.To == heard))
            {
                return;
            }

            tables[reporter].Add(new PlannedLink(heard, RssiOf(seed, reporter, heard, Distance(positions, reporter, heard))));
            joined.Union(reporter, heard);
        }

        foreach (var device in fleet)
        {
            var nearest = fleet
                .Where(other => other.MacAddress != device.MacAddress)
                .OrderBy(other => Distance(positions, device.MacAddress, other.MacAddress))
                .ThenBy(other => other.MacAddress, StringComparer.Ordinal)
                .Take(2 + random.Next(3));

            foreach (var other in nearest)
            {
                Hear(device.MacAddress, other.MacAddress);
            }

            var gateway = MeshGateways.All
                .OrderBy(candidate => Distance(positions, device.MacAddress, candidate.Id))
                .First();

            if (Distance(positions, device.MacAddress, gateway.Id) <= GatewayRange)
            {
                Hear(device.MacAddress, gateway.Id);
            }
        }

        // Join the islands. Each pass finds the closest pair of vertices in different islands where at least one is a device (a
        // gateway cannot report), joins them, and repeats. This is Kruskal's idea applied to islands, and it ends with one network.
        var vertices = positions.Keys.Order(StringComparer.Ordinal).ToList();

        while (joined.Components > 1)
        {
            (string Reporter, string Heard, double Distance)? best = null;

            foreach (var first in fleet.Select(device => device.MacAddress).Order(StringComparer.Ordinal))
            {
                foreach (var second in vertices)
                {
                    if (first == second || joined.Connected(first, second))
                    {
                        continue;
                    }

                    var distance = Distance(positions, first, second);

                    if (best is null || distance < best.Value.Distance)
                    {
                        best = (first, second, distance);
                    }
                }
            }

            Hear(best!.Value.Reporter, best.Value.Heard);
        }

        return new MeshPlan(positions, tables);
    }

    private static double Distance(Dictionary<string, (double X, double Y)> positions, string a, string b)
    {
        var (ax, ay) = positions[a];
        var (bx, by) = positions[b];

        return Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2));
    }

    /// <summary>What <paramref name="reporter"/> hears <paramref name="heard"/> at: falling with distance, plus a little jitter.</summary>
    private static int RssiOf(int seed, string reporter, string heard, double distance)
    {
        var strength = LinkCost.StrongestRssi - ((LinkCost.StrongestRssi - LinkCost.WeakestRssi) * Math.Min(distance / MaxRange, 1));

        // Jitter from a hash of the pair and the seed, not from the shared Random: adding a device must not change every other link.
        var jitter = (StableHash($"{seed}|{reporter}|{heard}") % 7) - 3;

        return (int)Math.Clamp(Math.Round(strength + jitter), LinkCost.WeakestRssi, LinkCost.StrongestRssi);
    }

    /// <summary>FNV-1a. <c>string.GetHashCode</c> is randomised per process, which would make the layout different on every start.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;

        foreach (var character in text)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash;
    }

    /// <summary>Tracks which vertices are already connected, and how many separate networks remain.</summary>
    private sealed class UnionFind
    {
        private readonly Dictionary<string, string> _parent;

        public UnionFind(IEnumerable<string> ids)
        {
            _parent = ids.ToDictionary(id => id, id => id, StringComparer.Ordinal);
            Components = _parent.Count;
        }

        public int Components { get; private set; }

        public bool Connected(string a, string b) => Find(a) == Find(b);

        public void Union(string a, string b)
        {
            var (rootA, rootB) = (Find(a), Find(b));

            if (rootA != rootB)
            {
                _parent[rootA] = rootB;
                Components--;
            }
        }

        private string Find(string id)
        {
            while (_parent[id] != id)
            {
                _parent[id] = _parent[_parent[id]];
                id = _parent[id];
            }

            return id;
        }
    }
}
