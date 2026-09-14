using System.Text.Json;
using SmartX.Shared;

namespace SmartX.Api.Simulation;

/// <summary>
/// One simulated sensor: its scenario, where its value currently sits, and when it next reports.
/// </summary>
/// <remarks>
/// Not thread-safe. <see cref="MeshSimulator"/> only touches a node, and the shared <see cref="Random"/>,
/// while holding its lock.
/// <para>
/// A reading is produced as a <see cref="JsonElement"/>, the same type an HTTP request delivers, because it
/// is then judged by <c>TelemetryEndpoints.TryCreatePacket</c> exactly as a device's reading would be. That
/// is what makes a simulated fault honest: a type violation here is a wrong-typed JSON value that the real
/// ingestion rules reject, not a flag that skips straight to a trust penalty.
/// </para>
/// </remarks>
internal sealed class SimulatedNode(SensorRegistration sensor, SimulationScenario scenario)
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

    private double _position = 0.5;
    private bool _engaged;

    public SensorRegistration Sensor { get; } = sensor;

    public SimulationScenario Scenario { get; private set; } = scenario;

    /// <summary>When the node next reports.</summary>
    public DateTimeOffset NextDue { get; set; }

    public TimeSpan Interval => Sensor.HeartbeatInterval ?? TimeSpan.FromMinutes(1);

    /// <summary>How often the node actually reports: a little inside its heartbeat interval.</summary>
    /// <remarks>
    /// The registered interval is a deadline, not a schedule. The heartbeat monitor counts a missed heartbeat
    /// the moment a whole interval passes in silence, so a device configured for five seconds has to report
    /// within five seconds. Reporting exactly on the interval would leave every healthy node one late
    /// simulator tick away from a false penalty — which is what the first version of this did.
    /// </remarks>
    public TimeSpan Cadence => Interval * 0.8;

    /// <summary>Can this sensor run a scenario? Drift and Spike need a range, which a State sensor lacks.</summary>
    public static bool Supports(SensorRegistration sensor, SimulationScenario scenario) =>
        sensor.PayloadKind != TelemetryPayloadKind.State
        || scenario is not (SimulationScenario.Drift or SimulationScenario.Spike);

    /// <summary>Switches scenario, starting the new one from a normal value.</summary>
    public void ChangeScenario(SimulationScenario scenario, DateTimeOffset now)
    {
        if (Scenario == SimulationScenario.Disconnect && scenario != SimulationScenario.Disconnect)
        {
            // Reconnecting: report straight away rather than waiting out an interval from a schedule that
            // stopped minutes ago.
            NextDue = now;
        }

        Scenario = scenario;
        _position = 0.5;
    }

    public SimulatedNodeStatus ToStatus() =>
        new(Sensor.MacAddress, Scenario, Sensor.PayloadKind, Interval);

    /// <summary>Produces the next reading for a scenario.</summary>
    /// <param name="scenario">
    /// Passed in rather than read from <see cref="Scenario"/>, so the backfill can replay a node as nominal
    /// up to the moment its scenario began.
    /// </param>
    /// <param name="random">The simulator's shared generator.</param>
    public JsonElement NextReading(SimulationScenario scenario, Random random)
    {
        if (scenario == SimulationScenario.TypeViolation && random.NextDouble() < TypeViolationChance)
        {
            return WrongType(random);
        }

        if (Sensor.PayloadKind == TelemetryPayloadKind.State)
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
                // Well clear of the range, above or below, so the reading is unambiguously out.
                var overshoot = 0.15 + random.NextDouble() * 0.5;
                position = random.NextDouble() < 0.5 ? 1 + overshoot : -overshoot;
                break;

            default:
                _position = Math.Clamp(_position + Jitter(random, 0.03), NominalLow, NominalHigh);
                position = _position;
                break;
        }

        return Value(position);
    }

    /// <summary>Turns a position in the range into a JSON number of the registered payload kind.</summary>
    /// <remarks>
    /// Both branches call the generic <see cref="JsonSerializer.SerializeToElement{TValue}"/> with a concrete
    /// numeric type, so the value goes to JSON without passing through <c>object</c>.
    /// </remarks>
    private JsonElement Value(double position)
    {
        var range = Sensor.ExpectedRange;
        var value = range.Minimum + position * (range.Maximum - range.Minimum);

        return Sensor.PayloadKind == TelemetryPayloadKind.Integer
            ? JsonSerializer.SerializeToElement((int)Math.Round(value))
            : JsonSerializer.SerializeToElement(MathF.Round((float)value, 2));
    }

    /// <summary>A payload the registration does not allow: what a firmware or configuration mismatch sends.</summary>
    private JsonElement WrongType(Random random) => Sensor.PayloadKind switch
    {
        // A switch reporting 1 for "on" instead of true.
        TelemetryPayloadKind.State => JsonSerializer.SerializeToElement(1),

        // An integer meter reporting a fractional value, or an error string in place of a number.
        TelemetryPayloadKind.Integer when random.NextDouble() < 0.5 => JsonSerializer.SerializeToElement(21.5),
        _ => JsonSerializer.SerializeToElement("ERR")
    };

    private static double Jitter(Random random, double amplitude) => (random.NextDouble() * 2 - 1) * amplitude;
}
