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
/// <param name="ActuatorKind">
/// What kind of actuator this is, which decides the commands it can be sent. Optional, and ignored unless the
/// category is <see cref="SensorCategory.Actuator"/>. Part 1 requests never carried it and must keep working, so
/// its absence is accepted; an actuator registered without one is simply not commandable, and a command aimed at
/// it is refused with that reason.
/// </param>
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
    int? HeartbeatIntervalSeconds = null,
    ActuatorKind? ActuatorKind = null)
{
    /// <summary>
    /// Turns the request into a registration, or reports everything wrong with it.
    /// </summary>
    /// <param name="registration">The registration, when the request is sound.</param>
    /// <param name="errors">
    /// Field name to messages, in the shape ASP.NET Core's problem-details helper expects. The field
    /// names are this record's own property names, so the client can map each error straight back
    /// to the input it came from with <c>nameof</c>.
    /// </param>
    /// <remarks>
    /// Every rule comes from <see cref="SensorRegistrationRules"/>, the same methods the client form
    /// runs as the user types. Collects every field's problems rather than stopping at the first, for
    /// the same reason the zone walk does: a caller fixing one field at a time learns about the next
    /// only by trying again.
    /// </remarks>
    public bool TryCreateRegistration(
        [NotNullWhen(true)] out SensorRegistration? registration,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        registration = null;

        Collect(errors, nameof(MacAddress), SensorRegistrationRules.MacAddress(MacAddress));
        Collect(errors, nameof(Room), SensorRegistrationRules.Room(Room));
        Collect(errors, nameof(ZoneId), SensorRegistrationRules.ZoneId(ZoneId));
        Collect(errors, nameof(NodeId), SensorRegistrationRules.NodeId(NodeId));
        Collect(errors, nameof(Unit), SensorRegistrationRules.Unit(Unit));
        Collect(errors, nameof(Minimum), SensorRegistrationRules.Range(Minimum, Maximum));
        Collect(errors, nameof(PayloadKind), SensorRegistrationRules.PayloadKind(PayloadKind, Category));
        Collect(errors, nameof(HeartbeatIntervalSeconds),
            SensorRegistrationRules.HeartbeatIntervalSeconds(HeartbeatIntervalSeconds));
        Collect(errors, nameof(ActuatorKind),
            SensorRegistrationRules.ActuatorKind(ActuatorKind, Category, PayloadKind));

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
            HeartbeatIntervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            // Ignored unless it is an actuator, as documented on the parameter.
            Category == SensorCategory.Actuator ? ActuatorKind : null);

        return true;
    }

    private static void Collect(Dictionary<string, string[]> errors, string field, string? message)
    {
        if (message is not null)
        {
            errors[field] = [message];
        }
    }
}
