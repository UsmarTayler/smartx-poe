using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartX.Api.Topology;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// The spanning tree, the routes along it, shortest paths for comparison, and the record of routes healing.
/// </summary>
/// <remarks>
/// Written to a budget of twelve tests. The two algorithms are checked against each other and against brute force over seeded random
/// graphs, and nothing reads the clock: ageing uses a <see cref="FakeTimeProvider"/>.
/// </remarks>
public class MeshRoutingTests
{
    private const string A = "AA:BB:CC:00:20:01";
    private const string B = "AA:BB:CC:00:20:02";
    private const string C = "AA:BB:CC:00:20:03";
    private const string D = "AA:BB:CC:00:20:04";

    /// <summary>A random graph with random link costs and one to three gateways, possibly in pieces.</summary>
    private static MeshGraph RandomGraph(Random random)
    {
        var count = random.Next(2, 15);
        var gateways = random.Next(1, 4);
        var vertices = Enumerable.Range(0, count)
            .Select(i => new MeshVertex($"v{i:D2}", $"v{i}", null, 0, 0, IsGateway: i < gateways, TrustBand.Nominal, 1))
            .ToList();
        var density = 0.1 + (random.NextDouble() * 0.4);
        var edges = new List<MeshEdge>();

        for (var i = 0; i < count; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                if (random.NextDouble() < density)
                {
                    edges.Add(new MeshEdge(vertices[i].Id, vertices[j].Id, -60, 1 + (random.NextDouble() * 49)));
                }
            }
        }

