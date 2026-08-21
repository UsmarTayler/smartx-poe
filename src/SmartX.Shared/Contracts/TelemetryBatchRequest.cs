using System.Text.Json;

namespace SmartX.Shared;

/// <summary>
/// The body of a POST to /api/telemetry/batch.
/// </summary>
/// <param name="MacAddress">The sensor every reading in the batch came from.</param>
/// <param name="Windows">
/// Sequential ingestion windows, each holding that window's raw readings.
/// </param>
/// <param name="Timestamp">
/// When the batch was captured. Applied to every reading in it, defaulting to arrival.
/// </param>
/// <remarks>
/// A jagged <c>JsonElement[][]</c> because the wire format already is one: <c>[[1,2,3],[4],[5,6]]</c>
/// is a JSON array of arrays of differing lengths, which maps onto a jagged array exactly and onto
/// a rectangular one not at all. Deserialising this into a <c>[,]</c> would mean choosing a width
/// and padding every short window out to it before the data had even been looked at.
/// </remarks>
public sealed record TelemetryBatchRequest(
    string MacAddress,
    JsonElement[][] Windows,
    DateTimeOffset? Timestamp = null);
