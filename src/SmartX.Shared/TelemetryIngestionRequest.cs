using System.Text.Json;

namespace SmartX.Shared;

/// <summary>
/// The body of a POST to /api/telemetry.
/// </summary>
/// <param name="MacAddress">The sensor the reading came from.</param>
/// <param name="Value">
/// The reading, left as raw JSON until the sensor's registration says what type it should be.
/// </param>
/// <param name="Timestamp">
/// When the reading was taken. Defaults to the moment of arrival when the device does not say.
/// </param>
/// <remarks>
/// <see cref="Value"/> is a <see cref="JsonElement"/> rather than an <c>object</c> or a
/// <c>dynamic</c>, and the distinction is the point. A <see cref="JsonElement"/> is a struct that
/// holds a window onto the parsed document — reading it costs no allocation, and it can be
/// interrogated for its <see cref="JsonValueKind"/> and converted straight into the target type
/// with <c>TryGetInt32</c> or <c>TryGetSingle</c>. Declaring the field as <c>object</c> would box
/// the number the deserialiser produced, and <c>dynamic</c> would add a runtime binder on top of
/// that.
/// </remarks>
public sealed record TelemetryIngestionRequest(
    string MacAddress,
    JsonElement Value,
    DateTimeOffset? Timestamp = null);
