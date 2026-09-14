using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Simulation;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the demo mesh seeder, the simulator's scenarios, and the endpoint that switches them.
/// </summary>
/// <remarks>
/// The seeding and scenario tests build the simulator by hand against a manual clock, so the replayed history
/// and every band it produces are exactly reproducible. The endpoint tests go through the real API.
/// </remarks>
public class SimulationTests
{
    private const string DriftingThermometer = "B8:27:EB:10:00:02";
    private const string NominalThermometer = "B8:27:EB:10:00:01";
    private const string DisconnectedFeeder = "B8:27:EB:10:00:13";
    private const string CracUnit = "B8:27:EB:10:00:06";

    [Fact]
    public void Every_seeded_sensor_satisfies_the_demo_zone_hierarchy()
    {
        var validator = new ZoneValidator(new ZoneCatalogue(DemoMesh.Zones));

        Assert.All(DemoMesh.Sensors, seeded => Assert.True(
            validator.Validate(seeded.Registration).IsValid,
            $"{seeded.Registration.MacAddress} is not valid in {seeded.Registration.Location.ZoneId}."));

        // Three levels deep, and every category and payload kind represented.
        Assert.Equal(2, DemoMesh.Zones.Max(zone => zone.Depth));
        Assert.Equal(Enum.GetValues<SensorCategory>(), DemoMesh.Sensors.Select(s => s.Registration.Category).Distinct().Order());
        Assert.Equal(Enum.GetValues<TelemetryPayloadKind>(), DemoMesh.Sensors.Select(s => s.Registration.PayloadKind).Distinct().Order());
    }

    [Fact]
    public async Task Seeding_backfills_history_and_spreads_nodes_across_the_bands()
    {
        var mesh = await SeededMesh.CreateAsync();

        var nodes = mesh.Trust.Snapshot();

        Assert.Equal(DemoMesh.Sensors.Count, nodes.Count);

        // A full sparkline on the first page load, not a single starting point.
        Assert.All(nodes, node => Assert.Equal(new TrustOptions().SnapshotHistoryPoints, node.RecentHistory.Count));
        Assert.Equal(256, await mesh.Store.CountForAsync(NominalThermometer));

        var byScenario = nodes.ToLookup(node => ScenarioOf(node.MacAddress));

        // Every band a dashboard should show is present, rather than a wall of one colour.
        Assert.Equal([TrustBand.Nominal, TrustBand.Degrading, TrustBand.Fault], nodes.Select(node => node.Band).Distinct().Order());
        Assert.All(byScenario[SimulationScenario.Nominal], node => Assert.Equal(TrustBand.Nominal, node.Band));
        Assert.All(byScenario[SimulationScenario.Disconnect], node => Assert.Equal(TrustBand.Fault, node.Band));

        // A node only a few minutes into a fault may still be nominal, but it already scores below every
        // healthy node, so the worst-first list ranks it above them.
        var healthiest = byScenario[SimulationScenario.Nominal].Min(node => node.Score);
        Assert.All(nodes.Where(node => ScenarioOf(node.MacAddress) != SimulationScenario.Nominal),
            node => Assert.True(node.Score < healthiest, $"{node.MacAddress} scores {node.Score:0.00}."));
    }

    [Fact]
    public async Task A_disconnected_node_stops_reporting_and_reconnecting_resumes_it()
    {
        var mesh = await SeededMesh.CreateAsync();
        var sensor = (await mesh.Registry.FindAsync(NominalThermometer))!;

        Assert.True(mesh.Simulator.TrySetScenario(sensor, SimulationScenario.Disconnect, out _, out _));
        var before = await mesh.Latest(NominalThermometer);

        await mesh.RunForAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(before, await mesh.Latest(NominalThermometer));
        Assert.True(await mesh.Latest("B8:27:EB:10:00:03") > before, "Other nodes should keep reporting.");

        Assert.True(mesh.Simulator.TrySetScenario(sensor, SimulationScenario.Nominal, out _, out _));
        await mesh.RunForAsync(TimeSpan.FromSeconds(1));

        Assert.True(await mesh.Latest(NominalThermometer) > before);
    }

