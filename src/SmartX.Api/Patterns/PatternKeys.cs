using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>
/// What kind of trigger something was, and which one, at one level of generality.
/// </summary>
/// <remarks>
/// A <c>readonly record struct</c>, for the reason <see cref="AlertKey"/> is: these are dictionary keys that are looked up on
/// every event, and a value type with value equality needs no allocation and no <c>object</c> to be a key. The compiler
/// writes <see cref="IEquatable{T}"/> for a record struct, so <see cref="EqualityComparer{T}.Default"/> compares the fields
/// directly and never boxes.
/// </remarks>
/// <param name="Level">Whether this is about one device or a kind of device in a zone.</param>
/// <param name="Kind">The sort of trigger.</param>
/// <param name="Scope">A MAC address at the specific level, or <c>class@zone</c> at the general one.</param>
/// <param name="Detail">Which one: a side of a watch level, an alert kind, a band, or a critical reason.</param>
internal readonly record struct TriggerKey(PatternLevel Level, TriggerKind Kind, string Scope, string Detail);

/// <summary>What an operator did, at one level of generality. A value-equality key for the same reason as <see cref="TriggerKey"/>.</summary>
/// <param name="Level">Whether this is about one device or a kind of device.</param>
/// <param name="Kind">Viewed, searched, command or verdict.</param>
/// <param name="Target">A MAC address at the specific level, or a device class at the general one.</param>
/// <param name="Command">The command's action, or the verdict, or empty.</param>
internal readonly record struct ActionKey(PatternLevel Level, OperatorActionKind Kind, string Target, string Command);

/// <summary>A trigger as the system reports it, before it has been given its keys.</summary>
/// <param name="Kind">The sort of trigger.</param>
/// <param name="MacAddress">The device it happened to.</param>
/// <param name="Detail">Which one.</param>
/// <param name="At">When it happened.</param>
internal readonly record struct TriggerEvent(TriggerKind Kind, string MacAddress, string Detail, DateTimeOffset At);

/// <summary>An operator action as the system reports it, before it has been given its keys.</summary>
/// <param name="Kind">Viewed, searched, command or verdict.</param>
/// <param name="MacAddress">The device it was about.</param>
/// <param name="Command">The command's action, or the verdict, or empty.</param>
/// <param name="At">When it happened.</param>
internal readonly record struct OperatorAction(OperatorActionKind Kind, string MacAddress, string Command, DateTimeOffset At);

/// <summary>What the engine needs to know about a device to learn about it at both levels.</summary>
/// <param name="MacAddress">The device.</param>
/// <param name="Room">Where it is, as registered.</param>
/// <param name="ZoneId">The zone it is in.</param>
/// <param name="GeneralZoneId">The zone it is grouped under at the general level, found by walking up the zone tree.</param>
/// <param name="GeneralZoneName">That zone's name.</param>
/// <param name="Class">The kind of device: a unit for a sensor, the actuator kind for an actuator.</param>
/// <param name="ClassLabel">The class in words.</param>
/// <param name="IsState">Whether it reports an on or off state and not a measurement.</param>
/// <param name="RangeMinimum">The bottom of its registered range.</param>
/// <param name="RangeMaximum">The top of its registered range.</param>
internal sealed record DeviceProfile(
    string MacAddress,
    string Room,
    string ZoneId,
    string GeneralZoneId,
    string GeneralZoneName,
    string Class,
    string ClassLabel,
    bool IsState,
    double RangeMinimum,
    double RangeMaximum);

/// <summary>Looks devices up for the pattern engine, so the engine does not depend on how they are stored.</summary>
internal interface IDeviceDirectory
{
    /// <summary>Describes a device.</summary>
    /// <returns><see langword="false"/> when no sensor carries that address.</returns>
    bool TryDescribe(string macAddress, out DeviceProfile profile);
}
