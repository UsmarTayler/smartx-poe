using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// The kind of device a telemetry packet came from.
/// </summary>
/// <remarks>
/// Serialised as a string rather than as its underlying integer. These values cross the API
/// boundary, and a numeric wire format silently changes meaning if a member is ever inserted in
/// the middle of the enum — the string form survives that edit.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SensorCategory>))]
public enum SensorCategory
{
    /// <summary>Ambient conditions: temperature, humidity, air quality.</summary>
    Environmental,

    /// <summary>Power draw and metering. The category that aggregates across nodes.</summary>
    PowerConsumption,

    /// <summary>Switches, relays and valves — devices with an engaged/disengaged state.</summary>
    Actuator
}