    [Fact]
    public async Task Live_readings_never_miss_a_heartbeat_on_a_healthy_node()
    {
        // The regression the reporting cadence exists for: reporting exactly on the interval let a late tick
        // land after the heartbeat monitor had already counted the interval as missed.
        var mesh = await SeededMesh.CreateAsync();

        await mesh.RunForAsync(TimeSpan.FromMinutes(2));

        Assert.True(mesh.Trust.TryGet(NominalThermometer, out var node));
        Assert.DoesNotContain("heartbeat", node.Reason, StringComparison.Ordinal);
        Assert.Equal(TrustBand.Nominal, node.Band);
    }

    [Fact]
    public async Task The_scenario_endpoint_refuses_while_simulation_is_disabled()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/simulation/{DriftingThermometer}/scenario", new ScenarioChangeRequest(SimulationScenario.Drift));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(DisconnectedFeeder, "Nominal", HttpStatusCode.OK)]
    [InlineData(CracUnit, "Drift", HttpStatusCode.BadRequest)]
    [InlineData("AA:BB:CC:DD:EE:99", "Spike", HttpStatusCode.NotFound)]
    public async Task The_scenario_endpoint_switches_seeded_sensors_live(string mac, string scenario, HttpStatusCode expected)
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Simulation:Enabled"] = "true" })));
        var client = api.CreateClient();

        var response = await client.PostAsync($"/api/simulation/{mac}/scenario", Json($$"""{ "scenario": "{{scenario}}" }"""));

        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.OK)
        {
            var statuses = await client.GetFromJsonAsync<List<SimulatedNodeStatus>>("/api/simulation");
            Assert.Equal(SimulationScenario.Nominal, statuses!.Single(status => status.MacAddress == mac).Scenario);
        }
    }

    private static SimulationScenario ScenarioOf(string mac) =>
        DemoMesh.Sensors.Single(seeded => seeded.Registration.MacAddress == mac).Scenario;

    /// <summary>A seeded simulator, heartbeat monitor and stores sharing one manual clock.</summary>
    private sealed record SeededMesh(
        MeshSimulator Simulator,
        HeartbeatMonitor Monitor,
        InMemorySensorRegistry Registry,
        InMemoryTelemetryStore Store,
        TrustEngine Trust,
        ManualTimeProvider Clock)
    {
        public static async Task<SeededMesh> CreateAsync()
        {
            var clock = new ManualTimeProvider(Start);
            var trustOptions = Options.Create(new TrustOptions());
            var registry = new InMemorySensorRegistry();
            var store = new InMemoryTelemetryStore(Options.Create(new TelemetryOptions()));
            var trust = new TrustEngine(trustOptions, clock, NullLogger<TrustEngine>.Instance);

            var simulator = new MeshSimulator(
                Options.Create(new SimulationOptions { Enabled = true }),
                registry,
                store,
                trust,
                new InMemoryZoneStore(new ZoneCatalogue(DemoMesh.Zones)),
                clock,
                NullLogger<MeshSimulator>.Instance);

            var monitor = new HeartbeatMonitor(registry, trust, trustOptions, clock, NullLogger<HeartbeatMonitor>.Instance);

            await simulator.SeedAsync(CancellationToken.None);
            await monitor.SweepAsync(CancellationToken.None);

            return new SeededMesh(simulator, monitor, registry, store, trust, clock);
        }

        /// <summary>Advances the clock in simulator ticks, sweeping for heartbeats as the real services would.</summary>
        public async Task RunForAsync(TimeSpan duration)
        {
            var tick = new SimulationOptions().TickInterval;

            for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += tick)
            {
                // Late on purpose: every tick is a whole tick after the last, the worst case for a reading
                // that became due just after the previous one.
                Clock.Advance(tick);
                await Monitor.SweepAsync(CancellationToken.None);
                await Simulator.TickAsync(CancellationToken.None);
            }
        }

        public async Task<DateTimeOffset> Latest(string mac) => (await Store.RecentAsync(mac))[^1].Timestamp;
    }
}
