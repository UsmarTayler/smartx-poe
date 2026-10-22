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

    /// <summary>Logs in. A wrong username or password is a failed result whose message is the API's own, the same for both.</summary>
    public Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<LoginResponse>(token => http.PostAsJsonAsync("api/auth/login", request, token), cancellationToken);

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
    /// <summary>The retained readings for every registered sensor, by MAC address, in one request.</summary>
    public Task<ApiResult<Dictionary<string, List<ITelemetryPacket>>>> GetAllRecentReadingsAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<Dictionary<string, List<ITelemetryPacket>>>("api/telemetry/recent", cancellationToken);

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

    /// <summary>The suggested actions and automated insights, with the sequence number they are as of.</summary>
    public Task<ApiResult<SuggestionsUpdate>> GetSuggestionsSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SuggestionsUpdate>("api/realtime/suggestions", cancellationToken);

    /// <summary>Marks a suggestion "Not useful". Answers with the suggestions as they are afterwards.</summary>
    public Task<ApiResult<SuggestionsUpdate>> DismissSuggestionAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync<SuggestionsUpdate>(token => http.PostAsync($"api/suggestions/{Uri.EscapeDataString(id)}/dismiss", null, token), cancellationToken);

    /// <summary>Takes a "Not useful" back. Answers with the suggestions as they are afterwards.</summary>
    public Task<ApiResult<SuggestionsUpdate>> RestoreSuggestionAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync<SuggestionsUpdate>(token => http.DeleteAsync($"api/suggestions/{Uri.EscapeDataString(id)}/dismiss", token), cancellationToken);

    /// <summary>The investigations that are still open, newest first.</summary>
    public Task<ApiResult<IReadOnlyList<InvestigationDetails>>> GetOpenInvestigationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<InvestigationDetails>>("api/investigations?status=Open&limit=50", cancellationToken);

    /// <summary>Gives an operator's verdict on an investigation. A later verdict replaces an earlier one.</summary>
    public Task<ApiResult<InvestigationDetails>> GiveVerdictAsync(Guid id, VerdictRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<InvestigationDetails>(token => http.PostAsJsonAsync($"api/investigations/{id}/verdict", request, token), cancellationToken);

    /// <summary>The latest reading from each node, with the sequence number it is as of.</summary>
    public Task<ApiResult<StreamSnapshot>> GetStreamSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetAsync<StreamSnapshot>("api/realtime/stream", cancellationToken);

    // ---- the operator's page: searching, timelines, commands, undo, alerts --------------------------------------

    /// <summary>Finds devices matching every filter given. A filter left null does not constrain the result.</summary>
    public Task<ApiResult<DeviceSearchResponse>> SearchDevicesAsync(
        DeviceSearchFilters filters,
        CancellationToken cancellationToken = default)
    {
        List<string> query = [];

        void Add(string name, object? value)
        {
            if (value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                query.Add($"{name}={Uri.EscapeDataString(value.ToString()!)}");
            }
        }

        Add("q", filters.Q);
        Add("category", filters.Category);
        Add("actuatorKind", filters.ActuatorKind);
        Add("zone", filters.Zone);
        Add("band", filters.Band);
        Add("alert", filters.Alert);

        return GetAsync<DeviceSearchResponse>(
            query.Count == 0 ? "api/devices/search" : $"api/devices/search?{string.Join('&', query)}",
            cancellationToken);
    }

    /// <summary>One sensor's readings over a range, downsampled into at most <paramref name="maxPoints"/> buckets.</summary>
    public Task<ApiResult<SensorTimeline>> GetTimelineAsync(
        string macAddress,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxPoints,
        CancellationToken cancellationToken = default) =>
        GetAsync<SensorTimeline>(
            $"api/sensors/{Uri.EscapeDataString(macAddress)}/timeline?from={Uri.EscapeDataString(from.ToString("O"))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O"))}&maxPoints={maxPoints}",
            cancellationToken);

    /// <summary>Every device and gateway in the mesh, with every link.</summary>
    public Task<ApiResult<TopologyView>> GetTopologyAsync(CancellationToken cancellationToken = default) =>
        GetAsync<TopologyView>("api/topology", cancellationToken);

    /// <summary>The spanning tree: the cheapest links that connect every reachable device to a gateway.</summary>
    public Task<ApiResult<MstView>> GetMstAsync(CancellationToken cancellationToken = default) =>
        GetAsync<MstView>("api/topology/mst", cancellationToken);

    /// <summary>Each device's nearest gateway and hops, and the devices that cannot reach one.</summary>
    public Task<ApiResult<ReachabilityView>> GetReachabilityAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ReachabilityView>("api/topology/reachability", cancellationToken);

    /// <summary>The devices and gateways whose failure would split the mesh.</summary>
    public Task<ApiResult<ArticulationView>> GetArticulationPointsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ArticulationView>("api/topology/articulation-points", cancellationToken);

    /// <summary>One device's route to its gateway along the tree.</summary>
    public Task<ApiResult<RouteView>> GetRouteAsync(string macAddress, CancellationToken cancellationToken = default) =>
        GetAsync<RouteView>($"api/topology/route/{Uri.EscapeDataString(macAddress)}", cancellationToken);

    /// <summary>One sensor's raw readings in a window, from the history tree, with how long the tree took to find them.</summary>
    public Task<ApiResult<HistoryRange>> GetHistoryRangeAsync(
        string macAddress, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default) =>
        GetAsync<HistoryRange>(
            $"api/history/{Uri.EscapeDataString(macAddress)}/range?from={Uri.EscapeDataString(from.ToString("O"))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O"))}&limit={limit}",
            cancellationToken);

    /// <summary>The mesh's current revision and recent route changes, with the sequence of the topology topic, for the feed to start from.</summary>
    public Task<ApiResult<TopologyUpdate>> GetTopologySnapshotAsync(CancellationToken cancellationToken = default) =>
        GetAsync<TopologyUpdate>("api/realtime/topology", cancellationToken);

    /// <summary>One sensor's most recent readings with the sequence of its device topic, for a device feed to start from.</summary>
    public Task<ApiResult<DeviceSnapshot>> GetDeviceSnapshotAsync(string macAddress, CancellationToken cancellationToken = default) =>
        GetAsync<DeviceSnapshot>($"api/realtime/device/{Uri.EscapeDataString(macAddress)}", cancellationToken);

    /// <summary>Sends one command to one actuator.</summary>
    public Task<ApiResult<Command>> IssueCommandAsync(IssueCommandRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Command>(token => http.PostAsJsonAsync("api/commands", request, token), cancellationToken);

    /// <summary>Which devices a zone-wide action would reach, without sending it.</summary>
    public Task<ApiResult<ZonePreview>> PreviewZoneCommandAsync(
        string zoneId,
        CommandAction action,
        double? level,
        CancellationToken cancellationToken = default) =>
        GetAsync<ZonePreview>(
            $"api/commands/zone/preview?zoneId={Uri.EscapeDataString(zoneId)}&action={action}" +
            (level is { } value ? $"&level={value.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : string.Empty),
            cancellationToken);

    /// <summary>Sends one action to every matching actuator in a zone and beneath it, as one undoable step.</summary>
    public Task<ApiResult<CompositeCommandResult>> IssueZoneCommandAsync(
        ZoneCommandRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<CompositeCommandResult>(token => http.PostAsJsonAsync("api/commands/zone", request, token), cancellationToken);

    /// <summary>The undo stack, most recent action first.</summary>
    public Task<ApiResult<IReadOnlyList<UndoStackEntry>>> GetUndoStackAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<UndoStackEntry>>("api/commands/undo-stack", cancellationToken);

    /// <summary>
    /// Undoes the most recent action, and returns what the API says happened, whatever the status code.
    /// </summary>
    /// <remarks>
    /// The undo endpoint answers 200 when something was undone, 409 when it was refused and 404 when the stack was
    /// empty, and puts the same <see cref="UndoResult"/> in all three bodies. A refusal is a result the operator reads,
    /// not an error to be turned into a generic message, so all three are read as the result they carry.
    /// </remarks>
    public async Task<ApiResult<UndoResult>> UndoAsync(CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.PostAsync("api/commands/undo", content: null, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult<UndoResult>.Failure(ApiProblem.Unreachable());
        }

        using (response)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout)
            {
                return ApiResult<UndoResult>.Failure(ApiProblem.Unreachable());
            }

            if (response.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
            {
                try
                {
                    if (await response.Content.ReadFromJsonAsync<UndoResult>(cancellationToken) is { } result)
                    {
                        return ApiResult<UndoResult>.Success(result);
                    }
                }
                catch (JsonException)
                {
                    // Fall through to the problem below.
                }
            }

            return ApiResult<UndoResult>.Failure(await ReadProblemAsync(response, cancellationToken));
        }
    }

    /// <summary>How deep the undo stack goes, and how long a command may wait.</summary>
    public Task<ApiResult<CommandLimits>> GetCommandLimitsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CommandLimits>("api/commands/limits", cancellationToken);

    /// <summary>Marks an alert as seen. The condition is not touched.</summary>
    public Task<ApiResult<ActiveAlert>> AcknowledgeAlertAsync(AlertKey key, CancellationToken cancellationToken = default) =>
        SendAsync<ActiveAlert>(token => http.PostAsJsonAsync("api/alerts/acknowledge", key, token), cancellationToken);

    /// <summary>Records the scenario an operator wants a device to run.</summary>
    public Task<ApiResult<DeviceDesiredState>> SetScenarioAsync(
        string macAddress,
        SimulationScenario scenario,
        CancellationToken cancellationToken = default) =>
        SendAsync<DeviceDesiredState>(
            token => http.PostAsJsonAsync($"api/simulation/{Uri.EscapeDataString(macAddress)}/scenario", new ScenarioChangeRequest(scenario), token),
            cancellationToken);

    /// <summary>What operators did: searches and commands, with the alerts active at the time.</summary>
    public Task<ApiResult<IReadOnlyList<ActivityEntry>>> GetActivityAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        List<string> query = [];

        if (from is { } start)
        {
            query.Add($"from={Uri.EscapeDataString(start.ToString("O"))}");
        }

        if (to is { } end)
        {
            query.Add($"to={Uri.EscapeDataString(end.ToString("O"))}");
        }

        return GetAsync<IReadOnlyList<ActivityEntry>>(
            query.Count == 0 ? "api/activity" : $"api/activity?{string.Join('&', query)}",
            cancellationToken);
    }

    public Task<ApiResult<AttachmentLimits>> GetAttachmentLimitsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AttachmentLimits>("api/attachments/limits", cancellationToken);

    /// <summary>A sensor's attachments, newest first.</summary>
    public Task<ApiResult<IReadOnlyList<AttachmentMetadata>>> GetAttachmentsAsync(
        string macAddress,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<AttachmentMetadata>>($"api/sensors/{Uri.EscapeDataString(macAddress)}/attachments", cancellationToken);

    /// <summary>Tells the gateway an operator opened a device, which is what the pattern analysis learns from.</summary>
    /// <returns>Null on success, otherwise why it failed.</returns>
    public async Task<ApiProblem?> RecordSelectionAsync(string macAddress, string? suggestionId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/activity/selection", new SelectionRequest(macAddress, suggestionId), cancellationToken);

            return response.IsSuccessStatusCode ? null : await ReadProblemAsync(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiProblem.Unreachable();
        }
    }

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
            // A web server in front of the API answers 502 or 504 when the API behind it is down, which is the
            // same fact as a refused connection, said by someone else. Treated alike, so the app reports that the
            // API is unreachable and keeps the last data on screen instead of showing "Bad Gateway" as if it were
            // an answer. 503 is not included: the API itself uses it, to say a queue is full.
            if (response.StatusCode is System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout)
            {
                return ApiResult<T>.Failure(ApiProblem.Unreachable());
            }

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
