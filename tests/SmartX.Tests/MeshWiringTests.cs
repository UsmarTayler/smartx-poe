using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.History;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// The trust engine's band changes, and what is wired to them: alerts, the device index and history.
/// </summary>
public class MeshWiringTests
{
    private static SensorRegistration Sensor(string mac, TimeSpan? heartbeat = null) => new(
        mac, new DeploymentLocation("Plant Room", "zone-1", "N1"), SensorCategory.Environmental, "°C",
        new ValueRange(0, 100), TelemetryPayloadKind.Real, heartbeat);

    private static async Task<(TrustEngine Engine, ManualTimeProvider Clock, MeshTestHost Mesh, TrustEventBridge Bridge)> WireAsync(params string[] nodes)
    {
        var (engine, clock) = Create();
        var mesh = MeshTestHost.Create(clock);
        var bridge = new TrustEventBridge(engine, mesh.Index, mesh.Alerts, mesh.History, mesh.Sequence);

        foreach (var node in nodes)
        {
            mesh.Index.Register(Sensor(node));
            engine.Track(node);
        }

        await bridge.StartAsync(CancellationToken.None);

        return (engine, clock, mesh, bridge);
    }

    private static void Succeed(ITrustEngine engine, ManualTimeProvider clock, string node, int times)
    {
        for (var i = 0; i < times; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            engine.Record(node, TrustOutcome.Success);
        }
    }

    private static void Fail(ITrustEngine engine, ManualTimeProvider clock, string node, int times, TrustOutcome outcome = TrustOutcome.RangeViolation)
    {
        for (var i = 0; i < times; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            engine.Record(node, outcome);
        }
    }

    // ---- the trust engine's new event --------------------------------------------------------------------------

    [Fact]
    public void Every_change_of_band_is_announced_with_the_band_it_left_and_the_band_it_entered()
    {
        var (engine, clock) = Create();
        List<TrustTransition> changes = [];
        engine.BandChanged += changes.Add;

        engine.Track(NodeA);
        Succeed(engine, clock, NodeA, 6);
        Fail(engine, clock, NodeA, 12);

        // The first success proves the node, but one success is not yet enough to be nominal.
        Assert.NotEmpty(changes);
        Assert.Equal(TrustBand.Unproven, changes[0].PreviousBand);
        Assert.Equal(TrustBand.Degrading, changes[0].Band);
        Assert.Contains(changes, change => change.Band == TrustBand.Nominal);

        // A chain: each change starts where the last one ended, and none of them is a change from a band to itself.
        for (var i = 1; i < changes.Count; i++)
        {
            Assert.Equal(changes[i - 1].Band, changes[i].PreviousBand);
        }

        Assert.All(changes, change => Assert.NotEqual(change.PreviousBand, change.Band));
        Assert.Equal(TrustBand.Fault, changes[^1].Band);
    }

