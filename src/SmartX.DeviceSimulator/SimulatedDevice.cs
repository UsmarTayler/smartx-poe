using System.Text.Json;
using SmartX.Shared;

namespace SmartX.DeviceSimulator;

/// <summary>What a device did with a command.</summary>
/// <param name="Success">Whether it applied it.</param>
/// <param name="ResultingValue">The actuator's state afterwards.</param>
/// <param name="Reason">Why it could not, when it could not.</param>
/// <param name="WasRepeat">True when the command had been applied before and was not applied again.</param>
internal sealed record CommandOutcome(bool Success, double ResultingValue, string? Reason, bool WasRepeat);

/// <summary>
/// One simulated ESP32: what it is registered as, how it is behaving, and when it next reports.
/// </summary>
/// <remarks>
/// Not thread-safe. <see cref="DeviceFleet"/> only touches a device, and the shared <see cref="Random"/>,
/// while holding its lock.
/// <para>
/// A reading is produced as a <see cref="JsonElement"/> because that is what goes on the wire. The device
/// makes no judgement about whether a value is acceptable: it reports what it "measured" and the gateway
/// decides, which is the whole point of validating at the edge rather than trusting the device.
/// </para>
/// </remarks>
internal sealed class SimulatedDevice(DemoDevice definition)
{
    // The value is tracked as a position within the registered range — 0 at the minimum, 1 at the maximum —
    // so one set of rules drives a freezer at -28 °C and a feeder at 200 kW alike.
    private const double NominalLow = 0.3;
    private const double NominalHigh = 0.7;

    // Per reading. From mid-range, a drifting sensor crosses its maximum after about 35 readings: roughly
    // three minutes at a five-second heartbeat, which is slow enough to watch happen.
    private const double DriftStep = 0.015;

    private const double SpikeChance = 0.35;
    private const double TypeViolationChance = 0.25;

    // Critical readings are occasional by nature: a compressor surges, it does not sit in surge. Frequent
    // enough to see on the pipeline stats within a few seconds, rare enough that the critical lane stays
    // shallow and its wait time stays honest.
    private const double CriticalChance = 0.15;

    // The dry-out scenario is a cycle of this many readings: fall for most of it, recover for the rest. Eighty readings at the
    // moisture sensor's four-second cadence is a little over five minutes, so the sensor crosses its low watch level about
    // twice in a ten-minute pattern window, which is often enough to watch and rare enough to mean something.
    private const int DryOutCycle = 80;
    private const int DryOutFall = 60;
    private const double DryOutTop = 0.55;
    private const double DryOutBottom = 0.08;

    // Where in the cycle the sensor starts. Thirty readings in it is at 31 %, so the first crossing of 20 % comes about a minute
    // after the sensor starts, and a freshly started stack has a live trigger to show without anyone waiting for it.
    private int _cycle = 30;

    private double _position = 0.5;

    // An actuator's state, in the terms commands use: 1 for on or open, 0 for off or closed, a dimmer's level.
    // It only changes when the device applies a command. A pump does not decide to switch itself on.
    private double _actuatorValue = InitialActuatorValue(definition.Registration.ActuatorKind);

    // The ids of commands this device has already applied, with what came of them. A device can be handed
    // the same command twice, and the standard answer is idempotency: applying it again would run a pump twice
    // or toggle a relay back, so a repeat gets the first answer and changes nothing. Bounded, oldest first
    // out, because a device that has been running for a year cannot remember every id it ever saw.
    private const int RememberedCommands = 512;
    private readonly Dictionary<Guid, CommandOutcome> _applied = [];
    private readonly Queue<Guid> _appliedOrder = new();

    /// <summary>Acknowledgements the device owes the gateway, until the gateway has taken them.</summary>
    public Dictionary<Guid, CommandAckRequest> PendingAcks { get; } = [];

    public SensorRegistrationRequest Registration { get; } = definition.Registration;

    public string MacAddress => Registration.MacAddress;

    public SimulationScenario Scenario { get; private set; } = definition.Scenario;

    /// <summary>The desired-state version this device has already applied.</summary>
    public long AppliedVersion { get; private set; }

    /// <summary>When this device next reports.</summary>
    public DateTimeOffset NextDue { get; set; }

    /// <summary>When this device next asks the gateway what it should be doing.</summary>
    public DateTimeOffset NextShadowPoll { get; set; }

    public TimeSpan Interval => TimeSpan.FromSeconds(Registration.HeartbeatIntervalSeconds ?? 60);

