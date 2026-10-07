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

    /// <summary>One device's turn of the loop, so a test can drive it with a time of its choosing.</summary>
    internal Task TickDeviceForTestAsync(SimulatedDevice device, DateTimeOffset now, CancellationToken cancellationToken) =>
        TickDeviceAsync(device, now, cancellationToken);

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

    /// <summary>How often a device reports now: its own cadence, sped up by the configured multiplier.</summary>
    private TimeSpan LiveCadence(SimulatedDevice device) =>
        _options.ReportingRateMultiplier > 1
            ? TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, (long)(device.Cadence.Ticks / _options.ReportingRateMultiplier)))
            : device.Cadence;

    internal async Task TickDeviceAsync(SimulatedDevice device, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // TWO CHANNELS, deliberately kept apart. Telemetry is the data plane: it is what the simulated device
        // is "sending", and the Disconnect scenario switches it off. Polling the shadow is the harness's
        // control plane: it is how an operator tells the harness what the device should be doing, and it is
        // not something the simulated device is doing. It therefore keeps running when the data channel is
        // silent. If a Disconnect device also stopped polling, nothing would ever read the instruction to
        // bring it back, and a silent device could never be switched to Nominal again.
        if (device.NextShadowPoll <= now)
        {
            device.NextShadowPoll = now + _options.ShadowPollInterval;
            await ApplyDesiredStateAsync(device, now, cancellationToken);
        }

        await FlushAcknowledgementsAsync(device, cancellationToken);

        if (device.Scenario == SimulationScenario.Disconnect || device.NextDue > now)
        {
            return;
        }

        // Kept on its own cadence rather than reset to now, so a slow tick does not stretch the interval. A
        // device that has fallen more than an interval behind skips ahead instead of bursting to catch up.
        var cadence = LiveCadence(device);

        device.NextDue += cadence;

        if (device.NextDue <= now)
        {
            device.NextDue = now + cadence;
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
                    // A 404 for one device means the gateway restarted, and a restart forgets every device
                    // at once. Re-registering only this one would leave the silent ones (a Disconnect device
                    // never sends, so it never hears a 404) unregistered for good. So the first 404 the
                    // fleet sees re-registers the whole fleet, then this reading is sent again.
                    if (!await ReRegisterFleetAsync(device, cancellationToken))
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

    private readonly SemaphoreSlim _reRegistration = new(1, 1);
    private DateTimeOffset _lastFleetRegistration = DateTimeOffset.MinValue;

    /// <summary>Registers every device again after the gateway has forgotten them.</summary>
    /// <remarks>
    /// Forty devices sending in the same tick all get their 404 together, and forty full re-registrations
    /// would be forty times the work for one event. The first caller does it, holding the semaphore; the rest
    /// wait for it, see that a registration finished moments ago, and go straight on to resend.
    /// </remarks>
    private async Task<bool> ReRegisterFleetAsync(SimulatedDevice reporter, CancellationToken cancellationToken)
    {
        var asked = time.GetUtcNow();

        await _reRegistration.WaitAsync(cancellationToken);

        try
        {
            if (_lastFleetRegistration >= asked)
            {
                return true;
            }

            logger.LogWarning(
                "The gateway does not know {Mac}, which means it restarted. Registering all {Count} devices again.",
                reporter.MacAddress,
                _devices.Count);

            foreach (var device in _devices.ToArray())
            {
                if (!await gateway.RegisterAsync(device.Registration, cancellationToken))
                {
                    return false;
                }
            }

            _lastFleetRegistration = time.GetUtcNow();

            return true;
        }
        finally
        {
            _reRegistration.Release();
        }
    }

    /// <summary>Tells the gateway what came of the commands this device has applied.</summary>
    /// <remarks>
    /// An acknowledgement is kept until the gateway takes it, so a lost request or a brief outage does not
    /// leave a command "delivered" until it times out. A silent device sends nothing at all, acknowledgements
    /// included: it is switched off, and stays owing them until it is switched back on.
    /// </remarks>
    private async Task FlushAcknowledgementsAsync(SimulatedDevice device, CancellationToken cancellationToken)
    {
        if (device.PendingAcks.Count == 0 || device.Scenario == SimulationScenario.Disconnect)
        {
            return;
        }

        foreach (var (commandId, acknowledgement) in device.PendingAcks.ToArray())
        {
            var delivery = await gateway.AcknowledgeAsync(commandId, acknowledgement, cancellationToken);

            if (delivery != AckDelivery.Retry)
            {
                // Taken, or refused for good. Either way there is nothing left to send for this command.
                device.PendingAcks.Remove(commandId);
            }
        }
    }

    /// <summary>Applies whatever an operator has asked of this device since its last poll.</summary>
    private async Task ApplyDesiredStateAsync(SimulatedDevice device, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A device the harness has switched off checks its scenario but collects nothing: it is not there to
        // receive commands, and they must stay pending and time out like they would for hardware that is off.
        var desired = await gateway.GetDesiredStateAsync(
            device.MacAddress, cancellationToken, deliverCommands: device.Scenario != SimulationScenario.Disconnect);

        if (desired is not null)
        {
            foreach (var command in desired.CommandList)
            {
                var outcome = device.ApplyCommand(command, _options.CommandFailureRate, _random);

                logger.LogInformation(
                    "{Mac} {Result} {Action}{Repeat} (command {Command}).",
                    device.MacAddress,
                    outcome.Success ? "applied" : "could not apply",
                    command.Action,
                    outcome.WasRepeat ? ", a repeat it already had" : string.Empty,
                    command.Id);
            }
        }

        if (desired is null || desired.Scenario is null || desired.Version == device.AppliedVersion)
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
