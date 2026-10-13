using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// How a simulated sensor behaves.
/// </summary>
/// <remarks>
/// Each scenario maps onto one trust signal, so switching a node between them on the dashboard shows
/// that signal in isolation: range violations, schema failures or silence.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SimulationScenario>))]
public enum SimulationScenario
{
    /// <summary>In-range readings at the registered interval.</summary>
    Nominal,

    /// <summary>Readings walking slowly toward the top of the range, then hovering across it.</summary>
    Drift,

    /// <summary>Mostly normal readings with occasional out-of-range values.</summary>
    Spike,

    /// <summary>Mostly normal readings with occasional payloads of the wrong type.</summary>
    TypeViolation,

    /// <summary>Extreme but still valid readings: a power spike or a moisture crash.</summary>
    /// <remarks>
    /// The device does not decide that a reading is an alert. It sends a real value near the edge of its
    /// registered range, and the API classifies it against its configured critical thresholds.
    /// </remarks>
    Critical,

    /// <summary>Stops reporting entirely.</summary>
    Disconnect,

    /// <summary>
    /// A cycle of drying out slowly and recovering, forever: the moisture in a grow bed between waterings.
    /// </summary>
    /// <remarks>
    /// Every reading is inside the registered range, so nothing is refused and the node stays trusted. What it does is fall
    /// through the sensor's low watch level once a cycle, which is the trigger an operator responds to by running a pump.
    /// </remarks>
    DryOut,

    /// <summary>
    /// A probe that is slowly fouling: more and more of its readings come back wrong, until it is cleaned and starts again.
    /// </summary>
    /// <remarks>
    /// Unlike a sensor that fails all at once, its trust falls steadily over several minutes before it reaches Fault, which
    /// is the shape a forecast can see coming. The wrong readings are out of the registered range, so the gateway refuses
    /// them and the trust engine counts them, as it would for hardware that was failing.
    /// </remarks>
    Fouling
}

/// <summary>The body of a POST to /api/simulation/{mac}/scenario.</summary>
public sealed record ScenarioChangeRequest(SimulationScenario Scenario);

/// <summary>One simulated sensor and what it is currently doing.</summary>
/// <param name="MacAddress">The sensor.</param>
/// <param name="Scenario">Its current behaviour.</param>
/// <param name="PayloadKind">What it sends, which decides the scenarios it can run.</param>
/// <param name="HeartbeatInterval">How often it reports while not disconnected.</param>
public sealed record SimulatedNodeStatus(
    string MacAddress,
    SimulationScenario Scenario,
    TelemetryPayloadKind PayloadKind,
    TimeSpan HeartbeatInterval);
