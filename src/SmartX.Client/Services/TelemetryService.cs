using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// Sends readings by hand and holds each sensor's recent history, so the pipeline can be shown live.
/// </summary>
/// <remarks>
/// Per-sensor state is keyed by MAC in a dictionary, the same O(1) lookup the API uses for trust, and
/// is scoped so a sent reading and its outcome are still on screen after navigating away and back.
/// </remarks>
public sealed class TelemetryService(SmartXApiClient api, ApiConnection connection, TimeProvider time)
{
    private readonly Dictionary<string, SensorTelemetry> _bySensor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when any sensor's telemetry state changes.</summary>
    public event Action? Changed;

    /// <summary>One sensor's telemetry state, created empty on first request.</summary>
    public SensorTelemetry For(string macAddress)
    {
        if (!_bySensor.TryGetValue(macAddress, out var state))
        {
            state = new SensorTelemetry();
            _bySensor[macAddress] = state;
        }

        return state;
    }

    /// <summary>
    /// Adds readings the server pushed to the sensors' recent histories, so a card updates as readings arrive.
    /// </summary>
    /// <param name="readings">The pushed readings, oldest first.</param>
    /// <param name="sensors">The registered sensors by MAC, which say what kind of packet each reading is.</param>
    /// <remarks>
    /// A reading no newer than the sensor's latest is ignored. A page loads a sensor's history over HTTP and
    /// is also pushed readings, so the same reading can arrive by both routes, and the timestamp is what tells a
    /// repeat from a new one. A sensor the page does not know is ignored.
    /// </remarks>
    public void ApplyPushed(IReadOnlyList<ReadingPush> readings, IReadOnlyDictionary<string, SensorRegistration> sensors)
    {
        var changed = false;

        foreach (var reading in readings)
        {
            if (!sensors.TryGetValue(reading.MacAddress, out var sensor))
            {
                continue;
            }

            var state = For(reading.MacAddress);

            if (state.Latest is { } latest && latest.Timestamp >= reading.Timestamp)
            {
                continue;
            }

            var packet = ToPacket(sensor, reading);

            // Copy-on-write, and bounded like the ring buffer on the server, so a card holds the same recent
            // window and never grows for as long as the tab stays open.
            var recent = new List<ITelemetryPacket>(Math.Min(state.Recent.Count + 1, RecentLimit));
            recent.AddRange(state.Recent.Count >= RecentLimit ? state.Recent.Skip(state.Recent.Count - RecentLimit + 1) : state.Recent);
            recent.Add(packet);

            state.Recent = recent;
            state.IsLoaded = true;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private const int RecentLimit = 256;

    /// <summary>Turns a pushed reading into the packet type the sensor's registration calls for.</summary>
    private static ITelemetryPacket ToPacket(SensorRegistration sensor, ReadingPush reading) => sensor.PayloadKind switch
    {
        TelemetryPayloadKind.State => new StatePacket(sensor.MacAddress, reading.Value != 0, reading.Timestamp, sensor.Unit),
        TelemetryPayloadKind.Integer => new NumericPacket<int>(sensor.MacAddress, (int)reading.Value, reading.Timestamp, sensor.Category, sensor.Unit),
        _ => new NumericPacket<float>(sensor.MacAddress, (float)reading.Value, reading.Timestamp, sensor.Category, sensor.Unit),
    };

    /// <summary>Loads every sensor's recent readings concurrently.</summary>
    /// <remarks>
    /// All requests start before any is awaited. Loading them one after another would make the page's
    /// load time grow with the number of sensors.
    /// </remarks>
    public Task LoadAllAsync(IReadOnlyList<SensorRegistration> sensors, CancellationToken cancellationToken = default) =>
        Task.WhenAll(sensors.Select(sensor => LoadRecentAsync(sensor.MacAddress, cancellationToken)));

    /// <summary>Loads one sensor's retained readings from its ring buffer on the API.</summary>
    public async Task LoadRecentAsync(string macAddress, CancellationToken cancellationToken = default)
    {
        var state = For(macAddress);
        var result = await api.GetRecentReadingsAsync(macAddress, cancellationToken);

        connection.Report(result.Problem is not { IsUnreachable: true });

        if (result.IsSuccess)
        {
            state.Recent = result.Value;
            state.LoadProblem = null;
        }
        else
        {
            state.LoadProblem = result.Problem;
        }

        state.IsLoaded = true;
        Changed?.Invoke();
    }

    /// <summary>Sends one reading and records what the API made of it.</summary>
    /// <remarks>
    /// The reading is sent exactly as entered, even when the client could tell it is wrong — 21.5 to
    /// an integer meter, say. Refusing it here would hide the very thing this view exists to show:
    /// the server parsing the payload against the registration, rejecting it with a reason, and the
    /// node's trust falling as a result.
    /// </remarks>
    public async Task SendAsync(SensorRegistration sensor, ReadingInput input, CancellationToken cancellationToken = default)
    {
        var state = For(sensor.MacAddress);
        state.IsSending = true;
        Changed?.Invoke();

        var result = sensor.PayloadKind == TelemetryPayloadKind.State
            ? await api.SendReadingAsync(sensor.MacAddress, input.State, cancellationToken)
            : await api.SendReadingAsync(sensor.MacAddress, input.Number ?? double.NaN, cancellationToken);

        connection.Report(result.Problem is not { IsUnreachable: true });

        state.IsSending = false;
        state.LastOutcome = result.IsSuccess
            ? new SendOutcome(true, $"{Label(sensor, input)} accepted", time.GetUtcNow())
            : new SendOutcome(false, $"{input.Display} rejected: {ReasonFor(result.Problem)}", time.GetUtcNow());

        if (result.IsSuccess)
        {
            // Re-read the history from the API rather than appending locally, so what is shown is
            // what the ring buffer actually retained.
            //
            // The API now answers as soon as the reading is validated and stores it just afterwards, so the
            // first read can land before the worker has caught up. Rather than guess at a delay, the page
            // re-reads until the reading appears, a few times, and then settles for what is there — a
            // dropped reading must not leave the view spinning.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await LoadRecentAsync(sensor.MacAddress, cancellationToken);

                if (For(sensor.MacAddress).Recent.Count > 0
                    && For(sensor.MacAddress).Recent[^1].Timestamp >= result.Value.AcceptedAt.AddSeconds(-1))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            }
        }
        else
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The reading as it should be named in a message: with its unit, unless it is on or off.</summary>
    private static string Label(SensorRegistration sensor, ReadingInput input) =>
        sensor.PayloadKind == TelemetryPayloadKind.State ? input.Display : $"{input.Display} {sensor.Unit}";

    /// <summary>Why the API refused a reading, in its own words.</summary>
    /// <remarks>
    /// The server's reason is shown as it was written, never replaced with a generic "error". It is
    /// phrased to follow "21.5 rejected:", which is exactly how it is used here.
    /// </remarks>
    private static string ReasonFor(ApiProblem problem) =>
        problem.IsUnreachable ? "the API could not be reached"
        : problem.ErrorsFor(nameof(TelemetryIngestionRequest.Value)) is [var reason, ..] ? reason
        : problem.Message;
}
