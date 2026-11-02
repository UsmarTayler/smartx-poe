using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartX.Api.Ingestion;
using SmartX.Client.Components.CommandStream;
using SmartX.Client.Pages;
using SmartX.Client.Realtime;
using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Tests;

// What the Part 2 feedback asked to be visible: the order the lanes are served in, the counts a suggestion has learned, the alert
// sets and the index lookup, and views that keep what they showed. Written to a budget of seven tests; the two figures added to
// existing endpoint and tracker tests are assertions in them and not new tests.

/// <summary>The queue says the order it will serve readings in, and the worker then serves them in that order.</summary>
public class QueueOrderTests
{
    [Fact]
    public async Task The_order_the_stats_name_is_the_order_the_worker_takes()
    {
        // Seeded random cases in one test: a mix of lanes, a different yield limit each round, and a few readings already taken so
        // the worker is part-way through a run of critical ones.
        var random = new Random(20261102);

        for (var round = 0; round < 60; round++)
        {
            var yieldAfter = random.Next(1, 5);
            var queue = new TelemetryIngestionQueue(
                Options.Create(new IngestionOptions { MaxConsecutiveCritical = yieldAfter }),
                TimeProvider.System,
                new SmartX.Api.History.ArrivalSequence());

            var total = random.Next(0, 24);

            for (var i = 0; i < total; i++)
            {
                var packet = new NumericPacket<int>($"AA:BB:CC:DD:EE:{i:X2}", i, DateTimeOffset.UnixEpoch, SensorCategory.PowerConsumption, "W");
                var alert = random.Next(3) == 0 ? new CriticalAlert(random.Next(1, 4), "power spike") : (CriticalAlert?)null;

                Assert.True(queue.TryEnqueue(packet, alert, out _));
            }

            var alreadyTaken = random.Next(0, Math.Min(total, 4) + 1);

            for (var i = 0; i < alreadyTaken; i++)
            {
                await queue.DequeueAsync(CancellationToken.None);
            }

            var next = queue.Stats().NextUp!;

            Assert.True(next.Count == Math.Min(TelemetryIngestionQueue.NextUpShown, total - alreadyTaken), $"Round {round}: {next.Count} named.");

            foreach (var item in next)
            {
                var taken = await queue.DequeueAsync(CancellationToken.None);

                Assert.True(
                    taken.Packet.NodeId == item.MacAddress && taken.Lane == item.Lane,
                    $"Round {round}, position {item.Position}: named {item.Lane} {item.MacAddress}, took {taken.Lane} {taken.Packet.NodeId}.");
            }

            Assert.Equal(Enumerable.Range(1, next.Count), next.Select(item => item.Position));
        }

        // A critical reading, once taken, is remembered with what it overtook: the snapshot is made long after the lane has emptied.
        var overtaking = new TelemetryIngestionQueue(Options.Create(new IngestionOptions()), TimeProvider.System, new SmartX.Api.History.ArrivalSequence());

        for (var i = 0; i < 3; i++)
        {
            overtaking.TryEnqueue(new NumericPacket<int>("AA:BB:CC:00:00:0" + i, i, DateTimeOffset.UnixEpoch, SensorCategory.PowerConsumption, "W"), null, out _);
        }

        overtaking.TryEnqueue(new NumericPacket<int>("AA:BB:CC:00:00:99", 99, DateTimeOffset.UnixEpoch, SensorCategory.PowerConsumption, "W"), new CriticalAlert(1, "power spike"), out _);
        await overtaking.DequeueAsync(CancellationToken.None);

        var overtook = Assert.Single(overtaking.Stats().RecentCritical!);

        Assert.Equal("AA:BB:CC:00:00:99", overtook.MacAddress);
        Assert.Equal(3, overtook.StandardAhead);
    }
}

/// <summary>The suggestion card sets the learned counts out as figures, in the full card and in the compact one.</summary>
public class SuggestionFigureTests : StreamComponentTests
{
    private static List<(string Label, string Figure)> Figures(IRenderedComponent<SuggestionCard> cut) =>
        [.. cut.FindAll(".smartx-suggestion__learned > div").Select(item => (item.QuerySelector("dt")!.TextContent, item.QuerySelector("dd")!.TextContent))];

