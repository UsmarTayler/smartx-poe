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

    /// <summary>Stops reporting entirely.</summary>
    Disconnect
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
