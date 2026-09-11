using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// The registration form's editable state.
/// </summary>
/// <remarks>
/// Separate from <see cref="SensorRegistrationRequest"/> because a form and a request have different
/// shapes. The request is immutable and its range bounds are plain numbers; a form is edited field by
/// field and starts with empty boxes, so its bounds are nullable until typed. Binding a form straight
/// to the request type would force an empty box to mean zero.
/// </remarks>
public sealed class SensorRegistrationModel
{
    public string MacAddress { get; set; } = string.Empty;

    public string Room { get; set; } = string.Empty;

    public string? ZoneId { get; set; }

    public string NodeId { get; set; } = string.Empty;

    public SensorCategory Category { get; set; } = SensorCategory.Environmental;

    public string Unit { get; set; } = string.Empty;

    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    public TelemetryPayloadKind PayloadKind { get; set; } = TelemetryPayloadKind.Real;

    public int? HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>Builds the request the API expects.</summary>
    /// <remarks>
    /// An empty bound is sent as NaN rather than zero, so if validation were ever bypassed the server
    /// would reject it instead of accepting a range of 0 to 0.
    /// </remarks>
    public SensorRegistrationRequest ToRequest() => new(
        MacAddress.Trim(),
        Room.Trim(),
        ZoneId ?? string.Empty,
        NodeId.Trim(),
        Category,
        Unit.Trim(),
        Minimum ?? double.NaN,
        Maximum ?? double.NaN,
        PayloadKind,
        HeartbeatIntervalSeconds);

    /// <summary>Clears the device-specific fields and keeps the location.</summary>
    /// <remarks>
    /// Sensors are usually installed several to a room, so after one is registered the next most
    /// likely entry is a different device in the same place. Keeping room, zone, node and unit saves
    /// retyping four fields per sensor; the MAC is the one thing that must change.
    /// </remarks>
    public void ClearForNextSensor() => MacAddress = string.Empty;
}
