using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// What a device search asked for. Every member is optional and they combine with AND.
/// </summary>
/// <param name="Q">Text to find in the MAC address, the room or the node id.</param>
/// <param name="Category">Environmental, power or actuator.</param>
/// <param name="ActuatorKind">Pump, relay, valve or dimmer.</param>
/// <param name="Zone">A zone id. Matches devices in that zone and every zone beneath it.</param>
/// <param name="Band">The trust band the device is in now.</param>
/// <param name="Alert">A kind of alert the device has active now.</param>
public sealed record DeviceSearchFilters(
    string? Q = null,
    SensorCategory? Category = null,
    ActuatorKind? ActuatorKind = null,
    string? Zone = null,
    TrustBand? Band = null,
    AlertKind? Alert = null)
{
    /// <summary>True when nothing was asked for, so every device matches.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Q)
        && Category is null
        && ActuatorKind is null
        && string.IsNullOrWhiteSpace(Zone)
        && Band is null
        && Alert is null;
}

/// <summary>One device in a search result.</summary>
/// <param name="MacAddress">The device.</param>
/// <param name="Room">The room it was registered in. In the demo fleet that is the device's own label.</param>
/// <param name="NodeId">The node, or gateway, it reports through.</param>
/// <param name="ZoneId">The zone it is deployed in.</param>
/// <param name="Category">What sort of device it is.</param>
/// <param name="ActuatorKind">Which sort of actuator, for an actuator.</param>
/// <param name="Band">Its trust band now.</param>
/// <param name="ActiveAlerts">The kinds of alert it has active now.</param>
public sealed record DeviceSearchHit(
    string MacAddress,
    string Room,
    string NodeId,
    string ZoneId,
    SensorCategory Category,
    ActuatorKind? ActuatorKind,
    TrustBand Band,
    IReadOnlyList<AlertKind> ActiveAlerts);

/// <summary>The result of a device search.</summary>
/// <param name="Filters">What was asked for, echoed back.</param>
/// <param name="Count">How many devices matched.</param>
/// <param name="Devices">The matches, ordered by MAC address.</param>
public sealed record DeviceSearchResponse(
    DeviceSearchFilters Filters,
    int Count,
    IReadOnlyList<DeviceSearchHit> Devices);

/// <summary>What sort of thing an operator did.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActivityKind>))]
public enum ActivityKind
{
    /// <summary>A device search.</summary>
    Search,

    /// <summary>An action on a command: issuing it, cancelling it or undoing it.</summary>
    Command,

    /// <summary>Opening a device in the Command Stream.</summary>
    Selection
}

/// <summary>
/// One thing an operator did, with the state of the mesh when they did it.
/// </summary>
/// <param name="Sequence">The arrival number, which orders actions that share a timestamp.</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Kind">A search or a command.</param>
/// <param name="Summary">A short phrase describing it.</param>
/// <param name="Filters">For a search, what was asked for.</param>
/// <param name="ResultCount">For a search, how many devices matched.</param>
/// <param name="MacAddress">For a command, the actuator.</param>
/// <param name="CommandId">For a command, which one.</param>
/// <param name="ActiveAlerts">
/// Which alerts were active at that moment. This is the context the pattern analysis learns from: a search for
/// pumps made while a pump is disconnected means something different from the same search on a quiet day.
/// </param>
public sealed record ActivityEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    ActivityKind Kind,
    string Summary,
    DeviceSearchFilters? Filters,
    int? ResultCount,
    string? MacAddress,
    Guid? CommandId,
    IReadOnlyList<AlertKey> ActiveAlerts);
