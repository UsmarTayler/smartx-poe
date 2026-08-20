using System.Text.Json;
using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Ingestion of sensor readings.
/// </summary>
/// <remarks>
/// <b>How a JSON value becomes a typed packet.</b> The reading arrives as a
/// <see cref="JsonElement"/>, which is a struct pointing into the parsed request — reading from it
/// allocates nothing. It is not converted eagerly, because JSON cannot distinguish the types the
/// system cares about: <c>1200</c> is equally a valid int and a valid float, so a packet type
/// inferred from the payload would flip between <c>NumericPacket&lt;int&gt;</c> and
/// <c>NumericPacket&lt;float&gt;</c> for the same meter depending on whether a reading happened to
/// be whole.
/// <para>
/// So the registration decides instead. <see cref="SensorRegistration.PayloadKind"/> was fixed when
/// the device was registered, and it selects the branch. The JSON is then asked to produce exactly
/// that type — <c>TryGetInt32</c>, <c>TryGetSingle</c> or <c>GetBoolean</c> — and a payload that
/// cannot is a 400 rather than a silent coercion. A thermometer sending <c>"warm"</c>, or an
/// integer meter sending <c>21.5</c>, is rejected at the boundary.
/// </para>
/// <para>
/// Each branch constructs its packet type directly, so <c>T</c> is known at compile time and the
/// payload is never boxed on the way in. There is no <c>object</c>, no <c>dynamic</c> and no
/// reflection anywhere on this path.
/// </para>
/// </remarks>
internal static class TelemetryEndpoints
{
    internal static RouteGroupBuilder MapTelemetryEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/telemetry", IngestAsync).WithName("IngestTelemetry");

        return api;
    }

    private static async Task<IResult> IngestAsync(
        TelemetryIngestionRequest request,
        ISensorRegistry registry,
        ITelemetryStore store,
        CancellationToken cancellationToken)
    {
        var sensor = await registry.FindAsync(request.MacAddress, cancellationToken);

        if (sensor is null)
        {
            // Telemetry from an unregistered device is refused rather than stored speculatively:
            // without a registration there is no unit, no category and no expected range, so there
            // is nothing to validate the reading against.
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{request.MacAddress}'. " +
                        "Register it before sending telemetry.");
        }

        // A device that does not stamp its own readings is timestamped on arrival. That is a
        // weaker guarantee than a device clock, but it is better than a null timestamp on a
        // reading that will later be ordered against others.
        var timestamp = request.Timestamp ?? DateTimeOffset.UtcNow;
        var value = request.Value;

        switch (sensor.PayloadKind)
        {
            case TelemetryPayloadKind.State:
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return PayloadMismatch(sensor, value, "a boolean");
                }

                var packet = new StatePacket(sensor.MacAddress, value.GetBoolean(), timestamp, sensor.Unit);
                await store.AddAsync(packet, cancellationToken);

                return TypedResults.Accepted((string?)null, packet);
            }

            case TelemetryPayloadKind.Integer:
            {
                // TryGetInt32 fails on a fractional or oversized number, so an integer meter that
                // reports 21.5 is caught here rather than being silently truncated to 21.
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var reading))
                {
                    return PayloadMismatch(sensor, value, "a whole number");
                }

                if (!sensor.ExpectedRange.Contains(reading))
                {
                    return OutOfRange(sensor, reading);
                }

                var packet = new NumericPacket<int>(
                    sensor.MacAddress, reading, timestamp, sensor.Category, sensor.Unit);

                await store.AddAsync(packet, cancellationToken);

                return TypedResults.Accepted((string?)null, packet);
            }

            case TelemetryPayloadKind.Real:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var reading))
                {
                    return PayloadMismatch(sensor, value, "a number");
                }

                if (!sensor.ExpectedRange.Contains(reading))
                {
                    return OutOfRange(sensor, reading);
                }

                var packet = new NumericPacket<float>(
                    sensor.MacAddress, reading, timestamp, sensor.Category, sensor.Unit);

                await store.AddAsync(packet, cancellationToken);

                return TypedResults.Accepted((string?)null, packet);
            }

            default:
                // Unreachable for any declared member, but an enum can hold a value outside its
                // declared set, and answering that with a 500 would blame the caller's payload for
                // a problem in stored configuration.
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Unsupported payload kind",
                    detail: $"Sensor '{sensor.MacAddress}' is registered with payload kind " +
                            $"'{sensor.PayloadKind}', which this gateway cannot route.");
        }
    }

    /// <summary>The payload is not the shape the sensor's registration promised.</summary>
    private static IResult PayloadMismatch(SensorRegistration sensor, JsonElement value, string expected) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                [nameof(TelemetryIngestionRequest.Value)] =
                    [$"Sensor '{sensor.MacAddress}' is registered as {sensor.PayloadKind}, " +
                     $"so its readings must be {expected}. Received {value.ValueKind}."]
            },
            title: "The reading does not match the sensor's registered payload type");

    /// <summary>The reading is the right shape but outside what the sensor promised to report.</summary>
    private static IResult OutOfRange<T>(SensorRegistration sensor, T reading)
        where T : struct, System.Numerics.INumber<T> =>
        Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                [nameof(TelemetryIngestionRequest.Value)] =
                    [$"Sensor '{sensor.MacAddress}' is expected to report {sensor.Unit} within " +
                     $"{sensor.ExpectedRange}, but sent {reading}."]
            },
            title: "The reading falls outside the sensor's expected range");
}
