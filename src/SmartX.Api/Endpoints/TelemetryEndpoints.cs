using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Ingestion of sensor readings, single and batched, and the retained history.
/// </summary>
/// <remarks>
/// <b>The pipeline, stage by stage.</b> A batched reading travels:
/// <list type="number">
/// <item><description>
/// <b>Raw</b> — arrives as jagged JSON, <c>[[21.5, 21.6], [22.0]]</c>, one inner array per
/// ingestion window.
/// </description></item>
/// <item><description>
/// <b>Staged</b> — each window is copied into a row of <see cref="TelemetryStagingBuffer{T}"/>,
/// sized to that window's own length.
/// </description></item>
/// <item><description>
/// <b>Flattened</b> — <see cref="TelemetryStagingBuffer{T}.Drain"/> hands the whole batch over as
/// one pre-sized <see cref="List{T}"/> and empties the staging rows.
/// </description></item>
/// <item><description>
/// <b>Validated</b> — <see cref="TryCreatePacket"/> checks each reading against the registration
/// and turns it into a typed packet.
/// </description></item>
/// <item><description>
/// <b>Retained</b> — the packet enters that node's <see cref="TelemetryRingBuffer{T}"/> inside the
/// store, evicting the oldest reading if the window is full.
/// </description></item>
/// <item><description>
/// <b>Snapshot</b> — a response copies the window out as a <see cref="List{T}"/>.
/// </description></item>
/// </list>
/// <para>
/// <b>How a JSON value becomes a typed packet.</b> The reading arrives as a
/// <see cref="JsonElement"/>, a struct pointing into the parsed request, so reading it allocates
/// nothing. It is not converted eagerly, because JSON cannot distinguish the types the system cares
/// about: <c>1200</c> is equally a valid int and a valid float, so a packet type inferred from the
/// payload would flip between <c>NumericPacket&lt;int&gt;</c> and <c>NumericPacket&lt;float&gt;</c>
/// for the same meter depending on whether a reading happened to be whole.
/// </para>
/// <para>
/// So the registration decides instead. <see cref="SensorRegistration.PayloadKind"/> was fixed when
/// the device was registered, and it selects the branch. The JSON is then asked to produce exactly
/// that type — <c>TryGetInt32</c>, <c>TryGetSingle</c> or <c>GetBoolean</c> — and a payload that
/// cannot is rejected rather than coerced. Each branch constructs its packet type directly, so
/// <c>T</c> is known at compile time and the payload is never boxed.
/// </para>
/// </remarks>
internal static class TelemetryEndpoints
{
    internal static RouteGroupBuilder MapTelemetryEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/telemetry", IngestAsync).WithName("IngestTelemetry");
        api.MapPost("/telemetry/batch", IngestBatchAsync).WithName("IngestTelemetryBatch");
        api.MapGet("/sensors/{mac}/telemetry", GetHistoryAsync).WithName("GetSensorTelemetry");

