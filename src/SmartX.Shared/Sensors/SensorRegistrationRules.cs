namespace SmartX.Shared;

/// <summary>
/// The per-field rules a sensor registration must satisfy, shared by the client form and the API.
/// </summary>
/// <remarks>
/// One definition with two callers. The client runs these as the user types, so a malformed MAC is
/// flagged before anything is sent; the API runs the very same methods inside
/// <see cref="SensorRegistrationRequest.TryCreateRegistration"/>, so a request that bypasses the form
/// is held to the same standard. Written twice — once for the server, once in a form library's
/// validation syntax for the browser — the two would drift, and the form would start accepting
/// addresses the server rejects, or refusing ones it would accept.
/// <para>
/// Each rule returns null when the value is acceptable and a message when it is not, which is the
/// shape a MudBlazor field's validation function takes directly.
/// </para>
/// </remarks>
public static class SensorRegistrationRules
{
    /// <summary>A MAC address: present, well-formed, and not the reserved aggregate address.</summary>
    public static string? MacAddress(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "A MAC address is required."
        : MacAddressValidator.IsValid(value) ? null
        : "Expected six hex pairs separated consistently by colons or hyphens, like AA:BB:CC:DD:EE:01, " +
          "and not the reserved all-zero address.";

    /// <summary>The room or bay the sensor is installed in.</summary>
    public static string? Room(string? value) => Required(value, "A room");

    /// <summary>The zone the sensor is registered into.</summary>
    public static string? ZoneId(string? value) => Required(value, "A zone");

    /// <summary>The mesh node the sensor reports through.</summary>
    public static string? NodeId(string? value) => Required(value, "A node id");

    /// <summary>The unit readings are expressed in.</summary>
    public static string? Unit(string? value) => Required(value, "A unit");

    /// <summary>The expected range, whose bounds must be real numbers in order.</summary>
    public static string? Range(double minimum, double maximum) =>
        double.IsNaN(minimum) || double.IsNaN(maximum) ? "Range bounds cannot be NaN."
        : minimum > maximum ? $"Minimum {minimum} exceeds maximum {maximum}."
        : null;

    /// <summary>One bound of the expected range, which a form holds as nullable until it is typed.</summary>
    /// <remarks>
    /// The API's request carries both bounds as plain numbers, so it can never see a missing one; a
    /// form, though, starts with empty boxes. This rule is what stops an untouched box being sent as
    /// zero and quietly registering a sensor expected to read between 0 and 0.
    /// </remarks>
    public static string? RangeBound(double? value, string bound) =>
        value is null ? $"An expected {bound} is required."
        : double.IsNaN(value.Value) || double.IsInfinity(value.Value) ? $"The expected {bound} must be a real number."
        : null;

    /// <summary>A boolean payload only makes sense for an actuator.</summary>
    public static string? PayloadKind(TelemetryPayloadKind payloadKind, SensorCategory category) =>
        payloadKind == TelemetryPayloadKind.State && category != SensorCategory.Actuator
            ? $"A {nameof(TelemetryPayloadKind.State)} sensor must be registered as " +
              $"{nameof(SensorCategory.Actuator)}, not {category}."
            : null;

    /// <summary>An optional heartbeat interval, which if given must be positive.</summary>
    public static string? HeartbeatIntervalSeconds(int? seconds) =>
        seconds is <= 0 ? "A heartbeat interval must be a positive number of seconds." : null;

    private static string? Required(string? value, string subject) =>
        string.IsNullOrWhiteSpace(value) ? $"{subject} is required." : null;
}