        return new MeshGraph(vertices, edges);
    }

    /// <summary>The seeded demo layout as a graph, every device up and fully trusted.</summary>
    private static MeshGraph DemoGraph()
    {
        var plan = MeshLayout.Create(DemoDevices.All);
        var vertices = MeshGateways.All
            .Select(gateway => new MeshVertex(gateway.Id, gateway.Name, null, gateway.X, gateway.Y, true, null, 1))
            .Concat(DemoDevices.All.Select(device => new MeshVertex(device.MacAddress, device.Registration.Room, device.Registration.ZoneId, 0, 0, false, TrustBand.Nominal, 1)));

        return MeshGraph.FromClaims(
            vertices,
            DemoDevices.All.SelectMany(device => plan.TableOf(device.MacAddress).Select(link => new LinkClaim(device.MacAddress, link.To, link.Rssi))),
            LinkCost.DefaultPenaltyFactor);
    }

    // ---- the algorithms --------------------------------------------------------------------------------------

    [Fact]
    public void Prims_and_Kruskals_agree_on_the_weight_and_both_are_spanning_forests_on_two_hundred_random_graphs()
    {
        var random = new Random(200);

        for (var round = 0; round < 200; round++)
        {
            var graph = RandomGraph(random);
            var cutOff = MeshTraversal.NearestGateways(graph).CutOff.ToHashSet();
            var reachable = graph.Vertices.Select(vertex => vertex.Id).Where(id => !cutOff.Contains(id)).ToList();
            var gatewayCount = graph.Vertices.Count(vertex => vertex.IsGateway);

            var prim = MeshSpanning.Prim(graph);
            var kruskal = MeshSpanning.Kruskal(graph);

            Assert.True(Math.Abs(prim.TotalWeight - kruskal.TotalWeight) < 1e-9, $"round {round}: Prim {prim.TotalWeight} against Kruskal {kruskal.TotalWeight}.");

            foreach (var (name, forest) in new[] { ("Prim", prim), ("Kruskal", kruskal) })
            {
                // One tree per gateway over every reachable vertex has (vertices - gateways) edges, and none of them closes a cycle.
                Assert.True(forest.Edges.Count == reachable.Count - gatewayCount, $"round {round}: {name} has {forest.Edges.Count} edges for {reachable.Count} vertices and {gatewayCount} gateways.");

                var sets = new DisjointSet(reachable);
                Assert.True(forest.Edges.All(edge => sets.Union(edge.A, edge.B)), $"round {round}: {name} contains a cycle.");
                Assert.True(forest.Edges.All(edge => !cutOff.Contains(edge.A) && !cutOff.Contains(edge.B)), $"round {round}: {name} reaches a cut-off device.");
                Assert.True(reachable.All(forest.GatewayOf.ContainsKey), $"round {round}: {name} leaves a reachable vertex out.");
            }
        }
    }

    [Fact]
    public void Routes_in_the_demo_layout_follow_tree_edges_only_end_at_a_gateway_and_cost_the_sum_of_their_edges()
    {
        var graph = DemoGraph();
        var forest = MeshSpanning.Prim(graph);
        var shortest = MeshSpanning.ShortestCosts(graph);
        var treeEdges = forest.Edges.Select(edge => (edge.A, edge.B)).ToHashSet();
        var devices = graph.Vertices.Where(vertex => !vertex.IsGateway).ToList();
        var costlierThanShortest = 0;

        foreach (var device in devices)
        {
            var route = MeshSpanning.RouteOf(graph, forest, device.Id);

            Assert.NotNull(route);
            Assert.Equal(device.Id, route.Path[0]);
            Assert.True(graph.TryGetVertex(route.Path[^1], out var end) && end.IsGateway, $"{device.Id}: the route ends at {route.Path[^1]}.");

            var sum = 0d;

            for (var i = 0; i < route.Path.Count - 1; i++)
            {
                var (low, high) = string.CompareOrdinal(route.Path[i], route.Path[i + 1]) < 0 ? (route.Path[i], route.Path[i + 1]) : (route.Path[i + 1], route.Path[i]);

                Assert.True(treeEdges.Contains((low, high)), $"{device.Id}: {low} to {high} is not a tree edge.");
                sum += graph.NeighboursOf(route.Path[i]).First(link => link.To == route.Path[i + 1]).Cost;
            }

            Assert.True(Math.Abs(sum - route.Cost) < 1e-9, $"{device.Id}: cost {route.Cost} is not the sum {sum}.");

            // The tree can never beat the cheapest possible path, and sometimes it is dearer: it minimises the network, not each device.
            Assert.True(route.Cost >= shortest[device.Id] - 1e-9, $"{device.Id}: the tree route {route.Cost} is cheaper than the shortest {shortest[device.Id]}.");

            if (route.Cost > shortest[device.Id] + 1e-9)
            {
                costlierThanShortest++;
            }
        }

        Assert.True(costlierThanShortest > 0, "Expected at least one device whose tree route costs more than its shortest path.");
    }

    [Fact]
    public void Dijkstras_costs_match_a_repeated_relaxation_on_two_hundred_small_random_graphs()
    {
        var random = new Random(2000);

        for (var round = 0; round < 200; round++)
        {
            var graph = RandomGraph(random);
            var ids = graph.Vertices.Select(vertex => vertex.Id).ToList();

            // The slow answer: start every gateway at 0 and relax every edge until nothing improves.
            var expected = ids.ToDictionary(id => id, id => graph.TryGetVertex(id, out var vertex) && vertex.IsGateway ? 0d : double.PositiveInfinity);

            for (var pass = 0; pass < ids.Count; pass++)
            {
                foreach (var edge in graph.Edges)
                {
                    expected[edge.B] = Math.Min(expected[edge.B], expected[edge.A] + edge.Cost);
                    expected[edge.A] = Math.Min(expected[edge.A], expected[edge.B] + edge.Cost);
                }
            }

            var actual = MeshSpanning.ShortestCosts(graph);

            foreach (var id in ids)
            {
                if (double.IsInfinity(expected[id]))
                {
                    Assert.True(!actual.ContainsKey(id), $"round {round}: {id} is unreachable but has a cost.");
                }
                else
                {
                    Assert.True(actual.TryGetValue(id, out var cost) && Math.Abs(cost - expected[id]) < 1e-9, $"round {round}: {id} expected {expected[id]}.");
                }
            }
        }
    }

    // ---- self-healing ----------------------------------------------------------------------------------------

    [Fact]
    public void A_route_that_loses_a_device_on_it_heals_and_is_recorded_and_losing_an_articulation_point_cuts_off_what_hangs_from_it()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        var trust = new TrustEngine(Options.Create(new TrustOptions()), clock, NullLogger<TrustEngine>.Instance);
        var topology = new TopologyService(trust, clock, Options.Create(new TopologyOptions { SettleTime = TimeSpan.Zero }));

        // GATEWAY-1 to A and to C; B between A and C; D hangs off C alone. The tree breaks the loop at its dearest link, C to
        // GATEWAY-1, so B and C are both reached through A, and D through C.
        void Report(string mac, params NeighbourLink[] neighbours) => topology.Submit(mac, mac, "z", new NeighbourReport(0, 0, neighbours));

        void ReportEveryone(params string[] except)
        {
            if (!except.Contains(A)) { Report(A, new NeighbourLink("GATEWAY-1", -40), new NeighbourLink(B, -50)); }
            if (!except.Contains(B)) { Report(B, new NeighbourLink(A, -50), new NeighbourLink(C, -55)); }
            if (!except.Contains(C)) { Report(C, new NeighbourLink("GATEWAY-1", -60), new NeighbourLink(B, -55)); }
            if (!except.Contains(D)) { Report(D, new NeighbourLink(C, -50)); }
        }

        ReportEveryone();
        Assert.Equal([B, A, "GATEWAY-1"], topology.Current.Routes[B].Path);
        Assert.Equal([D, C, B, A, "GATEWAY-1"], topology.Current.Routes[D].Path);
        Assert.Empty(topology.RouteChanges(10));

        // A goes quiet; everyone else keeps reporting. At 93 seconds A has been silent for three intervals and a bit. The loop is now
        // the only way out for B, C and D, and the dear link that the tree had left out is the one that carries them.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            ReportEveryone(A);
        }

        var healed = topology.Current;

        Assert.False(healed.Graph.Contains(A));
        Assert.Equal([B, C, "GATEWAY-1"], healed.Routes[B].Path);
        var bChange = Assert.Single(topology.RouteChanges(10), change => change.Device == B);
        Assert.Equal([B, A, "GATEWAY-1"], bChange.OldPath);
        Assert.Equal([B, C, "GATEWAY-1"], bChange.NewPath);
        Assert.Equal($"{A} excluded: Stale", bChange.Reason);
        Assert.Equal(clock.GetUtcNow(), bChange.At);
        Assert.Equal([D, C, "GATEWAY-1"], healed.Routes[D].Path);

        // C is an articulation point for D, and for B now. It goes quiet too, and they are cut off.
        foreach (var _ in Enumerable.Range(0, 4))
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            ReportEveryone(A, C);
        }

        var cut = topology.Current;

        Assert.Contains(D, cut.Reachability.CutOff);
        Assert.False(cut.Routes.ContainsKey(D));

        var dChange = topology.RouteChanges(10).First(change => change.Device == D);
        Assert.Null(dChange.NewPath);
        Assert.Equal($"{C} excluded: Stale; {D} cut off", dChange.Reason);
    }

    // ---- the endpoints ---------------------------------------------------------------------------------------

    /// <summary>
    /// A small mesh with a loop in it: GATEWAY-1 to A, A to B and D, B to C, and C straight to GATEWAY-1 over a weak link. The tree
    /// leaves the weak link out, so C is routed through B and A. No route change is recorded in the first 30 seconds unless told otherwise.
    /// </summary>
    private static async Task<(WebApplicationFactory<Program> Api, Func<string, double, NeighbourLink[], Task> Report)> MeshApiAsync()
    {
        var api = ApiTestHost.New().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Topology:SettleTime"] = "00:00:00" })));
        var http = api.CreateClient();

        foreach (var mac in new[] { A, B, C, D })
        {
            Assert.Equal(HttpStatusCode.Created, (await http.PostAsync("/api/sensors", Thermometer10To30(mac))).StatusCode);
        }

        async Task Report(string mac, double x, NeighbourLink[] neighbours) =>
            Assert.Equal(HttpStatusCode.NoContent, (await api.AsDevice(mac).PostAsJsonAsync($"/api/devices/{mac}/neighbours", new NeighbourReport(x, 5, neighbours))).StatusCode);

        await Report(A, 10, [new("GATEWAY-1", -50), new(B, -60), new(D, -70)]);
        await Report(B, 20, [new(A, -60), new(C, -55)]);
        await Report(C, 30, [new(B, -55), new("GATEWAY-1", -80)]);
        await Report(D, 15, [new(A, -70)]);

        return (api, Report);
    }

    [Fact]
    public async Task The_mst_endpoint_returns_the_backbone_with_a_weight_per_gateway_and_leaves_the_weak_link_out()
    {
        var (api, _) = await MeshApiAsync();
        using var _ = api;

        var mst = await api.AsViewer().GetFromJsonAsync<MstView>("/api/topology/mst");

        // Five links in the graph, four in the tree: the loop is broken at its most expensive link, C to GATEWAY-1.
        Assert.Equal(4, mst!.Edges.Count);
        Assert.DoesNotContain(mst.Edges, edge => edge.A == C && edge.B == "GATEWAY-1" || edge.A == "GATEWAY-1" && edge.B == C);
        Assert.Equal(mst.Edges.Sum(edge => edge.Cost), mst.TotalWeight, precision: 9);
        Assert.Equal(["GATEWAY-1", "GATEWAY-2", "GATEWAY-3"], mst.Trees.Select(tree => tree.Gateway));
        Assert.Equal(4, mst.Trees.Single(tree => tree.Gateway == "GATEWAY-1").Devices);
        Assert.Equal(mst.TotalWeight, mst.Trees.Single(tree => tree.Gateway == "GATEWAY-1").Weight, precision: 9);
        Assert.Equal(0, mst.Trees.Single(tree => tree.Gateway == "GATEWAY-2").Weight);
        Assert.Empty(mst.CutOff);
    }

    [Fact]
    public async Task The_route_endpoint_gives_the_path_with_names_and_the_shortest_cost_beside_the_tree_cost_and_an_unknown_device_is_a_404()
    {
        var (api, _) = await MeshApiAsync();
        using var _ = api;
        var http = api.AsViewer();

        var route = await http.GetFromJsonAsync<RouteView>($"/api/topology/route/{C}");

        Assert.False(route!.CutOff);
        Assert.Equal([C, B, A, "GATEWAY-1"], route.Hops.Select(hop => hop.Id));
        Assert.All(route.Hops, hop => Assert.False(string.IsNullOrWhiteSpace(hop.Name)));
        Assert.Equal("GATEWAY-1", route.Gateway);
        Assert.Equal(3, route.HopCount);

        // C could reach the gateway directly, over the weak link, for less than the tree's three hops cost: the tree is the backbone,
        // not each device's cheapest way.
        Assert.True(route.ShortestCost < route.MstCost, $"shortest {route.ShortestCost} against tree {route.MstCost}");
        Assert.Equal(LinkCost.Cost(-80, 0.5, 4), route.ShortestCost!.Value, precision: 9);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/topology/route/AA:BB:CC:00:99:99")).StatusCode);
    }

    [Fact]
    public async Task The_route_changes_endpoint_lists_a_changed_route_newest_first()
    {
        var (api, report) = await MeshApiAsync();
        using var _ = api;
        var http = api.AsViewer();

        // Build the first snapshot, then take away the links that route C through B: B stops hearing C, and C stops hearing B.
        await http.GetAsync("/api/topology/mst");
        await report(B, 20, [new(A, -60)]);
        await report(C, 30, [new("GATEWAY-1", -80)]);

        var changes = await http.GetFromJsonAsync<List<RouteChangeView>>("/api/topology/route-changes?limit=10");

        var change = Assert.Single(changes!);
        Assert.Equal(C, change.Device);
        Assert.Equal([C, B, A, "GATEWAY-1"], change.OldPath);
        Assert.Equal([C, "GATEWAY-1"], change.NewPath);
        Assert.False(string.IsNullOrWhiteSpace(change.Reason));
    }

    [Fact]
    public async Task Every_route_endpoint_refuses_a_request_with_no_credentials()
    {
        using var api = ApiTestHost.New();
        var http = api.Anonymous();

        foreach (var url in new[] { "/api/topology/mst", $"/api/topology/route/{A}", "/api/topology/route-changes" })
        {
            var status = (await http.GetAsync(url)).StatusCode;

            Assert.True(status == HttpStatusCode.Unauthorized, $"{url}: expected 401, got {(int)status}.");
        }
    }
}
