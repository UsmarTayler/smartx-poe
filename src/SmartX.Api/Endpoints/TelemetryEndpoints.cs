using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartX.Api.Ingestion;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Ingestion of sensor readings, single and batched, and the retained history.
/// </summary>
/// <remarks>
/// <b>The pipeline, stage by stage.</b> A batched reading travels:
/// <list type="number">
/// <item><description>
/// <b>Raw</b> — arrives as jagged JSON, <c>[[21.5, 21.6], [22.0]]</c>, one inner array per window.
/// </description></item>
/// <item><description>
/// <b>Staged</b> — each window is copied into a row of <see cref="TelemetryStagingBuffer{T}"/>,
/// sized to that window's own length.
/// </description></item>
/// <item><description>
/// <b>Flattened</b> — <see cref="TelemetryStagingBuffer{T}.Drain"/> hands the batch over as one
/// pre-sized <see cref="List{T}"/> and empties the staging rows.
/// </description></item>
/// <item><description>
/// <b>Validated</b> — <see cref="TryCreatePacket"/> checks each reading against the registration
/// and turns it into a typed packet.
/// </description></item>
/// <item><description>
/// <b>Retained</b> — an accepted packet enters that node's <see cref="TelemetryRingBuffer{T}"/>.
/// </description></item>
/// <item><description>
/// <b>Scored</b> — the outcome, accepted or not, is recorded against the node's trust.
/// </description></item>
/// <item><description>
/// <b>Snapshot</b> — a response copies the window out as a <see cref="List{T}"/>.
/// </description></item>
/// </list>
/// <para>
/// <b>How a JSON value becomes a typed packet.</b> The reading arrives as a
/// <see cref="JsonElement"/>, a struct pointing into the parsed request. JSON cannot distinguish the
/// types the system cares about — <c>1200</c> is equally a valid int and a valid float — so the
/// registration's <see cref="SensorRegistration.PayloadKind"/> selects the branch, and the JSON is
/// asked to produce exactly that type. A payload that cannot is rejected rather than coerced. Each
/// branch constructs its packet type directly, so <c>T</c> is known at compile time and the payload
/// is never boxed.
/// </para>
/// <para>
/// <b>Why a rejected reading is scored, not just refused.</b> A 400 tells the device it sent
/// something wrong; it tells an operator nothing. Recording the rejection as a trust outcome is what
/// turns a stream of individually refused packets into a node that visibly sinks down the dashboard.
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
    /// <remarks>
    /// Validation happens here, while the caller waits. Storage and scoring happen behind the queue, in
    /// <see cref="TelemetryProcessor"/>. See the queue's own comments for why the line is drawn there.
    /// </remarks>
    private static async Task<IResult> IngestAsync(
        HttpContext context,
        TelemetryIngestionRequest request,
        ISensorRegistry registry,
        ITrustEngine trust,
        TelemetryIngestionQueue queue,
        CriticalClassifier classifier,
        IOptions<IngestionOptions> options,
        CancellationToken cancellationToken)
    {
        var sensor = await registry.FindAsync(request.MacAddress, cancellationToken);

        if (sensor is null)
        {
            // Not scored: without a registration there is no node to hold accountable.
            return UnregisteredSensor(request.MacAddress);
        }

        // A device that does not stamp its own readings is timestamped on arrival. That is a weaker
        // guarantee than a device clock, but better than a null timestamp on a reading that will
        // later be ordered against others.
        var timestamp = request.Timestamp ?? DateTimeOffset.UtcNow;

        if (!TryCreatePacket(sensor, request.Value, timestamp, out var packet, out var rejection))
        {
            // Scored on the request thread, not in the worker: a rejected reading never enters a queue, and
            // the node has still told us something about itself.
            trust.Record(sensor.MacAddress, rejection.Outcome);

            return Rejected(rejection.Reason);
        }

        return Enqueue(context, queue, classifier, options, packet);
    }

    /// <summary>Puts a validated packet into its lane, or reports that the gateway is saturated.</summary>
    /// <remarks>
    /// 202 rather than 201: telemetry is an event stream, and an individual reading is not a resource with
    /// a URL of its own to point a Location header at. The body is a receipt rather than the packet, because
    /// at this moment the packet has not been processed yet — saying otherwise would be a lie the caller
    /// could act on.
    /// </remarks>
    private static IResult Enqueue(
        HttpContext context,
        TelemetryIngestionQueue queue,
        CriticalClassifier classifier,
        IOptions<IngestionOptions> options,
        ITelemetryPacket packet)
    {
        var alert = classifier.IsCritical(packet, out var found) ? found : (CriticalAlert?)null;

        if (queue.TryEnqueue(packet, alert, out var receipt))
        {
            return TypedResults.Accepted((string?)null, receipt);
        }

        return QueueFull(context, options);
    }

    /// <summary>The backpressure answer: 503 with Retry-After, rather than dropping the reading silently.</summary>
    /// <remarks>
    /// A device that is told to come back can come back. A reading accepted into an unbounded queue and then
    /// never processed is lost without anyone being told, which is the failure mode this exists to avoid.
    /// </remarks>
    private static IResult QueueFull(HttpContext context, IOptions<IngestionOptions> options)
    {
        var seconds = options.Value.RetryAfterSeconds;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The ingestion queue is full",
            detail: $"The gateway is behind on processing. Retry in {seconds} seconds.");
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
    /// Each pipeline stage is a numbered step below, in order, so a reading can be traced from the
    /// request body to the node's trust.
    /// </remarks>
    private static async Task<IResult> IngestBatchAsync(
        HttpContext context,
        TelemetryBatchRequest request,
        ISensorRegistry registry,
        ITrustEngine trust,
        TelemetryIngestionQueue queue,
        CriticalClassifier classifier,
        IOptions<IngestionOptions> options,
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

        // STAGE 2 — stage the raw windows. The outer array is reserved at the batch's window count,
        // so it never has to grow; each row is allocated at its own window's length.
        var staging = new TelemetryStagingBuffer<JsonElement>(request.Windows.Length);

        foreach (var window in request.Windows)
        {
            staging.Stage(window ?? []);
        }

        // Captured before draining, because draining resets the counters this describes.
        var statistics = staging.Statistics;

        // STAGE 3 — flatten into one pre-sized list and release the staged rows.
        var raw = staging.Drain();

        var timestamp = request.Timestamp ?? DateTimeOffset.UtcNow;
        var accepted = 0;
        var errors = new List<string>();

        for (var index = 0; index < raw.Count; index++)
        {
            // STAGE 4 — validate and convert.
            if (TryCreatePacket(sensor, raw[index], timestamp, out var packet, out var rejection))
            {
                // STAGE 5 — into its lane, one reading at a time so the batch keeps its order in the queue.
                var alert = classifier.IsCritical(packet, out var found) ? found : (CriticalAlert?)null;

                if (!queue.TryEnqueue(packet, alert, out _))
                {
                    // The rest of the batch would only be refused too, so the caller is told to retry the
                    // whole thing rather than left to work out which readings got through.
                    return QueueFull(context, options);
                }

                accepted++;
            }
            else
            {
                // A rejected reading is never queued, but it is still scored. One bad sample does not
                // discard the batch, and its position is reported so the caller can find it.
                trust.Record(sensor.MacAddress, rejection.Outcome);
                errors.Add($"Reading {index}: {rejection.Reason}");
            }
        }

        // STAGE 6 — retention and scoring now happen in the worker, so what is reported here is how many
        // readings were queued, not how many are already stored.
        return TypedResults.Accepted(
            (string?)null,
            new TelemetryBatchResult(
                sensor.MacAddress, accepted, errors.Count, statistics, errors));
    }

    /// <summary>
    /// Turns one raw JSON reading into the packet type the sensor's registration calls for.
    /// </summary>
    /// <param name="sensor">The registration that decides the target type and permitted range.</param>
    /// <param name="value">The reading as it arrived.</param>
    /// <param name="timestamp">When the reading was taken.</param>
    /// <param name="packet">The typed packet, when the reading is acceptable.</param>
    /// <param name="rejection">Why it was not, and what kind of failure that counts as.</param>
    /// <remarks>
    /// Single-sourced so the batch path and the single-reading path cannot drift apart. Two copies
    /// of these rules would eventually disagree about whether 21.5 is a valid integer reading, and
    /// the answer — and the trust penalty — would depend on which endpoint a device happened to use.
    /// <para>
    /// Internal rather than private for the same reason: the mesh simulator judges its generated
    /// readings with this method, so a simulated fault is rejected by exactly the rules a real device's
    /// would be, not by a second copy of them.
    /// </para>
    /// </remarks>
    internal static bool TryCreatePacket(
        SensorRegistration sensor,
        JsonElement value,
        DateTimeOffset timestamp,
        [NotNullWhen(true)] out ITelemetryPacket? packet,
        [NotNullWhen(false)] out PacketRejection? rejection)
    {
        packet = null;
        rejection = null;

        switch (sensor.PayloadKind)
        {
            case TelemetryPayloadKind.State:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    rejection = Mismatch(sensor, value, "true or false");

                    return false;
                }

                packet = new StatePacket(sensor.MacAddress, value.GetBoolean(), timestamp, sensor.Unit);

                return true;

            case TelemetryPayloadKind.Integer:
                // TryGetInt32 fails on a fractional or oversized number, so an integer meter
                // reporting 21.5 is caught here rather than silently truncated to 21.
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var whole))
                {
                    rejection = Mismatch(sensor, value, "whole numbers");

                    return false;
                }

                if (!sensor.ExpectedRange.Contains(whole))
                {
                    rejection = OutOfRange(sensor);

                    return false;
                }

                packet = new NumericPacket<int>(
                    sensor.MacAddress, whole, timestamp, sensor.Category, sensor.Unit);

                return true;

            case TelemetryPayloadKind.Real:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var real))
                {
                    rejection = Mismatch(sensor, value, "numbers");

                    return false;
                }

                if (!sensor.ExpectedRange.Contains(real))
                {
                    rejection = OutOfRange(sensor);

                    return false;
                }

                packet = new NumericPacket<float>(
                    sensor.MacAddress, real, timestamp, sensor.Category, sensor.Unit);

                return true;

            default:
                // Unreachable for any declared member, but an enum can hold a value outside its
                // declared set. Counted as a schema failure: the device and its registration disagree
                // about what it sends.
                rejection = new PacketRejection(
                    TrustOutcome.SchemaFailure,
                    $"sensor is registered with payload kind " +
                    $"'{sensor.PayloadKind}', which this gateway cannot route");

                return false;
        }
    }

    // Rejection reasons are phrased as the end of a sentence, without the value and without the
    // sensor's MAC. They are always shown after something that already names both: "21.5 rejected:"
    // on the dashboard, "Reading 3:" in a batch result. Repeating either inside the reason produced
    // lines like "21.5 rejected: Sensor 'AA:BB:…' … Received Number", which said the same thing twice
    // and ended on a JSON token name no operator should need to know.

    /// <summary>The payload is not the shape the registration promised: a schema failure.</summary>
    private static PacketRejection Mismatch(SensorRegistration sensor, JsonElement value, string expected)
    {
        // A fractional number sent to an integer sensor is still a number, so "not a number" would be
        // false; the requirement for whole numbers is the whole explanation.
        var received = value.ValueKind == JsonValueKind.Number && sensor.PayloadKind == TelemetryPayloadKind.Integer
            ? string.Empty
            : $", not {Describe(value.ValueKind)}";

        return new PacketRejection(
            TrustOutcome.SchemaFailure,
            $"sensor is registered as {sensor.PayloadKind}, so readings must be {expected}{received}");
    }

    /// <summary>The payload is the right shape but outside the promised range: a range violation.</summary>
    private static PacketRejection OutOfRange(SensorRegistration sensor) =>
        new(TrustOutcome.RangeViolation,
            $"outside the expected range {sensor.ExpectedRange.Minimum:0.##}–{sensor.ExpectedRange.Maximum:0.##} {sensor.Unit}");

    /// <summary>Names a JSON value's kind in words rather than as a token name.</summary>
    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => "a number",
        JsonValueKind.String => "text",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        _ => "a missing value"
    };

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

    /// <summary>Why a reading was refused, and what kind of failure that counts as for trust.</summary>
    /// <remarks>
    /// The classification travels with the message because it is made once, at the point the failure
    /// is detected, where it is known for certain. Working it out later from the message would mean
    /// parsing English to decide whether something was a range violation or a type failure.
    /// </remarks>
    internal sealed record PacketRejection(TrustOutcome Outcome, string Reason);
}