    [Fact]
    public void A_card_shows_how_often_the_pattern_held_and_says_when_a_figure_is_capped_or_not_seeded()
    {
        var full = Render<SuggestionCard>(parameters => parameters.Add(card => card.Suggestion, SuggestionKit.Make("a")));

        Assert.Equal(
            [("Followed", "7 of 8 times"), ("Confidence", "88 %"), ("More likely", "3.2×"), ("Seeded", "5 of 7")],
            Figures(full));

        // The engine caps lift at 100: "over 100×" says so, and no seeded figure is shown when nothing was seeded.
        var capped = Render<SuggestionCard>(parameters => parameters.Add(card => card.Suggestion, SuggestionKit.Make("b", lift: 150, seeded: 0)));

        Assert.Equal([("Followed", "7 of 8 times"), ("Confidence", "88 %"), ("More likely", "over 100×")], Figures(capped));

        // The compact form on the trust page carries the same figures. Only the style sheet decides which of them its strip shows.
        var compact = Render<SuggestionCard>(parameters => parameters
            .Add(card => card.Suggestion, SuggestionKit.Make("c"))
            .Add(card => card.Compact, true));

        Assert.Equal(Figures(full), Figures(compact));
    }
}

/// <summary>The pipeline strip lists the next readings in the order they will be taken.</summary>
public class PipelineOrderTests : StreamComponentTests
{
    private static PipelineStats Stats(QueuedItem[] next, params CriticalTaken[] taken) => new([], 10_000, 0, StreamKit.T0, next, 20, taken);

