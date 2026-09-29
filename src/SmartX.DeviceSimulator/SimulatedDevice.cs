using System.Text.Json;
using SmartX.Shared;

namespace SmartX.DeviceSimulator;

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

    private double _position = 0.5;
    private bool _engaged;

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
    /// <remarks>Drift and Spike need a numeric range, and a state device does not have one.</remarks>
    public bool Supports(SimulationScenario scenario) =>
        Registration.PayloadKind != TelemetryPayloadKind.State
        || scenario is not (SimulationScenario.Drift or SimulationScenario.Spike or SimulationScenario.Critical);

    /// <summary>Applies a desired state fetched from the gateway.</summary>
    /// <returns><see langword="false"/> when the instruction is one this device cannot carry out.</returns>
    public bool TryApply(DeviceDesiredState desired, DateTimeOffset now)
    {
        AppliedVersion = desired.Version;

        if (!Supports(desired.Scenario))
        {
            return false;
        }

        if (Scenario == SimulationScenario.Disconnect && desired.Scenario != SimulationScenario.Disconnect)
        {
            // Reconnecting: report straight away rather than waiting out an interval from a schedule that
            // stopped minutes ago.
            NextDue = now;
        }

        Scenario = desired.Scenario;
        _position = 0.5;

        return true;
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

        if (Registration.PayloadKind == TelemetryPayloadKind.State)
        {
            // Actuators change state now and then rather than on every report.
            if (random.NextDouble() < 0.1)
            {
                _engaged = !_engaged;
            }

            return JsonSerializer.SerializeToElement(_engaged);
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

            default:
                _position = Math.Clamp(_position + Jitter(random, 0.03), NominalLow, NominalHigh);
                position = _position;
                break;
        }

        return Value(position);
    }

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