    /// <summary>How often the device actually reports: a little inside its heartbeat interval.</summary>
    /// <remarks>
    /// The registered interval is a deadline, not a schedule. The gateway counts a missed heartbeat the
    /// moment a whole interval passes in silence, so a device configured for five seconds has to report
    /// within five seconds, and a reading in flight must not be what makes it late.
    /// </remarks>
    public TimeSpan Cadence => Interval * 0.8;

    /// <summary>Whether this device can run a scenario at all.</summary>
    /// <remarks>
    /// Drift, Spike and Critical are about a measurement leaving its range. An actuator does not measure
    /// anything: its state is whatever it was last commanded to, so there is nothing to drift.
    /// </remarks>
    public bool Supports(SimulationScenario scenario) =>
        Registration.Category != SensorCategory.Actuator
        || scenario is SimulationScenario.Nominal or SimulationScenario.TypeViolation or SimulationScenario.Disconnect;

    /// <summary>Applies a desired state fetched from the gateway.</summary>
    /// <returns><see langword="false"/> when the instruction is one this device cannot carry out.</returns>
    public bool TryApply(DeviceDesiredState desired, DateTimeOffset now)
    {
        // Commands can arrive with no scenario ever having been set. There is nothing to apply then.
        if (desired.Scenario is not { } wanted)
        {
            return true;
        }

        AppliedVersion = desired.Version;

        if (!Supports(wanted))
        {
            return false;
        }

        if (Scenario == SimulationScenario.Disconnect && wanted != SimulationScenario.Disconnect)
        {
            // Reconnecting: report straight away rather than waiting out an interval from a schedule that
            // stopped minutes ago.
            NextDue = now;
        }

        Scenario = wanted;
        _position = 0.5;

        return true;
    }

    /// <summary>What an actuator is doing before anyone has commanded it.</summary>
    private static double InitialActuatorValue(ActuatorKind? kind) => kind switch
    {
        ActuatorKind.Valve => 1,
        ActuatorKind.Dimmer => 50,
        _ => 0,
    };

    /// <summary>The actuator's current state, for tests and for the fleet's own logging.</summary>
    public double ActuatorValue => _actuatorValue;

    /// <summary>
    /// Applies a command fetched from the gateway, once.
    /// </summary>
    /// <remarks>
    /// <b>At-least-once delivery, made safe by idempotency.</b> The gateway hands each command over once, but
    /// networks repeat things and a real gateway might too. A command whose id has been applied before is not
    /// applied again: the device returns what it said the first time, flagged as a repeat, so the gateway
    /// still gets its acknowledgement if the first was lost.
    /// </remarks>
    public CommandOutcome ApplyCommand(Command command, double failureRate, Random random)
    {
        if (_applied.TryGetValue(command.Id, out var earlier))
        {
            return earlier with { WasRepeat = true };
        }

        CommandOutcome outcome;

        if (Registration.ActuatorKind != command.Kind)
        {
            outcome = new CommandOutcome(false, _actuatorValue, $"This device is a {Registration.ActuatorKind}, and cannot do {command.Action}.", false);
        }
        else if (failureRate > 0 && random.NextDouble() < failureRate)
        {
            // A valve that will not turn, a relay that does not click. Reported honestly, because a device
            // that cannot do what it was told says so.
            outcome = new CommandOutcome(false, _actuatorValue, "The actuator did not respond.", false);
        }
        else
        {
            _actuatorValue = command.TargetValue;
            outcome = new CommandOutcome(true, _actuatorValue, null, false);
        }

        Remember(command.Id, outcome);
        PendingAcks[command.Id] = new CommandAckRequest(outcome.Success, outcome.ResultingValue, outcome.Reason);

        return outcome;
    }

    private void Remember(Guid id, CommandOutcome outcome)
    {
        _applied[id] = outcome;
        _appliedOrder.Enqueue(id);

        while (_appliedOrder.Count > RememberedCommands)
        {
            _applied.Remove(_appliedOrder.Dequeue());
        }
    }

    /// <summary>Sets the scenario locally, for the fleet's own start-up overrides.</summary>
    public void StartIn(SimulationScenario scenario) => Scenario = Supports(scenario) ? scenario : Scenario;