    [Fact]
    public void Tracking_a_node_is_a_start_and_not_a_band_change()
    {
        var (engine, _) = Create();
        var changes = 0;
        engine.BandChanged += _ => changes++;

        engine.Track(NodeA);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_packet_that_leaves_the_band_where_it_was_announces_nothing()
    {
        var (engine, clock) = Create();
        engine.Track(NodeA);
        Succeed(engine, clock, NodeA, 6);

        var changes = 0;
        engine.BandChanged += _ => changes++;

        Succeed(engine, clock, NodeA, 20);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void The_fault_events_are_unchanged_and_still_fire_once_per_crossing()
    {
        var (engine, clock) = Create();
        var entered = 0;
        var cleared = 0;
        engine.FaultEntered += _ => entered++;
        engine.FaultCleared += _ => cleared++;

        engine.Track(NodeA);
        Fail(engine, clock, NodeA, 5);
        Succeed(engine, clock, NodeA, 30);

        Assert.Equal(1, entered);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void A_band_subscriber_that_throws_does_not_stop_the_others_or_the_engine()
    {
        var (engine, clock) = Create();
        var heard = 0;
        engine.BandChanged += _ => throw new InvalidOperationException("a faulty subscriber");
        engine.BandChanged += _ => heard++;

        engine.Track(NodeA);
        Succeed(engine, clock, NodeA, 6);

        Assert.True(heard > 0);
        Assert.Equal(TrustBand.Nominal, Trust(engine, NodeA).Band);
    }

    // ---- the bridge ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_node_entering_fault_raises_a_fault_alert_and_recovering_clears_it()
    {
        var (engine, clock, mesh, _) = await WireAsync(NodeA);

        Fail(engine, clock, NodeA, 3);

        var alert = Assert.Single(mesh.Alerts.Active());
        Assert.Equal(new AlertKey(NodeA, AlertKind.Fault), alert.Key);
        Assert.False(string.IsNullOrWhiteSpace(alert.Detail));

        // More failures while still in fault do not raise it again: the engine only announces a crossing, so
        // there is nothing to suppress.
        Fail(engine, clock, NodeA, 3);
        Assert.Equal(0, Assert.Single(mesh.Alerts.Active()).SuppressedCount);

        Succeed(engine, clock, NodeA, 30);

        Assert.Empty(mesh.Alerts.Active());
        Assert.Equal(1, mesh.Alerts.Stats().Cleared);
    }

    [Fact]
    public async Task Every_band_change_updates_the_index_and_is_recorded_in_history()
    {
        var (engine, clock, mesh, _) = await WireAsync(NodeA);

        Succeed(engine, clock, NodeA, 6);

        Assert.Equal(TrustBand.Nominal, mesh.Index.BandOf(NodeA));

        Fail(engine, clock, NodeA, 12);

        Assert.Equal(TrustBand.Fault, mesh.Index.BandOf(NodeA));

        var transitions = mesh.History.GetEvents(Start.AddDays(-1), Start.AddDays(1))
            .Where(entry => entry.Kind == HistoryKind.TrustTransition)
            .ToList();

        Assert.True(transitions.Count >= 2);
        Assert.Equal(TrustBand.Unproven, transitions[0].Event!.PreviousBand);
        Assert.Contains(transitions, entry => entry.Event!.Band == TrustBand.Nominal);
        Assert.Equal(TrustBand.Fault, transitions[^1].Event!.Band);
        Assert.All(transitions, entry => Assert.Equal(NodeA, entry.MacAddress));
        Assert.False(string.IsNullOrWhiteSpace(transitions[^1].Event!.Detail));
        Assert.Empty(mesh.Index.FindInconsistencies());
    }

    [Fact]
    public async Task The_index_agrees_with_the_trust_engines_degraded_set_after_every_step()
    {
        // The Part 1 degraded set and the new band index must say the same thing. The index is fed by the
        // engine's own events, so this is the proof that it follows the engine and does not keep a rival
        // definition of "degraded".
        var (engine, clock, mesh, _) = await WireAsync(NodeA, NodeB, NodeC);
        var random = new Random(11);
        string[] nodes = [NodeA, NodeB, NodeC];

        for (var step = 0; step < 400; step++)
        {
            var node = nodes[random.Next(nodes.Length)];
            clock.Advance(TimeSpan.FromSeconds(random.Next(1, 90)));

            switch (random.Next(4))
            {
                case 0:
                    engine.Record(node, TrustOutcome.Success);
                    break;
                case 1:
                    engine.Record(node, TrustOutcome.RangeViolation);
                    break;
                case 2:
                    engine.Record(node, TrustOutcome.SchemaFailure);
                    break;
                default:
                    engine.Reevaluate();
                    break;
            }

            var fromEngine = engine.DegradedNodes.Order(StringComparer.Ordinal).ToList();
            var fromIndex = nodes
                .Where(mac => mesh.Index.BandOf(mac) is TrustBand.Degrading or TrustBand.Fault)
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.Equal(fromEngine, fromIndex);
            Assert.All(nodes, mac => Assert.Equal(Trust(engine, mac).Band, mesh.Index.BandOf(mac)));
        }
    }

    [Fact]
    public async Task A_fault_whose_evidence_fades_to_nothing_is_not_cleared_as_if_it_were_repaired()
    {
        var (engine, clock, mesh, _) = await WireAsync(NodeA);
        Fail(engine, clock, NodeA, 3);
        Assert.Single(mesh.Alerts.Active());

        // A day later, every outcome has decayed to nothing and the node is unproven. That is the absence of
        // information and not a recovery, so the engine does not raise FaultCleared, and the alert stands until
        // something real clears it.
        clock.Advance(TimeSpan.FromHours(30));
        engine.Reevaluate();

        Assert.Equal(TrustBand.Unproven, Trust(engine, NodeA).Band);
        Assert.Equal(TrustBand.Unproven, mesh.Index.BandOf(NodeA));
        Assert.Single(mesh.Alerts.Active());
    }

    [Fact]
    public async Task After_the_bridge_stops_it_no_longer_listens()
    {
        var (engine, clock, mesh, bridge) = await WireAsync(NodeA);

        await bridge.StopAsync(CancellationToken.None);
        Fail(engine, clock, NodeA, 3);

        Assert.Empty(mesh.Alerts.Active());
        Assert.Equal(TrustBand.Unproven, mesh.Index.BandOf(NodeA));
    }

    // ---- the heartbeat monitor and silence -------------------------------------------------------------------

    private static async Task<(HeartbeatMonitor Monitor, TrustEngine Engine, ManualTimeProvider Clock, MeshTestHost Mesh)> CreateMonitorAsync(
        TimeSpan heartbeat, int disconnectedAfter = 3)
    {
        var trustOptions = Options.Create(new TrustOptions());
        var clock = new ManualTimeProvider(Start);
        var engine = new TrustEngine(trustOptions, clock, NullLogger<TrustEngine>.Instance);
        var mesh = MeshTestHost.Create(clock);
        var registry = new InMemorySensorRegistry();
        Assert.True(await registry.TryAddAsync(Sensor(NodeA, heartbeat)));
        engine.Track(NodeA);

        var monitor = new HeartbeatMonitor(
            registry,
            engine,
            trustOptions,
            mesh.Alerts,
            Options.Create(new AlertOptions { DisconnectedAfterIntervals = disconnectedAfter }),
            clock,
            NullLogger<HeartbeatMonitor>.Instance);

        return (monitor, engine, clock, mesh);
    }

    [Fact]
    public async Task A_node_silent_for_less_than_the_threshold_is_not_disconnected()
    {
        var (monitor, _, clock, mesh) = await CreateMonitorAsync(TimeSpan.FromSeconds(10));

        // Three intervals of ten seconds. Twenty-five seconds is two and a half.
        clock.Advance(TimeSpan.FromSeconds(25));
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Empty(mesh.Alerts.DisconnectedNodes);
        Assert.DoesNotContain(mesh.Alerts.Active(), alert => alert.Key.Kind == AlertKind.Disconnected);
    }

    [Fact]
    public async Task A_node_silent_for_the_threshold_raises_one_disconnected_alert_and_later_sweeps_count_as_repeats()
    {
        var (monitor, _, clock, mesh) = await CreateMonitorAsync(TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(30));
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Equal([NodeA], mesh.Alerts.DisconnectedNodes);
        var alert = Assert.Single(mesh.Alerts.Active(), alert => alert.Key.Kind == AlertKind.Disconnected);
        Assert.Equal(0, alert.SuppressedCount);
        Assert.Contains("30s", alert.Detail, StringComparison.Ordinal);

        // The monitor sweeps every few seconds, and finds the node still silent each time.
        for (var sweep = 0; sweep < 14; sweep++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await monitor.SweepAsync(CancellationToken.None);
        }

        alert = Assert.Single(mesh.Alerts.Active(), alert => alert.Key.Kind == AlertKind.Disconnected);
        Assert.Equal(14, alert.SuppressedCount);
        Assert.True(mesh.Alerts.DisconnectedSetMatchesAlerts());
    }

    [Fact]
    public async Task A_node_that_reports_again_is_no_longer_disconnected()
    {
        var (monitor, engine, clock, mesh) = await CreateMonitorAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(40));
        await monitor.SweepAsync(CancellationToken.None);
        Assert.Single(mesh.Alerts.DisconnectedNodes);

        // The next sweep after a packet finds it fresh, even if the ingestion path has not cleared it yet.
        engine.Record(NodeA, TrustOutcome.Success);
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Empty(mesh.Alerts.DisconnectedNodes);
        Assert.DoesNotContain(mesh.Alerts.Active(), alert => alert.Key.Kind == AlertKind.Disconnected);
    }

    [Fact]
    public async Task A_second_outage_after_recovery_is_a_new_alert()
    {
        var (monitor, engine, clock, mesh) = await CreateMonitorAsync(TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(40));
        await monitor.SweepAsync(CancellationToken.None);
        engine.Record(NodeA, TrustOutcome.Success);
        await monitor.SweepAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(40));
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Equal(2, mesh.Alerts.Stats().Raised);
        Assert.Equal(1, mesh.Alerts.Stats().Cleared);
    }

    [Fact]
    public async Task A_slow_reporter_is_measured_against_its_own_interval()
    {
        var (monitor, _, clock, mesh) = await CreateMonitorAsync(TimeSpan.FromMinutes(5));

        // Ten minutes is a long silence for a node that reports every ten seconds and nothing for one that
        // reports every five minutes.
        clock.Advance(TimeSpan.FromMinutes(10));
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Empty(mesh.Alerts.DisconnectedNodes);

        clock.Advance(TimeSpan.FromMinutes(5));
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Equal([NodeA], mesh.Alerts.DisconnectedNodes);
    }

    [Fact]
    public async Task A_sensor_that_registers_and_never_reports_is_caught_from_when_it_was_tracked()
    {
        var (monitor, _, clock, mesh) = await CreateMonitorAsync(TimeSpan.FromSeconds(10));

        // Never sent a packet at all.
        clock.Advance(TimeSpan.FromSeconds(31));
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Equal([NodeA], mesh.Alerts.DisconnectedNodes);
    }
}
