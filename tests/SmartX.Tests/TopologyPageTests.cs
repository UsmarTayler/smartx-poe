using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartX.Api.Topology;
using SmartX.Api.Trust;
using SmartX.Client.Layout;
using SmartX.Client.Pages;
using SmartX.Client.Realtime;
using SmartX.Client.Services;
using SmartX.Client.Components.Common;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>An API for the topology page that answers a small fixed mesh and records how often it was asked for it.</summary>
public sealed class FakeTopologyApi : ITopologyApi
{
    public const string A = "AA:BB:CC:00:30:01";
    public const string B = "AA:BB:CC:00:30:02";
    public const string C = "AA:BB:CC:00:30:03";
    public const string D = "AA:BB:CC:00:30:04";

    private int _topologyCalls;

    public int TopologyCalls => Volatile.Read(ref _topologyCalls);

    private static TopologyNode Node(string id, string name, double x, double y, TrustBand band) => new(id, name, "zone-1", x, y, band, 0.9, false);

    /// <summary>
    /// GATEWAY-1 to A, and A to B and C; B and C also hear each other, but that link is not in the tree. A is the weak point: B and C
    /// depend on it. D has been taken offline.
    /// </summary>
    public TopologyView Topology { get; set; } = new(
        ClientRealtimeKit.Start,
        [
            new TopologyNode("GATEWAY-1", "Gateway 1", null, 0, 0, null, 1, true),
            Node(A, "Room A", 10, 0, TrustBand.Nominal),
            Node(B, "Room B", 20, 0, TrustBand.Degrading),
            Node(C, "Room C", 10, 10, TrustBand.Fault),
        ],
        [new("GATEWAY-1", A, -50, 2), new(A, B, -60, 3), new(A, C, -55, 2.5), new(B, C, -70, 6)],
        [new ExcludedNode(D, "Taken offline by operator", "Room D", 30, 0)]);

    public MstView Mst { get; set; } = new(
        ClientRealtimeKit.Start,
        [new("GATEWAY-1", A, -50, 2), new(A, B, -60, 3), new(A, C, -55, 2.5)],
        [new MstTreeView("GATEWAY-1", "Gateway 1", 7.5, 3)],
        7.5,
        []);

    public ReachabilityView Reachability { get; set; } = new(
        ClientRealtimeKit.Start,
        [new DeviceRoute(A, "GATEWAY-1", 1), new DeviceRoute(B, "GATEWAY-1", 2), new DeviceRoute(C, "GATEWAY-1", 2)],
        []);

    public ArticulationView WeakPoints { get; set; } = new(ClientRealtimeKit.Start, [new ArticulationPointView(A, "Room A", false, [B, C])]);

    public Func<string, RoutePathView> Route { get; set; } = mac => new RoutePathView(
        ClientRealtimeKit.Start,
        mac,
        false,
        null,
        "GATEWAY-1",
        2,
        8,
        6.5,
        [new RouteHop(mac, "Room B"), new RouteHop(A, "Room A"), new RouteHop("GATEWAY-1", "Gateway 1")]);

    public Task<ApiResult<TopologyView>> GetTopologyAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _topologyCalls);

        return Task.FromResult(ApiResult<TopologyView>.Success(Topology));
    }

    public Task<ApiResult<MstView>> GetMstAsync(CancellationToken cancellationToken = default) => Task.FromResult(ApiResult<MstView>.Success(Mst));

    public Task<ApiResult<ReachabilityView>> GetReachabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<ReachabilityView>.Success(Reachability));

    public Task<ApiResult<ArticulationView>> GetArticulationPointsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<ArticulationView>.Success(WeakPoints));

    public Task<ApiResult<RoutePathView>> GetRouteAsync(string macAddress, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<RoutePathView>.Success(Route(macAddress)));

    public Task<ApiResult<HistoryRange>> GetHistoryRangeAsync(
        string macAddress, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<HistoryRange>.Success(new HistoryRange(macAddress, from, to, null, [], false, 3, null)));
}

