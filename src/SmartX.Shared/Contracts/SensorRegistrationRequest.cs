using System.Diagnostics.CodeAnalysis;

namespace SmartX.Shared;

/// <summary>
/// The body of a POST to /api/sensors.
/// </summary>
/// <remarks>
/// Flat and made only of primitives, deliberately. <see cref="SensorRegistration"/> refuses to
/// exist in an invalid state, which is the right rule for a domain type but the wrong shape for a
/// request body: a malformed POST would throw inside the JSON deserialiser and surface as an
/// unhelpful 400 with no indication of which field was wrong. This type accepts anything, and
/// <see cref="TryCreateRegistration"/> reports every problem at once.
/// </remarks>
/// <param name="MacAddress">The device's MAC, in any accepted format.</param>
/// <param name="Room">Room or bay name.</param>
/// <param name="ZoneId">The zone to register into.</param>
/// <param name="NodeId">The mesh node the sensor reports through.</param>
/// <param name="Category">What kind of device it is.</param>
/// <param name="Unit">The unit it reports in.</param>
/// <param name="Minimum">Lowest value it is expected to report.</param>
/// <param name="Maximum">Highest value it is expected to report.</param>
/// <param name="PayloadKind">The shape of reading it emits.</param>
/// <param name="HeartbeatIntervalSeconds">
/// How often, in seconds, the device is expected to report. Omit to use the gateway default.
/// Whole seconds rather than a TimeSpan string, because "60" is what a device author writes and
/// "00:01:00" is not.
/// </param>
public sealed record SensorRegistrationRequest(
    string MacAddress,
    string Room,
    string ZoneId,
    string NodeId,
    SensorCategory Category,
    string Unit,
    double Minimum,
    double Maximum,
    TelemetryPayloadKind PayloadKind,
    int? HeartbeatIntervalSeconds = null)
{
    /// <summary>
    /// Turns the request into a registration, or reports everything wrong with it.
    /// </summary>
    /// <param name="registration">The registration, when the request is sound.</param>
    /// <param name="errors">
    /// Field name to messages, in the shape ASP.NET Core's problem-details helper expects.
    /// </param>
    /// <remarks>
    /// Collects every field's problems rather than stopping at the first, for the same reason the
    /// zone walk does: a caller fixing one field at a time learns about the next only by trying
    /// again.
    /// </remarks>
    public bool TryCreateRegistration(
        [NotNullWhen(true)] out SensorRegistration? registration,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        registration = null;

        if (!MacAddressValidator.IsValid(MacAddress))
        {
            errors[nameof(MacAddress)] =
                ["Expected six hex pairs separated consistently by colons or hyphens, " +
                 "and not the reserved all-zero address."];
        }

        if (string.IsNullOrWhiteSpace(Room))
        {
            errors[nameof(Room)] = ["A room is required."];
        }

        if (string.IsNullOrWhiteSpace(ZoneId))
        {
            errors[nameof(ZoneId)] = ["A zone id is required."];
        }

        if (string.IsNullOrWhiteSpace(NodeId))
        {
            errors[nameof(NodeId)] = ["A node id is required."];
        }

        if (string.IsNullOrWhiteSpace(Unit))
        {
            errors[nameof(Unit)] = ["A unit is required."];
        }

        if (double.IsNaN(Minimum) || double.IsNaN(Maximum))
        {
            errors[nameof(Minimum)] = ["Range bounds cannot be NaN."];
        }
        else if (Minimum > Maximum)
        {
            errors[nameof(Minimum)] = [$"Minimum {Minimum} exceeds maximum {Maximum}."];
        }

        if (PayloadKind == TelemetryPayloadKind.State && Category != SensorCategory.Actuator)
        {
            errors[nameof(PayloadKind)] =
                [$"A {nameof(TelemetryPayloadKind.State)} sensor must be registered as " +
                 $"{nameof(SensorCategory.Actuator)}, not {Category}."];
        }

        if (HeartbeatIntervalSeconds is <= 0)
        {
            errors[nameof(HeartbeatIntervalSeconds)] =
                ["A heartbeat interval must be a positive number of seconds."];
        }

        if (errors.Count > 0)
        {
            return false;
        }

        registration = new SensorRegistration(
            MacAddress,
            new DeploymentLocation(Room, ZoneId, NodeId),
            Category,
            Unit,
            new ValueRange(Minimum, Maximum),
            PayloadKind,
            HeartbeatIntervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);

        return true;
    }
}
