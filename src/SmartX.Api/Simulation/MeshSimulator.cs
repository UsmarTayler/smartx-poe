using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartX.Api.Endpoints;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Simulation;

/// <summary>
/// Seeds the demo mesh on startup, then keeps telemetry flowing from it.
/// </summary>
/// <remarks>
/// <b>Seeding happens in <see cref="StartAsync"/>, not <see cref="ExecuteAsync"/>.</b> The host runs every
/// hosted service's StartAsync to completion before the server accepts a request, so the first page load
/// already sees forty sensors with twenty minutes of history. Seeding in ExecuteAsync would race the first
/// request and could show a half-populated dashboard.
/// <para>
/// <b>Readings take the real ingestion path.</b> Each one is judged by
/// <see cref="TelemetryEndpoints.TryCreatePacket"/>, retained in the node's ring buffer through
/// <see cref="ITelemetryStore"/>, and scored by <see cref="ITrustEngine"/> — the same three calls the
/// telemetry endpoint makes. Only the HTTP hop is skipped, because the brief asks the data structures to be
/// shown under load, not the loopback network stack.
/// </para>
/// </remarks>
internal sealed class MeshSimulator(
    IOptions<SimulationOptions> options,
    ISensorRegistry registry,
    ITelemetryStore store,
    ITrustEngine trust,
    IZoneStore zones,
    TimeProvider time,
    ILogger<MeshSimulator> logger) : BackgroundService
{
    private readonly SimulationOptions _options = options.Value;
    private readonly Random _random = new(options.Value.RandomSeed);
    private readonly Lock _gate = new();

    // Keyed by canonical MAC, like the registry and the trust engine, so a scenario change looks a node up
    // in O(1) however large the simulated mesh is.
    private readonly Dictionary<string, SimulatedNode> _nodes = new(StringComparer.Ordinal);

    public bool IsEnabled => _options.Enabled;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled)
        {
            await SeedAsync(cancellationToken);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.TickInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One failed tick must not stop the simulation for the rest of the demo.
                    logger.LogError(exception, "Simulation tick failed. The next tick will retry.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Every simulated node and its scenario, ordered by MAC.</summary>
    public IReadOnlyList<SimulatedNodeStatus> List()
    {
        lock (_gate)
        {
            return [.. _nodes.Values.Select(node => node.ToStatus()).OrderBy(status => status.MacAddress, StringComparer.Ordinal)];
        }
    }

    /// <summary>Changes a sensor's scenario, starting to simulate it if it was registered by hand.</summary>
    /// <returns>False, with a reason, when the sensor cannot run that scenario.</returns>
    public bool TrySetScenario(
        SensorRegistration sensor,
        SimulationScenario scenario,
        [NotNullWhen(true)] out SimulatedNodeStatus? status,
        [NotNullWhen(false)] out string? error)
    {
        status = null;
        error = null;

        if (!SimulatedNode.Supports(sensor, scenario))
        {
            error = $"{sensor.MacAddress} is a {nameof(TelemetryPayloadKind.State)} sensor, which has no range to " +
                    $"{(scenario == SimulationScenario.Drift ? "drift out of" : "spike beyond")}. " +
                    $"Use {nameof(SimulationScenario.Nominal)}, {nameof(SimulationScenario.TypeViolation)} or " +
                    $"{nameof(SimulationScenario.Disconnect)}.";

            return false;
        }

        var now = time.GetUtcNow();

        lock (_gate)
        {
            if (!_nodes.TryGetValue(sensor.MacAddress, out var node))
            {
                node = new SimulatedNode(sensor, scenario) { NextDue = now };
                _nodes.Add(sensor.MacAddress, node);
            }

            node.ChangeScenario(scenario, now);
            status = node.ToStatus();
        }

        logger.LogInformation("Simulated sensor {Mac} switched to {Scenario}.", sensor.MacAddress, scenario);

        return true;
    }

    /// <summary>Registers the demo sensors and replays their recent history.</summary>
    /// <remarks>
    /// Every sensor is validated against the zone hierarchy before anything is registered, and a failure
    /// stops the API. A seeder that skipped an invalid sensor would demo a mesh that could never have been
    /// registered through the API.
    /// </remarks>
    internal async Task SeedAsync(CancellationToken cancellationToken)
    {
        var validator = new ZoneValidator(await zones.GetCatalogueAsync(cancellationToken));

        var failures = DemoMesh.Sensors
            .Select(seeded => (seeded.Registration, Result: validator.Validate(seeded.Registration)))
            .Where(check => !check.Result.IsValid)
            .Select(check => $"{check.Registration.MacAddress}: {string.Join(" ", check.Result.Violations.Select(v => v.Message))}")
            .ToList();

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "The demo mesh does not satisfy its own zone constraints. " + string.Join(" ", failures));
        }

        foreach (var seeded in DemoMesh.Sensors)
        {
            if (!await registry.TryAddAsync(seeded.Registration, cancellationToken))
            {
                continue;
            }

            var node = new SimulatedNode(seeded.Registration, seeded.Scenario);

            // Each node is replayed up to the moment its own replay runs, not the moment seeding began, so a
            // node seeded last is not already a second or two overdue when the simulator takes over.
            await BackfillAsync(node, time.GetUtcNow(), cancellationToken);

            lock (_gate)
            {
                _nodes.Add(node.Sensor.MacAddress, node);
            }
        }

        logger.LogInformation(
            "Seeded {Sensors} sensors across {Zones} zones with {Duration} of history.",
            DemoMesh.Sensors.Count, DemoMesh.Zones.Count, _options.BackfillDuration);
    }

    /// <summary>
    /// Replays a node's readings from <see cref="SimulationOptions.BackfillDuration"/> ago up to now.
    /// </summary>
    /// <remarks>
    /// A node reports normally until its scenario began a few minutes ago, then follows the scenario. That is
    /// what gives each sparkline a shape — a flat healthy line that bends where the fault started — instead of
    /// every node looking as if it had been failing forever. A disconnected node's replay simply stops at that
    /// moment, and the heartbeat monitor's first sweep finds the silence.
    /// </remarks>
    private async Task BackfillAsync(SimulatedNode node, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset timestamp;
        DateTimeOffset onset;

        lock (_gate)
        {
            // Staggered, so forty sensors do not all report on the same second.
            timestamp = now - _options.BackfillDuration + node.Cadence * _random.NextDouble();
            onset = now - TimeSpan.FromSeconds(_random.Next(240, 600));
        }

        while (timestamp <= now)
        {
            var scenario = timestamp < onset ? SimulationScenario.Nominal : node.Scenario;

            if (scenario == SimulationScenario.Disconnect)
            {
                break;
            }

            JsonElement reading;

            lock (_gate)
            {
                reading = node.NextReading(scenario, _random);
            }

            await IngestAsync(node.Sensor, reading, timestamp, cancellationToken);
            timestamp += node.Cadence;
        }

        node.NextDue = timestamp;
    }

    /// <summary>Sends a reading from every node that is due.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        List<(SensorRegistration Sensor, JsonElement Reading)> due = [];

        // Readings are generated under the lock, because the nodes and the Random are not thread-safe, and
        // ingested after it is released, so a scenario change never waits on the telemetry store.
        lock (_gate)
        {
            foreach (var node in _nodes.Values)
            {
                if (node.Scenario == SimulationScenario.Disconnect || node.NextDue > now)
                {
                    continue;
                }

                due.Add((node.Sensor, node.NextReading(node.Scenario, _random)));

                // Kept on its own cadence rather than reset to now, so a slow tick does not stretch the
                // interval. A node that has fallen more than an interval behind skips ahead instead of
                // bursting to catch up.
                node.NextDue += node.Cadence;

                if (node.NextDue <= now)
                {
                    node.NextDue = now + node.Cadence;
                }
            }
        }

        foreach (var (sensor, reading) in due)
        {
            await IngestAsync(sensor, reading, now, cancellationToken);
        }
    }

    private async Task IngestAsync(
        SensorRegistration sensor,
        JsonElement reading,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (TelemetryEndpoints.TryCreatePacket(sensor, reading, timestamp, out var packet, out var rejection))
        {
            await store.AddAsync(packet, cancellationToken);
            trust.Record(sensor.MacAddress, TrustOutcome.Success, timestamp);
        }
        else
        {
            trust.Record(sensor.MacAddress, rejection.Outcome, timestamp);
        }
    }
}
