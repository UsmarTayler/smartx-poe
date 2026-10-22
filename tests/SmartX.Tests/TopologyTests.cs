using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartX.Api.Topology;
using SmartX.Api.Trust;
using SmartX.DeviceSimulator;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// The mesh graph: its weights, its link rule, the two traversals checked against brute force, and the endpoints that feed and
/// read it.
/// </summary>
/// <remarks>
/// Written to a budget of twenty tests. The two traversals are each proved by one seeded comparison against an obviously correct
/// slow answer over two hundred random graphs, which covers more shapes than twenty hand-drawn cases would. Nothing here reads
/// the clock: ageing uses a <see cref="FakeTimeProvider"/>.
/// </remarks>
public class TopologyTests
{
    private const string A = "AA:BB:CC:00:10:01";
    private const string B = "AA:BB:CC:00:10:02";
    private const string C = "AA:BB:CC:00:10:03";
    private const string D = "AA:BB:CC:00:10:04";

    // ---- the traversals against brute force ------------------------------------------------------------------

    /// <summary>A random graph of up to fourteen vertices, a few of them gateways, with random links.</summary>
    private static MeshGraph RandomGraph(Random random)
    {
        var count = random.Next(1, 15);
        var vertices = Enumerable.Range(0, count)
            .Select(i => new MeshVertex($"v{i:D2}", $"v{i}", null, 0, 0, IsGateway: random.Next(5) == 0, TrustBand.Nominal, 1))
            .ToList();
        var density = random.NextDouble() * 0.5;
        var edges = new List<MeshEdge>();

        for (var i = 0; i < count; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                if (random.NextDouble() < density)
                {
                    edges.Add(new MeshEdge(vertices[i].Id, vertices[j].Id, -60, 5));
                }
            }
        }

