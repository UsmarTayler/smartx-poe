using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.DeviceSimulator;

/// <summary>
/// Runs the fleet: registers every device, fills in history, then keeps them reporting and polling.
/// </summary>
/// <remarks>
/// This is the whole program. It replaces the in-process seeder from Part 1, which called the gateway's
/// services directly — convenient, but not what the brief asks for, and it proved nothing about the API
/// under real traffic. Every reading here crosses the network, is validated by the gateway, and is queued
/// like any other device's.
/// </remarks>
internal sealed class DeviceFleet(
    GatewayClient gateway,
    IOptions<SimulatorOptions> options,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<DeviceFleet> logger) : BackgroundService
{
    private readonly SimulatorOptions _options = options.Value;
    private readonly Random _random = new(options.Value.RandomSeed);
    private readonly Lock _gate = new();
    private readonly List<SimulatedDevice> _devices = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await gateway.WaitForGatewayAsync(_options.StartupTimeout, stoppingToken))
        {
            logger.LogCritical("The gateway never came up. Stopping.");
            lifetime.StopApplication();

            return;
        }

        if (!await RegisterFleetAsync(stoppingToken))
        {
            lifetime.StopApplication();

            return;
        }

        await BackfillAsync(stoppingToken);
        await ReportAsync(stoppingToken);
    }

    /// <summary>The fleet as it stands, so a test can inspect what registration produced.</summary>
    internal IReadOnlyList<SimulatedDevice> DevicesForTest => _devices;

    /// <summary>Registration on its own, so a test can run it without starting the reporting loop.</summary>
    internal Task<bool> RegisterFleetForTestAsync() => RegisterFleetAsync(CancellationToken.None);

    /// <summary>Introduces every device to the gateway.</summary>
    private async Task<bool> RegisterFleetAsync(CancellationToken cancellationToken)
    {
        var fleet = DemoDevices.All.Take(Math.Clamp(_options.DeviceCount, 0, DemoDevices.All.Count));
        var registered = 0;

        // Keys are normalised so a MAC written for an environment variable (underscores or hyphens) finds
        // the device it names.
        var overrides = _options.Scenarios.ToDictionary(
            pair => pair.Key.Replace('_', ':').Replace('-', ':').ToUpperInvariant(),
            pair => pair.Value,
            StringComparer.Ordinal);

        foreach (var definition in fleet)
        {
            if (!await gateway.RegisterAsync(definition.Registration, cancellationToken))
            {
                logger.LogCritical(
                    "Device {Mac} could not register, so the fleet is not what it claims to be. Stopping.",
                    definition.MacAddress);

                return false;
            }

            var device = new SimulatedDevice(definition);

            if (_options.Mix == FleetMix.Nominal)
            {
                device.StartIn(SimulationScenario.Nominal);
            }

            if (overrides.TryGetValue(device.MacAddress, out var scenario))
            {
                device.StartIn(scenario);
            }

            _devices.Add(device);
            registered++;
        }

        logger.LogInformation("Registered {Count} devices with the gateway.", registered);

        return true;
    }

    /// <summary>
    /// Posts each device's recent history, so the dashboard has something to show immediately.
    /// </summary>
    /// <remarks>
    /// Readings are stamped with the time they would have been taken, so trust decay and the sparklines are
    /// shaped as if the fleet had been running. A device that starts a scenario partway through the window
    /// reports normally before that point, which is what gives each sparkline a bend rather than a flat line.
    /// <para>
    /// Sent with a small amount of concurrency: thousands of round trips one after another would take
    /// minutes, and the gateway is perfectly happy to be asked by several devices at once — that is what it
    /// is for.
    /// </para>
    /// </remarks>
    private async Task BackfillAsync(CancellationToken cancellationToken)
    {
        if (_options.BackfillDuration <= TimeSpan.Zero)
        {
            return;
        }

        var started = time.GetUtcNow();
        using var concurrency = new SemaphoreSlim(Math.Max(1, _options.BackfillConcurrency));
        var sent = 0;

        var work = _devices.Select(async device =>
        {
            await concurrency.WaitAsync(cancellationToken);

            try
            {
                // Interlocked, and the await first: `sent += await ...` reads sent before the await and writes it
                // after, so two devices finishing together overwrite each other and the total comes out short.
                var posted = await BackfillDeviceAsync(device, started, cancellationToken);
                Interlocked.Add(ref sent, posted);
            }
            finally
            {
                concurrency.Release();
            }
        });

        await Task.WhenAll(work);

        logger.LogInformation(
            "Backfilled {Readings} readings covering {Duration} in {Elapsed}.",
            sent,
            _options.BackfillDuration,
            time.GetUtcNow() - started);
    }

    private async Task<int> BackfillDeviceAsync(SimulatedDevice device, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset timestamp;
        DateTimeOffset onset;

        lock (_gate)
        {
            // Staggered, so forty devices do not all report on the same second.
            timestamp = now - _options.BackfillDuration + device.Cadence * _random.NextDouble();
            onset = now - TimeSpan.FromSeconds(_random.Next(120, 360));
        }

        var sent = 0;

        while (timestamp <= now && !cancellationToken.IsCancellationRequested)
        {
            var scenario = timestamp < onset ? SimulationScenario.Nominal : device.Scenario;

            if (scenario == SimulationScenario.Disconnect)
            {
                break;
            }

            JsonElement reading;

            lock (_gate)
            {
                reading = device.NextReading(scenario, _random);
            }

            if (await SendWithBackpressureAsync(device, reading, timestamp, cancellationToken))
            {
                sent++;
            }

            timestamp += device.Cadence;
        }

        device.NextDue = timestamp;
        device.NextShadowPoll = now;

        return sent;
    }

    /// <summary>The steady state: report when due, poll the shadow when due.</summary>
    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = time.GetUtcNow();

                await Task.WhenAll(_devices.Select(device => TickDeviceAsync(device, now, cancellationToken)));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task TickDeviceAsync(SimulatedDevice device, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (device.NextShadowPoll <= now)
        {
            device.NextShadowPoll = now + _options.ShadowPollInterval;
            await ApplyDesiredStateAsync(device, now, cancellationToken);
        }

        if (device.Scenario == SimulationScenario.Disconnect || device.NextDue > now)
        {
            return;
        }

        // Kept on its own cadence rather than reset to now, so a slow tick does not stretch the interval. A
        // device that has fallen more than an interval behind skips ahead instead of bursting to catch up.
        device.NextDue += device.Cadence;

        if (device.NextDue <= now)
        {
            device.NextDue = now + device.Cadence;
        }

        JsonElement reading;

        lock (_gate)
        {
            reading = device.NextReading(device.Scenario, _random);
        }

        await SendWithBackpressureAsync(device, reading, timestamp: null, cancellationToken);
    }

    /// <summary>Sends a reading, honouring the gateway asking for it later.</summary>
    /// <remarks>
    /// A 503 means the gateway is saturated. Retrying immediately would make that worse, so the device waits
    /// and tries once more; if it is still busy, the reading is dropped and the next one is along shortly.
    /// Dropping is the honest behaviour for a device with no storage of its own.
    /// </remarks>
    internal async Task<bool> SendWithBackpressureAsync(
        SimulatedDevice device,
        JsonElement reading,
        DateTimeOffset? timestamp,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var outcome = await gateway.SendReadingAsync(device.MacAddress, reading, timestamp, cancellationToken);

            switch (outcome)
            {
                case SendOutcome.Accepted:
                    return true;

                // A refusal is the gateway doing its job — this device is deliberately sending a bad reading.
                case SendOutcome.Refused:
                    return false;

                case SendOutcome.Busy:
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                    break;

                case SendOutcome.NotRegistered:
                    // The gateway restarted and lost its device list. Register again and send this reading
                    // once more; if that does not work, the next tick will try the whole thing again.
                    logger.LogWarning("The gateway does not know {Mac}. Registering it again.", device.MacAddress);

                    if (!await gateway.RegisterAsync(device.Registration, cancellationToken))
                    {
                        return false;
                    }

                    break;

                case SendOutcome.Unreachable:
                    return false;
            }
        }

        return false;
    }

    /// <summary>Applies whatever an operator has asked of this device since its last poll.</summary>
    private async Task ApplyDesiredStateAsync(SimulatedDevice device, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var desired = await gateway.GetDesiredStateAsync(device.MacAddress, cancellationToken);

        if (desired is null || desired.Version == device.AppliedVersion)
        {
            return;
        }

        if (device.TryApply(desired, now))
        {
            logger.LogInformation(
                "{Mac} switched to {Scenario} (desired state version {Version}).",
                device.MacAddress,
                desired.Scenario,
                desired.Version);
        }
        else
        {
            logger.LogWarning(
                "{Mac} was asked for {Scenario}, which a {Kind} device cannot do. Staying on {Current}.",
                device.MacAddress,
                desired.Scenario,
                device.Registration.PayloadKind,
                device.Scenario);
        }
    }
}