        return api;
    }

    /// <summary>Accepts a single reading.</summary>
    private static async Task<IResult> IngestAsync(
        TelemetryIngestionRequest request,
        ISensorRegistry registry,
        ITelemetryStore store,
        CancellationToken cancellationToken)
    {
        var sensor = await registry.FindAsync(request.MacAddress, cancellationToken);

        if (sensor is null)
        {
            return UnregisteredSensor(request.MacAddress);
        }

        // A device that does not stamp its own readings is timestamped on arrival. That is a weaker
        // guarantee than a device clock, but better than a null timestamp on a reading that will
        // later be ordered against others.
        var timestamp = request.Timestamp ?? DateTimeOffset.UtcNow;

        if (!TryCreatePacket(sensor, request.Value, timestamp, out var packet, out var reason))
        {
            return Rejected(reason);
        }

        await store.AddAsync(packet, cancellationToken);

        // 202 rather than 201: telemetry is an event stream, and an individual reading is not a
        // resource with a URL of its own to point a Location header at.
        return TypedResults.Accepted((string?)null, packet);
    }

    /// <summary>The readings currently retained for one node, oldest first.</summary>
    private static async Task<IResult> GetHistoryAsync(
        string mac,
        ISensorRegistry registry,
        ITelemetryStore store,
        CancellationToken cancellationToken)
    {
        // Checked against the registry rather than the store, so a registered sensor that has not
        // reported yet returns an empty history instead of a 404. "No readings" and "no such
        // device" are different answers.
        if (await registry.FindAsync(mac, cancellationToken) is null)
        {
            return UnregisteredSensor(mac);
        }

        var readings = await store.RecentAsync(mac, cancellationToken);

        return Results.Ok(readings);
    }

    /// <summary>
    /// Accepts a batch of ingestion windows through the staging buffer.
    /// </summary>
    /// <remarks>
    /// Each of the six pipeline stages is a named step below, in order, so a reading can be traced
    /// from the request body to the retained window.
    /// </remarks>
    private static async Task<IResult> IngestBatchAsync(
        TelemetryBatchRequest request,
        ISensorRegistry registry,
        ITelemetryStore store,
        CancellationToken cancellationToken)
    {
        var sensor = await registry.FindAsync(request.MacAddress, cancellationToken);

        if (sensor is null)
        {
            return UnregisteredSensor(request.MacAddress);
        }

        if (request.Windows is null)
        {
            return Rejected("A batch must carry at least an empty array of ingestion windows.");
        }

        // STAGE 1 — stage the raw windows. The outer array is reserved at the batch's window count,
        // so it never has to grow; each row is allocated at its own window's length.
        var staging = new TelemetryStagingBuffer<JsonElement>(request.Windows.Length);

        foreach (var window in request.Windows)
        {
            staging.Stage(window ?? []);
        }

        // Captured before draining, because draining resets the counters this describes.
        var statistics = staging.Statistics;

        // STAGE 2 — flatten into one pre-sized list and release the staged rows.
        var raw = staging.Drain();

        var timestamp = request.Timestamp ?? DateTimeOffset.UtcNow;
        var accepted = 0;
        var errors = new List<string>();

        for (var index = 0; index < raw.Count; index++)
        {
            // STAGE 3 — validate and convert.
            if (TryCreatePacket(sensor, raw[index], timestamp, out var packet, out var reason))
            {
                // STAGE 4 — into the node's sliding window.
                await store.AddAsync(packet, cancellationToken);
                accepted++;
            }
            else
            {
                // One bad sample does not discard the batch. The position is reported so the caller
                // can find it in what they sent.
                errors.Add($"Reading {index}: {reason}");
            }
        }

        var retained = await store.CountForAsync(sensor.MacAddress, cancellationToken);

        return TypedResults.Accepted(
            (string?)null,
            new TelemetryBatchResult(
                sensor.MacAddress, accepted, errors.Count, retained, statistics, errors));
    }

    /// <summary>
    /// Turns one raw JSON reading into the packet type the sensor's registration calls for.
    /// </summary>
    /// <param name="sensor">The registration that decides the target type and permitted range.</param>
    /// <param name="value">The reading as it arrived.</param>
    /// <param name="timestamp">When the reading was taken.</param>
    /// <param name="packet">The typed packet, when the reading is acceptable.</param>
    /// <param name="reason">Why it was not, otherwise.</param>
    /// <remarks>
    /// Single-sourced so that the batch path and the single-reading path cannot drift apart. Two
    /// copies of these rules would eventually disagree about something like whether 21.5 is a valid
    /// integer reading, and the answer would then depend on which endpoint the device happened to
    /// use.
    /// </remarks>
    private static bool TryCreatePacket(
        SensorRegistration sensor,
        JsonElement value,
        DateTimeOffset timestamp,
        [NotNullWhen(true)] out ITelemetryPacket? packet,
        [NotNullWhen(false)] out string? reason)
    {
        packet = null;
        reason = null;

        switch (sensor.PayloadKind)
        {
            case TelemetryPayloadKind.State:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    reason = Mismatch(sensor, value, "a boolean");

                    return false;
                }

                packet = new StatePacket(sensor.MacAddress, value.GetBoolean(), timestamp, sensor.Unit);

                return true;

            case TelemetryPayloadKind.Integer:
                // TryGetInt32 fails on a fractional or oversized number, so an integer meter
                // reporting 21.5 is caught here rather than silently truncated to 21.
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var whole))
                {
                    reason = Mismatch(sensor, value, "a whole number");

                    return false;
                }

                if (!sensor.ExpectedRange.Contains(whole))
                {
                    reason = OutOfRange(sensor, whole);

                    return false;
                }

                packet = new NumericPacket<int>(
                    sensor.MacAddress, whole, timestamp, sensor.Category, sensor.Unit);

                return true;

            case TelemetryPayloadKind.Real:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var real))
                {
                    reason = Mismatch(sensor, value, "a number");

                    return false;
                }

                if (!sensor.ExpectedRange.Contains(real))
                {
                    reason = OutOfRange(sensor, real);

                    return false;
                }

                packet = new NumericPacket<float>(
                    sensor.MacAddress, real, timestamp, sensor.Category, sensor.Unit);

                return true;

            default:
                // Unreachable for any declared member, but an enum can hold a value outside its
                // declared set, and that would be a fault in stored configuration rather than in
                // the reading.
                reason = $"Sensor '{sensor.MacAddress}' is registered with payload kind " +
                         $"'{sensor.PayloadKind}', which this gateway cannot route.";

                return false;
        }
    }

    private static string Mismatch(SensorRegistration sensor, JsonElement value, string expected) =>
        $"Sensor '{sensor.MacAddress}' is registered as {sensor.PayloadKind}, so its readings must " +
        $"be {expected}. Received {value.ValueKind}.";

    private static string OutOfRange<T>(SensorRegistration sensor, T reading)
        where T : struct, System.Numerics.INumber<T> =>
        $"{reading} falls outside the expected range {sensor.ExpectedRange} declared for sensor " +
        $"'{sensor.MacAddress}'.";

    private static IResult UnregisteredSensor(string macAddress) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Sensor not registered",
            detail: $"No sensor is registered with MAC address '{macAddress}'. " +
                    "Register it before sending telemetry.");

    private static IResult Rejected(string reason) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [nameof(TelemetryIngestionRequest.Value)] = [reason] },
            title: "The reading was not accepted");
}