    [Fact]
    public async Task The_strip_lists_what_will_be_taken_next_with_critical_first_and_says_when_nothing_waits()
    {
        Rig.Snapshots.Pipeline = () => new PipelineUpdate(1, Stats(
            [
                new QueuedItem(1, "critical", "AA:BB:CC:00:00:01", 12.4),
                new QueuedItem(2, "critical", "AA:BB:CC:00:00:02", 5),
                new QueuedItem(3, "standard", "AA:BB:CC:00:00:03", 800),
            ],
            new CriticalTaken("AA:BB:CC:00:00:09", 0.4, 3477, 23685)));

        var cut = Render<PipelineStrip>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("ol.smartx-pipeline__order li").Count));

        var items = cut.FindAll("ol.smartx-pipeline__order li");

        Assert.Contains("Critical", items[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("next", items[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("smartx-pipeline__order-item--critical", items[0].ClassName, StringComparison.Ordinal);
        Assert.Contains("smartx-pipeline__order-item--critical", items[1].ClassName, StringComparison.Ordinal);
        Assert.Contains("Standard", items[2].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("--critical", items[2].ClassName, StringComparison.Ordinal);

        // The rule is the server's, as it reports it.
        Assert.Contains("After 20 in a row one standard reading is taken", cut.Markup, StringComparison.Ordinal);

        // The overtaking stays on the page after the critical lane has emptied: what it was served ahead of, and for how long that had waited.
        Assert.Contains("taken ahead of 3,477 standard readings, the oldest waiting 23685 ms", cut.Find("[aria-label='Critical readings taken just now, newest first']").TextContent, StringComparison.Ordinal);

        await ListeningAsync(Rig, RealtimeMethods.PipelineUpdated);
        Rig.Hub.Deliver(RealtimeMethods.PipelineUpdated, new PipelineUpdate(2, Stats([])));

        cut.WaitForAssertion(() => Assert.Contains("Nothing is waiting", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("ol.smartx-pipeline__order li"));
    }
}

/// <summary>The alert sets and the index lookup are figures on the page.</summary>
public class SetAndLookupFigureTests : StreamComponentTests
{
    [Fact]
    public async Task The_alerts_panel_gives_the_size_of_each_of_the_three_sets()
    {
        Rig.Snapshots.Alerts = () => new AlertSnapshot(1, [], new AlertStats(9, 4, 2, 3, 1, 2), []);
        await PreloadFeedsAsync();

        var cut = Render<AlertsPanel>();
        await SettledAsync(cut);

        var sets = cut.FindAll(".smartx-alert-sets > div").Select(item => (item.QuerySelector("dt")!.TextContent, item.QuerySelector("dd")!.TextContent));

        Assert.Equal([("Active set", "3"), ("Acknowledged set", "1"), ("Disconnected set", "2")], sets);
    }

    [Fact]
    public async Task The_search_says_how_long_the_device_index_took()
    {
        Api.Search = filters => new DeviceSearchResponse(
            filters,
            1,
            [new DeviceSearchHit("AA:BB:CC:00:00:01", "Pump A", "N", "zone-1", SensorCategory.Actuator, ActuatorKind.Pump, TrustBand.Nominal, [])],
            42.6);
        await Search.RefreshNowAsync();

        RenderPopoverHost();
        var cut = Render<DeviceSearchBar>();

        Assert.Contains("found in 43 µs through the device index", cut.Find(".smartx-search__summary").TextContent, StringComparison.Ordinal);
    }
}

/// <summary>Coming back to a view shows what it showed when it was left, while the fresh data is fetched.</summary>
public class NavigationKeepsWhatWasShownTests : StreamComponentTests
{
    /// <summary>The fake mesh API with a gate in front of it, so a test can hold every request open and look at the page meanwhile.</summary>
    private sealed class GatedTopologyApi(FakeTopologyApi inner) : ITopologyApi
    {
        public Task Gate { get; set; } = Task.CompletedTask;

        public async Task<ApiResult<TopologyView>> GetTopologyAsync(CancellationToken cancellationToken = default)
        {
            await Gate;

            return await inner.GetTopologyAsync(cancellationToken);
        }

        public async Task<ApiResult<MstView>> GetMstAsync(CancellationToken cancellationToken = default)
        {
            await Gate;

            return await inner.GetMstAsync(cancellationToken);
        }

        public async Task<ApiResult<ReachabilityView>> GetReachabilityAsync(CancellationToken cancellationToken = default)
        {
            await Gate;

            return await inner.GetReachabilityAsync(cancellationToken);
        }

        public async Task<ApiResult<ArticulationView>> GetArticulationPointsAsync(CancellationToken cancellationToken = default)
        {
            await Gate;

            return await inner.GetArticulationPointsAsync(cancellationToken);
        }

        public async Task<ApiResult<RoutePathView>> GetRouteAsync(string macAddress, CancellationToken cancellationToken = default)
        {
            await Gate;

            return await inner.GetRouteAsync(macAddress, cancellationToken);
        }

        public async Task<ApiResult<HistoryRange>> GetHistoryRangeAsync(
            string macAddress, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default)
        {
            await Gate;

            return await inner.GetHistoryRangeAsync(macAddress, from, to, limit, cancellationToken);
        }
    }

    private readonly GatedTopologyApi _topology = new(new FakeTopologyApi());

    public NavigationKeepsWhatWasShownTests()
    {
        TestServices.AddSingleton<ITopologyApi>(_topology);
        TestServices.AddSingleton<ITopologyFeed>(Rig.Topology);
    }

    [Fact]
    public async Task The_topology_page_returns_to_the_last_map_while_every_request_is_still_open()
    {
        RenderPopoverHost();
        var first = Render<TopologyPage>();

        first.WaitForAssertion(() => Assert.NotEmpty(first.FindAll("g.smartx-mesh__node")));
        await SettledAsync(first);
        await DisposeComponentsAsync();

        // Back on the page with the mesh API held shut: the map is the one drawn last time, and no skeleton stands in for it.
        var shut = new TaskCompletionSource();
        _topology.Gate = shut.Task;

        RenderPopoverHost();
        var second = Render<TopologyPage>();

        Assert.NotEmpty(second.FindAll("g.smartx-mesh__node"));
        Assert.Empty(second.FindAll(".mud-skeleton"));

        shut.SetResult();
        await SettledAsync(second);
    }

    [Fact]
    public async Task The_investigations_panel_returns_to_the_list_it_had_without_a_skeleton()
    {
        Api.OpenInvestigations = () => [SuggestionKit.Investigation(Guid.NewGuid())];

        var first = Render<InvestigationsPanel>();

        first.WaitForAssertion(() => Assert.NotEmpty(first.FindAll(".smartx-investigations")));
        await SettledAsync(first);
        await DisposeComponentsAsync();

        Assert.Single(State.OpenInvestigations!);

        var second = Render<InvestigationsPanel>();

        Assert.NotEmpty(second.FindAll(".smartx-investigations"));
        Assert.Empty(second.FindAll(".mud-skeleton"));
    }
}