        return new MeshGraph(vertices, edges);
    }

    [Fact]
    public void Nearest_gateway_and_hops_match_a_repeated_relaxation_on_two_hundred_random_graphs()
    {
        var random = new Random(19);

        for (var round = 0; round < 200; round++)
        {
            var graph = RandomGraph(random);
            var ids = graph.Vertices.Select(vertex => vertex.Id).ToList();

            // The slow answer: for each gateway on its own, relax every edge until nothing improves. Hops from the nearest gateway
            // are the smallest of those.
            var fromGateway = graph.Vertices.Where(vertex => vertex.IsGateway).ToDictionary(gateway => gateway.Id, gateway =>
            {
                var distance = ids.ToDictionary(id => id, _ => int.MaxValue);
                distance[gateway.Id] = 0;

                for (var pass = 0; pass < ids.Count; pass++)
                {
                    foreach (var edge in graph.Edges)
                    {
                        if (distance[edge.A] != int.MaxValue) { distance[edge.B] = Math.Min(distance[edge.B], distance[edge.A] + 1); }
                        if (distance[edge.B] != int.MaxValue) { distance[edge.A] = Math.Min(distance[edge.A], distance[edge.B] + 1); }
                    }
                }

                return distance;
            });

            var result = MeshTraversal.NearestGateways(graph);

            foreach (var device in graph.Vertices.Where(vertex => !vertex.IsGateway))
            {
                var best = fromGateway.Count == 0 ? int.MaxValue : fromGateway.Values.Min(distance => distance[device.Id]);

                if (best == int.MaxValue)
                {
                    Assert.Contains(device.Id, result.CutOff);
                    Assert.False(result.Routes.ContainsKey(device.Id));
                }
                else
                {
                    var route = result.Routes[device.Id];

                    Assert.Equal(best, route.Hops);
                    Assert.Equal(route.Hops, fromGateway[route.Gateway][device.Id]);   // the gateway it names really is that near
                    Assert.DoesNotContain(device.Id, result.CutOff);
                }
            }
        }
    }

    private static int Components(MeshGraph graph, string? without)
    {
        var seen = new HashSet<string>();
        var count = 0;

        foreach (var vertex in graph.Vertices.Where(vertex => vertex.Id != without))
        {
            if (!seen.Add(vertex.Id))
            {
                continue;
            }

            count++;
            var stack = new Stack<string>([vertex.Id]);

            while (stack.Count > 0)
            {
                foreach (var link in graph.NeighboursOf(stack.Pop()).Where(link => link.To != without && seen.Add(link.To)))
                {
                    stack.Push(link.To);
                }
            }
        }

        return count;
    }

    [Fact]
    public void Articulation_points_match_removing_each_vertex_and_recounting_on_two_hundred_random_graphs()
    {
        var random = new Random(190);

        for (var round = 0; round < 200; round++)
        {
            var graph = RandomGraph(random);
            var whole = Components(graph, without: null);

            // The slow answer: a vertex is a cut vertex if taking it out leaves more pieces than there were.
            var expected = graph.Vertices.Where(vertex => Components(graph, vertex.Id) > whole).Select(vertex => vertex.Id).Order(StringComparer.Ordinal);

            Assert.Equal(expected, MeshTraversal.FindArticulationVertices(graph));
        }
    }

    // ---- weights and the link rule ---------------------------------------------------------------------------

    [Theory]
    [InlineData(-30, 1.0, 4.0, 1.0)]    // the strongest link between fully trusted devices costs the minimum
    [InlineData(-90, 1.0, 4.0, 10.0)]   // the weakest costs ten times that
    [InlineData(-60, 0.5, 4.0, 16.5)]   // mid-scale radio cost 5.5, tripled by a trust of one half: 5.5 x (1 + 4 x 0.5)
    [InlineData(-90, 0.0, 4.0, 50.0)]   // a link whose weaker end has no trust costs five times its radio cost
    public void A_links_cost_is_its_radio_cost_made_worse_by_the_lack_of_trust_in_its_weaker_end(int rssi, double trust, double penalty, double expected) =>
        Assert.Equal(expected, LinkCost.Cost(rssi, trust, penalty), precision: 9);

    [Fact]
    public void A_link_exists_if_either_end_reports_it_and_uses_the_weaker_reading()
    {
        MeshVertex Device(string id, double trust) => new(id, id, "z", 0, 0, false, TrustBand.Nominal, trust);

        var graph = MeshGraph.FromClaims(
            [Device("a", 1), Device("b", 0.5), Device("c", 1), Device("d", 1)],
            [
                new LinkClaim("a", "b", -50),     // both ends report this one, at different strengths
                new LinkClaim("b", "a", -70),
                new LinkClaim("c", "d", -40),     // only one end reports this one
                new LinkClaim("a", "zzz", -40),   // an unknown end is dropped
            ],
            penaltyFactor: 4);

        Assert.Equal(2, graph.Edges.Count);

        var both = graph.Edges.Single(edge => edge.A == "a" && edge.B == "b");

        Assert.Equal(-70, both.Rssi);                                           // the weaker of -50 and -70
        Assert.Equal(LinkCost.Cost(-70, 0.5, 4), both.Cost, precision: 9);      // and the lower trust of the two ends
        Assert.Contains(graph.Edges, edge => edge is { A: "c", B: "d", Rssi: -40 });
    }

    [Fact]
    public void A_stale_device_and_a_device_in_fault_are_left_out_of_the_graph()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        var trust = new TrustEngine(Options.Create(new TrustOptions()), clock, NullLogger<TrustEngine>.Instance);
        var topology = new TopologyService(trust, clock, Options.Create(new TopologyOptions()));

        void Report(string mac, params NeighbourLink[] neighbours)
        {
            trust.Track(mac);
            topology.Submit(mac, mac, "z", new NeighbourReport(0, 0, neighbours));
        }

        Report(A, new NeighbourLink("GATEWAY-1", -50), new NeighbourLink(B, -60), new NeighbourLink(C, -60));
        Report(B, new NeighbourLink(A, -60));
        Report(C, new NeighbourLink(A, -60));

        Assert.Equal(3, topology.Current.Graph.NeighboursOf(A).Count);   // its gateway, B and C

        // C is driven into the Fault band by bad packets.
        for (var failures = 0; failures < 30 && trust.Snapshot().Single(node => node.MacAddress == C).Band != TrustBand.Fault; failures++)
        {
            trust.Record(C, TrustOutcome.SchemaFailure);
        }

        // Thirty-one seconds later A and C report again but B does not; then another sixty. B has now been silent for 91 seconds,
        // three intervals and a second.
        clock.Advance(TimeSpan.FromSeconds(31));
        Report(A, new NeighbourLink("GATEWAY-1", -50), new NeighbourLink(B, -60), new NeighbourLink(C, -60));
        Report(C, new NeighbourLink(A, -60));
        clock.Advance(TimeSpan.FromSeconds(60));

        var snapshot = topology.Current;

        Assert.Equal([A, "GATEWAY-1", "GATEWAY-2", "GATEWAY-3"], snapshot.Graph.Vertices.Select(vertex => vertex.Id).Order(StringComparer.Ordinal));
        Assert.False(snapshot.Graph.Contains(B));
        Assert.False(snapshot.Graph.Contains(C));
        Assert.Contains(snapshot.Excluded, excluded => excluded is { Id: B, Reason: "Stale" });
        Assert.Contains(snapshot.Excluded, excluded => excluded is { Id: C, Reason: "Fault" });
        Assert.Single(snapshot.Graph.Edges);   // only A to its gateway is left
    }

    // ---- the layout ------------------------------------------------------------------------------------------

    [Fact]
    public void The_seeded_demo_layout_is_one_connected_mesh_with_three_gateways_and_a_weak_point_and_is_the_same_every_time()
    {
        string Fingerprint(MeshPlan plan) =>
            string.Join(";", plan.Devices.Order(StringComparer.Ordinal).Select(mac =>
                $"{mac}@{plan.PositionOf(mac)}:{string.Join(",", plan.TableOf(mac).Select(link => $"{link.To}/{link.Rssi}"))}"));

        var plan = MeshLayout.Create(DemoDevices.All);

        Assert.Equal(Fingerprint(plan), Fingerprint(MeshLayout.Create(DemoDevices.All)));

        var vertices = MeshGateways.All
            .Select(gateway => new MeshVertex(gateway.Id, gateway.Name, null, gateway.X, gateway.Y, true, null, 1))
            .Concat(DemoDevices.All.Select(device => new MeshVertex(device.MacAddress, device.Registration.Room, device.Registration.ZoneId, 0, 0, false, TrustBand.Nominal, 1)))
            .ToList();
        var graph = MeshGraph.FromClaims(
            vertices,
            DemoDevices.All.SelectMany(device => plan.TableOf(device.MacAddress).Select(link => new LinkClaim(device.MacAddress, link.To, link.Rssi))),
            LinkCost.DefaultPenaltyFactor);

        Assert.Equal(3, graph.Vertices.Count(vertex => vertex.IsGateway));
        Assert.Equal(58, graph.VertexCount);
        Assert.Equal(1, Components(graph, without: null));
        Assert.Empty(MeshTraversal.NearestGateways(graph).CutOff);
        Assert.NotEmpty(MeshTraversal.FindArticulationVertices(graph));
    }

    [Fact]
    public async Task A_device_in_the_disconnect_scenario_drops_out_of_other_devices_tables_and_stops_reporting()
    {
        using var api = new AuthenticatedFactory().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Zones:Hierarchy"] = "Demo" })));
        var http = api.CreateClient();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        var fleet = new DeviceFleet(
            new GatewayClient(http, NullLogger<GatewayClient>.Instance, new DeviceKeyring(TestAuth.FleetSecret)),
            Options.Create(new SimulatorOptions()),
            clock,
            new NoopHostLifetime(),
            NullLogger<DeviceFleet>.Instance);

        await fleet.RegisterFleetForTestAsync();

        var silent = fleet.DevicesForTest.First(device => device.Scenario == SimulationScenario.Disconnect);
        var listener = fleet.DevicesForTest.First(device => device.Scenario != SimulationScenario.Disconnect
            && MeshLayout.Create(DemoDevices.All).TableOf(device.MacAddress).Any(link => link.To == silent.MacAddress));

        // The listener would hear the silent device if it were up, and does not while it is off the air.
        Assert.Contains(silent.MacAddress, MeshLayout.Create(DemoDevices.All).TableOf(listener.MacAddress).Select(link => link.To));
        Assert.DoesNotContain(silent.MacAddress, fleet.BuildNeighbourReport(listener).Neighbours.Select(link => link.Id));

        // A live device reports and schedules its next report an interval away. The silent one sends nothing and schedules nothing.
        await fleet.TickDeviceForTestAsync(listener, clock.GetUtcNow(), CancellationToken.None);
        await fleet.TickDeviceForTestAsync(silent, clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(30), listener.NextNeighbourReport);
        Assert.Equal(default, silent.NextNeighbourReport);
    }

    private sealed class NoopHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    // ---- the endpoints ---------------------------------------------------------------------------------------

    /// <summary>
    /// An API holding a small mesh: GATEWAY-1 to A, A to B and D, B to C. A is the one device whose loss splits it.
    /// </summary>
    private static async Task<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>> MeshApiAsync()
    {
        var api = ApiTestHost.New();
        var http = api.CreateClient();

        foreach (var mac in new[] { A, B, C, D })
        {
            Assert.Equal(HttpStatusCode.Created, (await http.PostAsync("/api/sensors", Thermometer10To30(mac))).StatusCode);
        }

        async Task ReportAsync(string mac, double x, params NeighbourLink[] neighbours) =>
            Assert.Equal(HttpStatusCode.NoContent, (await api.AsDevice(mac).PostAsJsonAsync($"/api/devices/{mac}/neighbours", new NeighbourReport(x, 5, neighbours))).StatusCode);

        await ReportAsync(A, 10, new NeighbourLink("GATEWAY-1", -50), new NeighbourLink(B, -60), new NeighbourLink(D, -70));
        await ReportAsync(B, 20, new NeighbourLink(A, -60), new NeighbourLink(C, -55));
        await ReportAsync(C, 30, new NeighbourLink(B, -55));
        await ReportAsync(D, 15, new NeighbourLink(A, -70));

        return api;
    }

    [Fact]
    public async Task A_device_can_report_its_own_neighbours_and_not_another_devices()
    {
        using var api = ApiTestHost.New();
        await api.CreateClient().PostAsync("/api/sensors", Thermometer10To30(A));
        await api.CreateClient().PostAsync("/api/sensors", Thermometer10To30(B));
        var report = new NeighbourReport(1, 2, [new NeighbourLink("GATEWAY-1", -50)]);

        var own = await api.AsDevice(A).PostAsJsonAsync($"/api/devices/{A}/neighbours", report);
        var other = await api.AsDevice(A).PostAsJsonAsync($"/api/devices/{B}/neighbours", report);

        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task The_topology_endpoint_returns_every_node_and_link()
    {
        using var api = await MeshApiAsync();

        var topology = await api.AsViewer().GetFromJsonAsync<TopologyView>("/api/topology");

        Assert.Equal(7, topology!.Nodes.Count);                                   // four devices and three gateways
        Assert.Equal(3, topology.Nodes.Count(node => node.IsGateway));
        Assert.Equal(4, topology.Edges.Count);
        Assert.Equal(-60, topology.Edges.Single(edge => edge is { A: A, B: B }).Rssi);
        Assert.Empty(topology.Excluded);
    }

    [Fact]
    public async Task The_reachability_endpoint_gives_each_device_its_nearest_gateway_and_hops()
    {
        using var api = await MeshApiAsync();

        var reachability = await api.AsViewer().GetFromJsonAsync<ReachabilityView>("/api/topology/reachability");

        Assert.Equal([(A, 1), (B, 2), (C, 3), (D, 2)], reachability!.Routes.Select(route => (route.Id, route.Hops)));
        Assert.All(reachability.Routes, route => Assert.Equal("GATEWAY-1", route.Gateway));
        Assert.Empty(reachability.CutOff);
    }

    [Fact]
    public async Task The_articulation_endpoint_names_each_weak_point_and_who_it_would_cut_off()
    {
        using var api = await MeshApiAsync();

        var view = await api.AsViewer().GetFromJsonAsync<ArticulationView>("/api/topology/articulation-points");

        Assert.Equal([A, B], view!.Points.Select(point => point.Id));
        Assert.Equal([B, C, D], view.Points.Single(point => point.Id == A).CutOff);
        Assert.Equal([C], view.Points.Single(point => point.Id == B).CutOff);
    }

    [Fact]
    public async Task Every_topology_endpoint_refuses_a_request_with_no_credentials()
    {
        using var api = ApiTestHost.New();
        var http = api.Anonymous();

        foreach (var url in new[] { "/api/topology", "/api/topology/reachability", "/api/topology/articulation-points" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(url)).StatusCode);
        }

        var post = await http.PostAsJsonAsync($"/api/devices/{A}/neighbours", new NeighbourReport(0, 0, []));

        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }
}
