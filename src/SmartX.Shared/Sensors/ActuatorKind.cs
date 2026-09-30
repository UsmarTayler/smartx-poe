using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// What kind of actuator a device is, and so which commands it can be sent.
/// </summary>
/// <remarks>
/// Registration records this because a command has to be checked against the device it is aimed at: telling a
/// valve to switch on has no meaning, and the API should refuse it with a reason rather than queue something a
/// device can only fail. Serialised as a string for the same reason as the other enums that cross the wire.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ActuatorKind>))]
public enum ActuatorKind
{
    /// <summary>Moves fluid. On or off.</summary>
    Pump,

    /// <summary>A switched circuit: a contactor, a smart plug, a door strike. On or off.</summary>
    Relay,

    /// <summary>Controls flow. Open or closed.</summary>
    Valve,

    /// <summary>Holds a level from 0 to 100 rather than a position: a light, a fan speed.</summary>
    Dimmer
}