    /// <summary>Produces the next reading for a scenario.</summary>
    /// <param name="scenario">
    /// Passed in rather than read from <see cref="Scenario"/>, so the backfill can replay a device as
    /// nominal up to the moment its scenario began.
    /// </param>
    /// <param name="random">The fleet's shared generator.</param>
    public JsonElement NextReading(SimulationScenario scenario, Random random)
    {
        if (scenario == SimulationScenario.TypeViolation && random.NextDouble() < TypeViolationChance)
        {
            return WrongType(random);
        }

        // An actuator reports the state it is in, which only a command changes. Its next report after a
        // command is how the gateway learns the command took effect.
        if (Registration.Category == SensorCategory.Actuator)
        {
            return Registration.PayloadKind == TelemetryPayloadKind.State
                ? JsonSerializer.SerializeToElement(_actuatorValue >= 0.5)
                : Registration.PayloadKind == TelemetryPayloadKind.Integer
                    ? JsonSerializer.SerializeToElement((int)Math.Round(_actuatorValue))
                    : JsonSerializer.SerializeToElement((float)_actuatorValue);
        }

        double position;

        switch (scenario)
        {
            case SimulationScenario.Drift:
                // Walks up to the boundary, then hovers just across it with some readings still inside,
                // the way a failing sensor wanders rather than jumping to a fixed wrong value.
                _position = Math.Min(_position + DriftStep, 1.04);
                position = _position + Jitter(random, 0.05);
                break;

            case SimulationScenario.Spike when random.NextDouble() < SpikeChance:
                // Well clear of the range, above or below, so the reading is unambiguously out and the
                // gateway rejects it.
                var overshoot = 0.15 + random.NextDouble() * 0.5;
                position = random.NextDouble() < 0.5 ? 1 + overshoot : -overshoot;
                break;

            case SimulationScenario.Critical when random.NextDouble() < CriticalChance:
                // Extreme but still inside the registered range, so it passes validation and is a real
                // reading the gateway must act on rather than refuse. Power and air quality go high;
                // moisture crashes low. The gateway, not this device, decides that the value is an alert.
                position = CrashesLow ? 0.02 + random.NextDouble() * 0.02 : 0.96 + random.NextDouble() * 0.03;
                break;

            case SimulationScenario.DryOut:
                // A fixed cycle and a little jitter: it is a bed drying out between waterings, not a fault, so every
                // reading stays inside the range and the node stays trusted.
                _cycle = (_cycle + 1) % DryOutCycle;
                position = DryOutPosition(_cycle) + Jitter(random, 0.01);
                break;

            default:
                _position = Math.Clamp(_position + Jitter(random, 0.03), NominalLow, NominalHigh);
                position = _position;
                break;
        }

        return Value(position);
    }

    /// <summary>Where a dry-out cycle is at a reading: a straight fall to the bottom, then a straight recovery to the top.</summary>
    private static double DryOutPosition(int index) =>
        index <= DryOutFall
            ? DryOutTop - (DryOutTop - DryOutBottom) * index / DryOutFall
            : DryOutBottom + (DryOutTop - DryOutBottom) * (index - DryOutFall) / (DryOutCycle - DryOutFall);

    /// <summary>Which end of the range is the dangerous one for this kind of device.</summary>
    private bool CrashesLow => Registration.Unit == "%RH";

    /// <summary>Turns a position in the range into a JSON number of the registered payload kind.</summary>
    /// <remarks>
    /// Both branches call the generic <see cref="JsonSerializer.SerializeToElement{TValue}"/> with a concrete
    /// numeric type, so the value goes to JSON without passing through <c>object</c>.
    /// </remarks>
    private JsonElement Value(double position)
    {
        var value = Registration.Minimum + position * (Registration.Maximum - Registration.Minimum);

        return Registration.PayloadKind == TelemetryPayloadKind.Integer
            ? JsonSerializer.SerializeToElement((int)Math.Round(value))
            : JsonSerializer.SerializeToElement(MathF.Round((float)value, 2));
    }

    /// <summary>A payload the registration does not allow: what a firmware or configuration mismatch sends.</summary>
    private JsonElement WrongType(Random random) => Registration.PayloadKind switch
    {
        // A switch reporting 1 for "on" instead of true.
        TelemetryPayloadKind.State => JsonSerializer.SerializeToElement(1),

        // An integer meter reporting a fractional value, or an error string in place of a number.
        TelemetryPayloadKind.Integer when random.NextDouble() < 0.5 => JsonSerializer.SerializeToElement(21.5),
        _ => JsonSerializer.SerializeToElement("ERR"),
    };

    private static double Jitter(Random random, double amplitude) => (random.NextDouble() * 2 - 1) * amplitude;
}