/// <summary>
/// The topology page, drawn from a fake mesh: what it shows, what selecting a device does, what a Viewer may not do, and that it
/// asks for the mesh again when the hub says there is a new revision.
/// </summary>
/// <remarks>
/// Written to a budget of ten tests across this file and the mesh's API tests. The page, its feed and the shared state are the shipped
/// classes; only the API and the hub are replaced.
/// </remarks>
public class TopologyPageTests : StreamComponentTests
{
    private readonly FakeTopologyApi _topology = new();

    public TopologyPageTests()
    {
        TestServices.AddSingleton<ITopologyApi>(_topology);
        TestServices.AddSingleton<ITopologyFeed>(Rig.Topology);
    }

    private async Task<IRenderedComponent<TopologyPage>> OpenAsync()
    {
        RenderPopoverHost();

        var page = Render<TopologyPage>();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("g.smartx-mesh__node")));
        await SettledAsync(page);

        return page;
    }

    private static AngleSharp.Dom.IElement NodeNamed(IRenderedComponent<TopologyPage> page, string name) =>
        page.FindAll("g.smartx-mesh__node").First(node => node.GetAttribute("aria-label")!.StartsWith(name, StringComparison.Ordinal));

    [Fact]
    public async Task The_page_draws_every_node_and_the_spanning_tree_links_from_the_snapshot()
    {
        var page = await OpenAsync();

        // Three devices and one left out are circles; the gateway is a square; the left-out device is drawn too.
        Assert.Equal(4, page.FindAll("g.smartx-mesh__node--device").Count);
        Assert.Single(page.FindAll("g.smartx-mesh__node--gateway"));
        Assert.Single(page.FindAll("g.smartx-mesh__node--gateway rect"));
        Assert.Equal(4, page.FindAll("g.smartx-mesh__node--device circle.smartx-mesh__shape").Count);

        // Three links are in the tree and drawn solid; the fourth is faint.
        Assert.Equal(3, page.FindAll("svg.smartx-mesh > line.smartx-mesh__link--tree").Count);
        Assert.Single(page.FindAll("svg.smartx-mesh > line.smartx-mesh__link:not(.smartx-mesh__link--tree)"));

        // The weak point says so on the map, and the left-out device is greyed.
        Assert.Contains("weak point", page.Find("svg.smartx-mesh").InnerHtml, StringComparison.Ordinal);
        Assert.Single(page.FindAll("g.smartx-mesh__node--weak"));
        Assert.Equal(1, page.FindAll("g.smartx-mesh__node--out").Count(node => node.GetAttribute("aria-label")!.Contains("Taken offline", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Selecting_a_device_highlights_its_route_hop_by_hop_and_shows_the_tree_cost_beside_the_shortest()
    {
        var page = await OpenAsync();

        NodeNamed(page, "Room B").Click();

        // Three stops, two links, drawn over the map in the accent.
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("svg.smartx-mesh > line.smartx-mesh__route").Count));
        Assert.Equal(["Room B", "Room A", "Gateway 1"], page.FindAll("ol.smartx-route li").Select(item => item.QuerySelector("span")!.TextContent));

        var costs = page.Find("dl.smartx-route-costs").TextContent;

        Assert.Contains("8.00", costs, StringComparison.Ordinal);
        Assert.Contains("6.50", costs, StringComparison.Ordinal);
        Assert.Contains("The spanning tree keeps the whole network cheapest, so this device's own route isn't the cheapest possible.", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_viewer_sees_take_offline_disabled_with_the_reason()
    {
        SignInAs(UserRoles.Viewer);
        var page = await OpenAsync();

        NodeNamed(page, "Room B").Click();

        page.WaitForAssertion(() =>
        {
            var button = page.FindAll("button").First(candidate => candidate.TextContent.Contains("Take offline", StringComparison.Ordinal));

            Assert.True(button.HasAttribute("disabled"));
            Assert.Contains("Operators only", page.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_table_fallback_lists_every_device_with_its_zone_band_gateway_hops_and_weak_point()
    {
        var page = await OpenAsync();

        var rows = page.FindAll("table.smartx-topo-table tbody tr");

        // The gateway, three devices in the graph, and the one left out.
        Assert.Equal(5, rows.Count);

        var roomA = rows.First(row => row.TextContent.Contains("Room A", StringComparison.Ordinal)).TextContent;

        Assert.Contains("Nominal", roomA, StringComparison.Ordinal);
        Assert.Contains("GATEWAY-1", roomA, StringComparison.Ordinal);
        Assert.Contains("Yes, 2 depend on it", roomA, StringComparison.Ordinal);
        Assert.Contains("Left out: Taken offline by operator", rows.First(row => row.TextContent.Contains("Room D", StringComparison.Ordinal)).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_topology_push_with_a_new_revision_fetches_the_mesh_again_and_a_repeat_does_not()
    {
        var page = await OpenAsync();
        await ListeningAsync(Rig, RealtimeMethods.TopologyUpdated);

        var before = _topology.TopologyCalls;

        // The snapshot said revision 1. A message at revision 2 is a new mesh; a later message at revision 2 is not.
        Rig.Hub.Deliver(RealtimeMethods.TopologyUpdated, new TopologyUpdate(1, 2, ClientRealtimeKit.Start, []));
        await ClientRig.UntilAsync(() => _topology.TopologyCalls > before, "the mesh to be fetched again");

        var after = _topology.TopologyCalls;
        Rig.Hub.Deliver(RealtimeMethods.TopologyUpdated, new TopologyUpdate(2, 2, ClientRealtimeKit.Start, []));
        await Task.Delay(150);

        Assert.Equal(after, _topology.TopologyCalls);
        Assert.NotNull(page.Find("svg.smartx-mesh"));
    }

    [Fact]
    public async Task The_feed_keeps_each_route_change_once_newest_first()
    {
        await Rig.Topology.StartAsync();
        await ClientRig.UntilAsync(() => Rig.Topology.Current.IsLoaded, "the topology snapshot");
        await ListeningAsync(Rig, RealtimeMethods.TopologyUpdated);

        RouteChangeView Change(long id) => new(id, ClientRealtimeKit.Start, FakeTopologyApi.B, "Room B", [FakeTopologyApi.B, "GATEWAY-1"], null, $"change {id}");

        // The second message repeats change 2, as a push just after a snapshot can.
        Rig.Hub.Deliver(RealtimeMethods.TopologyUpdated, new TopologyUpdate(1, 2, ClientRealtimeKit.Start, [Change(2), Change(1)]));
        Rig.Hub.Deliver(RealtimeMethods.TopologyUpdated, new TopologyUpdate(2, 3, ClientRealtimeKit.Start, [Change(3), Change(2)]));

        await ClientRig.UntilAsync(() => Rig.Topology.Current.Revision == 3, "the second message");

        Assert.Equal([3L, 2, 1], Rig.Topology.Current.Changes.Select(change => change.Id));
    }

    [Fact]
    public void The_landing_page_and_the_menu_open_all_three_pillars_alike()
    {
        // The pillars are links with the same look; none is disabled and none carries a "Part 3" chip.
        RenderPopoverHost();
        var cards = Render<PillarCards>();
        var menu = Render<NavMenu>();

        var hrefs = cards.FindAll("a").Select(link => link.GetAttribute("href")).ToList();

        Assert.Contains("topology", hrefs);
        Assert.Contains("command-stream", hrefs);
        Assert.DoesNotContain("Part 3", cards.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-disabled", cards.Markup, StringComparison.Ordinal);

        Assert.Contains(menu.FindAll("a"), link => link.GetAttribute("href") == "topology");
        Assert.DoesNotContain("Part 3", menu.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-disabled", menu.Markup, StringComparison.Ordinal);
    }
}

/// <summary>Taking a device offline, at once, and bringing it back, against the topology service and over HTTP.</summary>
public class TakeOfflineTests
{
    private const string A = "AA:BB:CC:00:31:01";
    private const string B = "AA:BB:CC:00:31:02";

    [Fact]
    public void An_operator_taking_a_device_offline_leaves_it_out_at_once_and_it_returns_with_its_next_report()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        var trust = new TrustEngine(Options.Create(new TrustOptions()), clock, NullLogger<TrustEngine>.Instance);
        var topology = new TopologyService(trust, clock, Options.Create(new TopologyOptions { SettleTime = TimeSpan.Zero }));

        void Report(string mac, params NeighbourLink[] neighbours) => topology.Submit(mac, mac, "z", new NeighbourReport(0, 0, neighbours));

        void ReportBoth()
        {
            Report(A, new NeighbourLink("GATEWAY-1", -40));
            Report(B, new NeighbourLink(A, -50));
        }

        ReportBoth();
        Assert.Equal([B, A, "GATEWAY-1"], topology.Current.Routes[B].Path);
        var revision = topology.Current.Revision;

        // No time passes: the device is out as soon as the operator says so, and the devices behind it lose their route.
        topology.TakeOffline(A);
        var gone = topology.Current;

        Assert.False(gone.Graph.Contains(A));
        Assert.Contains(gone.Excluded, excluded => excluded is { Id: A, Reason: "Taken offline by operator" });
        Assert.Contains(B, gone.Reachability.CutOff);
        Assert.NotEqual(revision, gone.Revision);

        var change = Assert.Single(topology.RouteChanges(10), recorded => recorded.Device == B);
        Assert.Equal($"{A} excluded: Taken offline by operator; {B} cut off", change.Reason);

        // Brought back, it stays out until it reports, so it cannot return on a report it sent before it went.
        topology.BringBack(A);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(topology.Current.Graph.Contains(A));

        ReportBoth();
        var back = topology.Current;

        Assert.True(back.Graph.Contains(A));
        Assert.Equal([B, A, "GATEWAY-1"], back.Routes[B].Path);
    }

    [Fact]
    public async Task Setting_the_disconnect_scenario_over_http_takes_the_node_out_of_the_topology_straight_away()
    {
        using var api = ApiTestHost.New();
        var http = api.CreateClient();

        foreach (var mac in new[] { A, B })
        {
            Assert.Equal(HttpStatusCode.Created, (await http.PostAsync("/api/sensors", Thermometer10To30(mac))).StatusCode);
        }

        async Task ReportAsync(string mac, params NeighbourLink[] neighbours) =>
            Assert.Equal(HttpStatusCode.NoContent, (await api.AsDevice(mac).PostAsJsonAsync($"/api/devices/{mac}/neighbours", new NeighbourReport(1, 1, neighbours))).StatusCode);

        await ReportAsync(A, new NeighbourLink("GATEWAY-1", -40));
        await ReportAsync(B, new NeighbourLink(A, -50));

        var viewer = api.AsViewer();
        var before = (await viewer.GetFromJsonAsync<TopologyView>("/api/topology"))!;

        Assert.Contains(before.Nodes, node => node.Id == A);

        var set = await http.PostAsJsonAsync($"/api/simulation/{A}/scenario", new ScenarioChangeRequest(SimulationScenario.Disconnect));
        Assert.Equal(HttpStatusCode.Accepted, set.StatusCode);

        var after = (await viewer.GetFromJsonAsync<TopologyView>("/api/topology"))!;

        Assert.DoesNotContain(after.Nodes, node => node.Id == A);
        var left = Assert.Single(after.Excluded, excluded => excluded.Id == A);
        Assert.Equal("Taken offline by operator", left.Reason);
        Assert.Equal((1d, 1d), (left.X, left.Y));

        // And back: the scenario returns to Nominal and the device rejoins with its next report.
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsJsonAsync($"/api/simulation/{A}/scenario", new ScenarioChangeRequest(SimulationScenario.Nominal))).StatusCode);
        Assert.DoesNotContain((await viewer.GetFromJsonAsync<TopologyView>("/api/topology"))!.Nodes, node => node.Id == A);

        await ReportAsync(A, new NeighbourLink("GATEWAY-1", -40));

        Assert.Contains((await viewer.GetFromJsonAsync<TopologyView>("/api/topology"))!.Nodes, node => node.Id == A);
    }
}
