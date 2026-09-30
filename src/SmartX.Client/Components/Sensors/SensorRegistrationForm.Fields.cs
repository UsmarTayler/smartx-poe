using SmartX.Shared;

namespace SmartX.Client.Components.Sensors;

/// <summary>
/// Per-field validation and change handling for the registration form.
/// </summary>
/// <remarks>
/// Every validator starts with the matching <see cref="SensorRegistrationRules"/> method and only
/// falls back to the server's message once the local rule is satisfied. The local rule wins because it
/// describes the value on screen now; the server's message describes the value that was submitted.
/// Every change handler clears that field's server message for the same reason.
/// </remarks>
public partial class SensorRegistrationForm
{
    private string? ValidateMac(string value) =>
        SensorRegistrationRules.MacAddress(value) ?? ServerError(MacField);

    private string? ValidateNodeId(string value) =>
        SensorRegistrationRules.NodeId(value) ?? ServerError(NodeIdField);

    private string? ValidateRoom(string value) =>
        SensorRegistrationRules.Room(value) ?? ServerError(RoomField);

    private string? ValidateUnit(string value) =>
        SensorRegistrationRules.Unit(value) ?? ServerError(UnitField);

    // The form insists on a kind whenever Actuator is chosen (required: true). The API does not, so that
    // Part 1 registrations without one keep working; the form is where new registrations get it right.
    private string? ValidateActuatorKind(ActuatorKind? kind) =>
        SensorRegistrationRules.ActuatorKind(kind, Model.Category, Model.PayloadKind, required: true)
        ?? ServerError(ActuatorKindField);

    private string? ValidatePayload(TelemetryPayloadKind kind) =>
        SensorRegistrationRules.PayloadKind(kind, Model.Category) ?? ServerError(PayloadField);

    private string? ValidateHeartbeat(int? seconds) =>
        SensorRegistrationRules.HeartbeatIntervalSeconds(seconds) ?? ServerError(HeartbeatField);

    /// <summary>A zone must be chosen; beyond that, the picker summarises the server's violations.</summary>
    /// <remarks>
    /// The individual violations are listed in full beneath the picker. This one-line summary is what
    /// puts the picker itself into its error state, so the fault is visible on the field even when the
    /// list is out of view.
    /// </remarks>
    private string? ValidateZone(string? zoneId) =>
        SensorRegistrationRules.ZoneId(zoneId)
        ?? ZoneViolations.Count switch
        {
            0 => null,
            1 => "This zone's rules reject the sensor. See below.",
            var count => $"{count} zone rules reject the sensor. See below."
        };

    /// <summary>A minimum must be given, and must not exceed the maximum.</summary>
    /// <remarks>
    /// The ordering error sits on the minimum rather than the maximum, because that is the field the
    /// API attaches it to. Putting it anywhere else would make the as-you-type message and the
    /// server's message appear under different boxes for the same mistake.
    /// </remarks>
    private string? ValidateMinimum(double? minimum) =>
        SensorRegistrationRules.RangeBound(minimum, "minimum")
        ?? (minimum is { } low && Model.Maximum is { } high ? SensorRegistrationRules.Range(low, high) : null)
        ?? ServerError(MinimumField);

    private static string? ValidateMaximum(double? maximum) =>
        SensorRegistrationRules.RangeBound(maximum, "maximum");

    private void OnMacChanged(string value)
    {
        Model.MacAddress = value;
        _serverErrors.Remove(MacField);
    }

    private void OnTextChanged(string value, Action<string> apply, string field)
    {
        apply(value);
        _serverErrors.Remove(field);
    }

    private void OnZoneChanged(string? zoneId)
    {
        Model.ZoneId = zoneId;
        _serverErrors.Remove(ZoneField);
    }

    private async Task OnCategoryChanged(SensorCategory category)
    {
        Model.Category = category;

        // Whether the payload kind is allowed depends on the category, so it is re-checked now rather
        // than waiting for someone to touch the payload field again.
        if (_payloadField is not null)
        {
            await _payloadField.ValidateAsync();
        }

        // Picking Actuator brings the kind field into view, and its rule depends on the payload chosen.
        if (_actuatorKindField is not null)
        {
            await _actuatorKindField.ValidateAsync();
        }
    }

    private async Task OnActuatorKindChanged(ActuatorKind? kind)
    {
        Model.ActuatorKind = kind;
        _serverErrors.Remove(ActuatorKindField);

        // A Dimmer wants a numeric payload and everything else wants State, so the payload is re-checked.
        if (_payloadField is not null)
        {
            await _payloadField.ValidateAsync();
        }
    }

    private async Task OnPayloadChanged(TelemetryPayloadKind kind)
    {
        Model.PayloadKind = kind;
        _serverErrors.Remove(PayloadField);

        if (_actuatorKindField is not null)
        {
            await _actuatorKindField.ValidateAsync();
        }
    }

    private void OnMinimumChanged(double? value)
    {
        Model.Minimum = value;
        _serverErrors.Remove(MinimumField);
    }

    private async Task OnMaximumChanged(double? value)
    {
        Model.Maximum = value;

        // The minimum's error depends on the maximum, so changing one re-checks the other.
        if (_minimumField is not null)
        {
            await _minimumField.ValidateAsync();
        }
    }

    private void OnHeartbeatChanged(int? value)
    {
        Model.HeartbeatIntervalSeconds = value;
        _serverErrors.Remove(HeartbeatField);
    }
}
