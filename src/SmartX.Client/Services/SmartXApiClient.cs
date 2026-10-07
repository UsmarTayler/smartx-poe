using System.Net.Http.Json;
using System.Text.Json;
using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// The client's only route to the Smart-X API.
/// </summary>
/// <remarks>
/// The client project deliberately holds no reference to SmartX.Api. It knows a base address and
/// the contracts in SmartX.Shared, nothing more, so the two halves are separated in fact and not
/// just in folder layout. Routing every call through one typed client keeps URL strings out of
/// components, and means no component ever touches <see cref="HttpClient"/> directly.
/// <para>
/// Every call returns an <see cref="ApiResult{T}"/>. A validation rejection, a 404 and an API that is
/// not running are all things the UI must render, so they come back as values; only cancellation
/// propagates as an exception, because a cancelled call has no result to render.
/// </para>
/// </remarks>
public sealed class SmartXApiClient(HttpClient http)
{
    /// <summary>Asks the API whether it is up.</summary>
    /// <returns>The reported health, or <c>null</c> when the API could not be reached.</returns>
    public async Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await http.GetFromJsonAsync<HealthResponse>("api/health", cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public Task<ApiResult<IReadOnlyList<ZoneTreeNode>>> GetZoneTreeAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ZoneTreeNode>>("api/zones", cancellationToken);

    public Task<ApiResult<IReadOnlyList<SensorRegistration>>> GetSensorsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<SensorRegistration>>("api/sensors", cancellationToken);

    public Task<ApiResult<SensorRegistration>> RegisterSensorAsync(
        SensorRegistrationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<SensorRegistration>(token => http.PostAsJsonAsync("api/sensors", request, token), cancellationToken);

    public Task<ApiResult<IReadOnlyList<ITelemetryPacket>>> GetRecentReadingsAsync(
        string macAddress,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ITelemetryPacket>>($"api/sensors/{Uri.EscapeDataString(macAddress)}/telemetry", cancellationToken);

    /// <summary>Sends one reading for a sensor.</summary>
    /// <typeparam name="T">The reading's type: a number or a bool, never boxed on the way out.</typeparam>
    /// <remarks>
    /// <see cref="JsonSerializer.SerializeToElement{TValue}(TValue, JsonSerializerOptions?)"/> is
    /// generic, so a float reading becomes a JSON number without passing through <c>object</c>. The
    /// server then decides which packet type it becomes from the sensor's registration, exactly as it
    /// would for a real device.
    /// </remarks>
    /// <returns>
    /// The gateway's receipt. The API validates the reading while the caller waits and processes it just
    /// afterwards, so a success here means accepted for processing, not yet stored.
    /// </returns>
    public Task<ApiResult<TelemetryReceipt>> SendReadingAsync<T>(
        string macAddress,
        T value,
        CancellationToken cancellationToken = default)
        where T : struct =>
        SendAsync<TelemetryReceipt>(
            token => http.PostAsJsonAsync(
                "api/telemetry",
                new TelemetryIngestionRequest(macAddress, JsonSerializer.SerializeToElement(value)),
                token),
            cancellationToken);

    /// <summary>Every tracked node, worst first, each carrying its recent history for a sparkline.</summary>
    public Task<ApiResult<IReadOnlyList<NodeTrustSnapshot>>> GetTrustAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<NodeTrustSnapshot>>("api/trust", cancellationToken);

    public Task<ApiResult<TrustSummary>> GetTrustSummaryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<TrustSummary>("api/trust/summary", cancellationToken);

    /// <summary>What the server will accept as an attachment.</summary>
    /// <summary>The current trust state with the sequence number it is as of, for a real-time feed to start from.</summary>
    public Task<ApiResult<TrustSnapshot>> GetTrustSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetAsync<TrustSnapshot>("api/realtime/trust", cancellationToken);

    /// <summary>The active alerts and recent critical readings, with the sequence number they are as of.</summary>
    public Task<ApiResult<AlertSnapshot>> GetAlertSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AlertSnapshot>("api/realtime/alerts", cancellationToken);

    /// <summary>Recent commands, with the sequence number they are as of.</summary>
    public Task<ApiResult<CommandSnapshot>> GetCommandSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CommandSnapshot>("api/realtime/commands", cancellationToken);

    /// <summary>The ingestion pipeline's statistics, with the sequence number they are as of.</summary>
    public Task<ApiResult<PipelineUpdate>> GetPipelineSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetAsync<PipelineUpdate>("api/realtime/pipeline", cancellationToken);

    /// <summary>The latest reading from each node, with the sequence number it is as of.</summary>
    public Task<ApiResult<StreamSnapshot>> GetStreamSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetAsync<StreamSnapshot>("api/realtime/stream", cancellationToken);

    public Task<ApiResult<AttachmentLimits>> GetAttachmentLimitsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AttachmentLimits>("api/attachments/limits", cancellationToken);

    /// <summary>A sensor's attachments, newest first.</summary>
    public Task<ApiResult<IReadOnlyList<AttachmentMetadata>>> GetAttachmentsAsync(
        string macAddress,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<AttachmentMetadata>>($"api/sensors/{Uri.EscapeDataString(macAddress)}/attachments", cancellationToken);

    /// <summary>Deletes an attachment.</summary>
    /// <returns>Null on success, otherwise why it failed.</returns>
    public async Task<ApiProblem?> DeleteAttachmentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"api/attachments/{id}", cancellationToken);

            return response.IsSuccessStatusCode ? null : await ReadProblemAsync(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiProblem.Unreachable();
        }
    }

    /// <summary>The absolute address of an API path, for requests the browser makes itself.</summary>
    /// <remarks>
    /// Downloads, thumbnails and uploads are requested by the browser directly — an anchor, an image, an
    /// XMLHttpRequest — rather than through this client, so they need the full address, not a relative one.
    /// </remarks>
    public string AbsoluteUrl(string relativePath) => new Uri(http.BaseAddress!, relativePath).ToString();

    private Task<ApiResult<T>> GetAsync<T>(string uri, CancellationToken cancellationToken) =>
        SendAsync<T>(token => http.GetAsync(uri, token), cancellationToken);

    /// <summary>Sends a request and turns every expected outcome into a result.</summary>
    private static async Task<ApiResult<T>> SendAsync<T>(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await send(cancellationToken);
        }
        catch (HttpRequestException)
        {
            // In the browser a refused connection surfaces as a failed fetch, which is the API not
            // running rather than anything the user did.
            return ApiResult<T>.Failure(ApiProblem.Unreachable());
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<T>.Failure(await ReadProblemAsync(response, cancellationToken));
            }

            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);

                return value is null
                    ? ApiResult<T>.Failure(new ApiProblem((int)response.StatusCode, "The API returned an empty response."))
                    : ApiResult<T>.Success(value);
            }
            catch (JsonException)
            {
                return ApiResult<T>.Failure(
                    new ApiProblem((int)response.StatusCode, "The API returned a response the client could not read."));
            }
        }
    }

    /// <summary>Reads an RFC 7807 problem body, falling back to the status line if there is none.</summary>
    private static async Task<ApiProblem> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ProblemPayload>(cancellationToken);

            if (payload is not null)
            {
                return new ApiProblem(
                    status,
                    payload.Title ?? response.ReasonPhrase ?? "The request failed.",
                    payload.Detail,
                    payload.Errors);
            }
        }
        catch (JsonException)
        {
            // Not a problem-details body. Fall through to the status line.
        }

        return new ApiProblem(status, response.ReasonPhrase ?? $"The request failed with status {status}.");
    }

    /// <summary>The parts of a problem-details body the client uses.</summary>
    private sealed record ProblemPayload(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}
