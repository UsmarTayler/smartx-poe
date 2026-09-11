using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the background service that applies missed-heartbeat penalties.
/// </summary>
/// <remarks>
/// Most of these call <see cref="HeartbeatMonitor.SweepAsync"/> directly against a manual clock, so
/// the sweep's logic is exercised deterministically. The last starts the real service on the real
/// clock, to prove the loop itself runs the sweep without being asked.
/// </remarks>
public class HeartbeatMonitorTests
{
    private static SensorRegistration Sensor(string mac, TimeSpan? heartbeat) =>
        new(mac,
            new DeploymentLocation("Plant Room", "zone-1", "NODE-1"),
            SensorCategory.Environmental,
            "°C",
            new ValueRange(0, 50),
            TelemetryPayloadKind.Real,
            heartbeat);

    private static (TrustEngine Engine, HeartbeatMonitor Monitor, InMemorySensorRegistry Registry, ManualTimeProvider Clock)
        CreateMonitor(Action<TrustOptions>? configure = null)
    {
        var settings = new TrustOptions();
        configure?.Invoke(settings);

        var options = Options.Create(settings);
        var clock = new ManualTimeProvider(Start);
        var engine = new TrustEngine(options, clock, NullLogger<TrustEngine>.Instance);
        var registry = new InMemorySensorRegistry();
        var monitor = new HeartbeatMonitor(registry, engine, options, clock, NullLogger<HeartbeatMonitor>.Instance);

        return (engine, monitor, registry, clock);
    }

    [Fact]
    public async Task A_sweep_penalises_silent_registered_sensors()
    {
        var (engine, monitor, registry, clock) = CreateMonitor();
        await registry.TryAddAsync(Sensor(NodeA, TimeSpan.FromSeconds(30)));
        engine.Track(NodeA);

        clock.Advance(TimeSpan.FromSeconds(95));

        Assert.Equal(3, await monitor.SweepAsync(CancellationToken.None));
        Assert.Equal(3, Trust(engine, NodeA).RecentFailures);
    }

    [Fact]
    public async Task Each_sensor_is_held_to_its_own_registered_interval()
    {
        // A minute is six missed intervals for a ten-second door contact and none at all for a meter
        // that reports every five minutes.
        var (engine, monitor, registry, clock) = CreateMonitor();
        await registry.TryAddAsync(Sensor(NodeA, TimeSpan.FromSeconds(10)));
        await registry.TryAddAsync(Sensor(NodeB, TimeSpan.FromMinutes(5)));
        engine.Track(NodeA);
        engine.Track(NodeB);

        clock.Advance(TimeSpan.FromSeconds(60));
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Equal(6, Trust(engine, NodeA).RecentFailures);
        Assert.Equal(0, Trust(engine, NodeB).RecentFailures);
    }

    [Fact]
    public async Task A_sensor_without_an_interval_uses_the_configured_default()
    {
        var (engine, monitor, registry, clock) =
            CreateMonitor(options => options.DefaultHeartbeatInterval = TimeSpan.FromSeconds(20));
        await registry.TryAddAsync(Sensor(NodeA, heartbeat: null));
        engine.Track(NodeA);

        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(3, await monitor.SweepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_sweep_ages_stale_good_evidence_back_to_degrading()
    {
        // No sensor is registered, so no penalties apply: this is the re-evaluation half of the
        // sweep on its own. Four clean packets forty minutes ago no longer make a node nominal.
        var (engine, monitor, _, clock) = CreateMonitor();
        RecordMany(engine, NodeA, TrustOutcome.Success, 4);
        Assert.Equal(TrustBand.Nominal, Trust(engine, NodeA).Band);

        clock.Advance(TimeSpan.FromMinutes(40));
        await monitor.SweepAsync(CancellationToken.None);

        Assert.Equal(TrustBand.Degrading, Trust(engine, NodeA).Band);
    }

    [Fact]
    public async Task The_running_service_applies_penalties_on_its_own_schedule()
    {
        var options = Options.Create(new TrustOptions { HeartbeatCheckInterval = TimeSpan.FromMilliseconds(20) });
        var engine = new TrustEngine(options, TimeProvider.System, NullLogger<TrustEngine>.Instance);
        var registry = new InMemorySensorRegistry();
        await registry.TryAddAsync(Sensor(NodeA, TimeSpan.FromMilliseconds(50)));
        engine.Track(NodeA);

        using var monitor = new HeartbeatMonitor(
            registry, engine, options, TimeProvider.System, NullLogger<HeartbeatMonitor>.Instance);

        await monitor.StartAsync(CancellationToken.None);

        try
        {
            // Polled with a generous deadline rather than a fixed sleep, so a slow CI machine waits
            // longer instead of failing.
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (Trust(engine, NodeA).RecentFailures == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }

        Assert.True(Trust(engine, NodeA).RecentFailures > 0, "The service never applied a penalty.");
    }
}
