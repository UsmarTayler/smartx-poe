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
            await LoadRecentAsync(sensor.MacAddress, cancellationToken);
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
