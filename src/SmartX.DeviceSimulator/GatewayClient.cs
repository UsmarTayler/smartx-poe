using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartX.Shared;

namespace SmartX.DeviceSimulator;

/// <summary>How a send ended, from the device's point of view.</summary>
internal enum SendOutcome
{
    /// <summary>The gateway took the reading.</summary>
    Accepted,

    /// <summary>The gateway refused it on its merits: wrong type, out of range, unknown device.</summary>
    Refused,

    /// <summary>The gateway is saturated and asked for the reading again later.</summary>
    Busy,

    /// <summary>The gateway has no record of this device, which usually means the gateway restarted.</summary>
    NotRegistered,

    /// <summary>The gateway could not be reached at all.</summary>
    Unreachable,
}

/// <summary>How the gateway took an acknowledgement.</summary>
internal enum AckDelivery
{
    /// <summary>Taken, or already known. Nothing more to send.</summary>
    Done,

    /// <summary>The gateway will never accept it: it does not know the command, or the command has moved on.</summary>
    Rejected,

    /// <summary>The gateway could not be reached. Try again later.</summary>
    Retry,
}

/// <summary>
/// The only way this program talks to the gateway: over HTTP, exactly as a real device would.
/// </summary>
/// <remarks>
/// Everything a device does is here — registering itself, posting readings, asking what it should be doing —
/// and there is nothing else it can do. The simulator has no reference to SmartX.Api, so there is no
/// in-process shortcut available even by accident.
/// </remarks>
internal sealed class GatewayClient(HttpClient http, ILogger<GatewayClient> logger, DeviceKeyring? keyring = null)
{
    private int _rejectionReported;

    /// <summary>
    /// Raised once, with the MAC of the device concerned, the first time the gateway answers 401 to a request that carried a device
    /// key. That means the fleet secret here is not the one the gateway derives keys from, so every request will be refused:
    /// retrying forever would only fill the log with the same failure. The fleet stops instead and says why.
    /// </summary>
    public event Action<string>? CredentialsRejected;

    /// <summary>
    /// Sends one request as a device: with its key on it, and watching for the one answer that means the key is wrong.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string macAddress, CancellationToken cancellationToken)
    {
        keyring?.Sign(request, macAddress);

        var response = await http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && keyring is { IsConfigured: true } && Interlocked.Exchange(ref _rejectionReported, 1) == 0)
        {
            logger.LogCritical(
                "The gateway rejected the device key for {Mac} (401). Simulator:FleetSecret must be the same secret as Auth:FleetSecret on " +
                "the gateway. Every request would be refused, so the simulator is stopping.",
                macAddress);

            CredentialsRejected?.Invoke(macAddress);
        }

        return response;
    }

    private Task<HttpResponseMessage> PostAsync<T>(string url, string macAddress, T body, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) }, macAddress, cancellationToken);

    private Task<HttpResponseMessage> GetAsync(string url, string macAddress, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, url), macAddress, cancellationToken);

    /// <summary>Waits until the gateway answers its health endpoint.</summary>
    /// <remarks>
    /// Compose starts containers in parallel, and a device that crashed because it won the race would be a
    /// bug in the device. Real hardware powers on before its gateway does and simply keeps trying, so this
    /// backs off from a quarter of a second up to five seconds and keeps going until the timeout.
    /// </remarks>
    public async Task<bool> WaitForGatewayAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(250);
        var reported = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync("api/health", cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                // Not up yet: refused outright, or accepting connections and not answering. Expected, and
                // not worth a stack trace each time.
            }

            if (!reported)
            {
                logger.LogInformation("Waiting for the gateway at {BaseAddress}.", http.BaseAddress);
                reported = true;
            }

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5_000));
        }

        return false;
    }

    /// <summary>Registers a device, treating "already registered" as success.</summary>
    /// <remarks>
    /// Restarting the simulator against a running gateway must be safe, and a device that has already
    /// introduced itself is in exactly the state it wanted to be in. The 409 is the gateway confirming that,
    /// not an error to report.
    /// </remarks>
    public async Task<bool> RegisterAsync(SensorRegistrationRequest registration, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync("api/sensors", registration.MacAddress, registration, cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                return true;
            }

            logger.LogError(
                "The gateway refused the registration for {Mac}: {Status} {Body}",
                registration.MacAddress,
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken));

            return false;
        }
        catch (Exception exception) when (IsTransient(exception, cancellationToken))
        {
            logger.LogWarning(exception, "Could not register {Mac}.", registration.MacAddress);

            return false;
        }
    }

    /// <summary>Tells the gateway what came of a command.</summary>
    /// <remarks>
    /// Safe to repeat: the gateway answers the same acknowledgement for the same command with the same result,
    /// so a device that never saw the reply simply sends it again.
    /// </remarks>
    public async Task<AckDelivery> AcknowledgeAsync(string macAddress, Guid commandId, CommandAckRequest acknowledgement, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync($"api/commands/{commandId}/ack", macAddress, acknowledgement, cancellationToken);

            return response.IsSuccessStatusCode ? AckDelivery.Done
                : response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict ? AckDelivery.Rejected
                : AckDelivery.Retry;
        }
        catch (Exception exception) when (IsTransient(exception, cancellationToken))
        {
            return AckDelivery.Retry;
        }
    }

    /// <summary>Is this the network being unavailable, as opposed to the program being asked to stop?</summary>
    /// <remarks>
    /// A refused connection throws <see cref="HttpRequestException"/>. A connection that is accepted but never
    /// answered surfaces as a <see cref="TaskCanceledException"/> when the client's own timeout fires. Both
    /// mean "try again", and treating only the first that way made the second crash the wait loop — which is
    /// exactly what a gateway that is still starting up looks like. Cancellation the caller asked for is
    /// different, and is left to propagate.
    /// </remarks>
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>Posts one reading.</summary>
    /// <param name="macAddress">The device sending it.</param>
    /// <param name="value">The reading, as it goes on the wire.</param>
    /// <param name="timestamp">When it was taken. Set for backfilled history, null for live readings.</param>
    public async Task<SendOutcome> SendReadingAsync(
        string macAddress,
        JsonElement value,
        DateTimeOffset? timestamp,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync(
                "api/telemetry",
                macAddress,
                new TelemetryIngestionRequest(macAddress, value, timestamp),
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return SendOutcome.Accepted;
            }

            return response.StatusCode switch
            {
                // 503 is backpressure, not failure: the gateway is telling the fleet to slow down.
                HttpStatusCode.ServiceUnavailable => SendOutcome.Busy,

                // The gateway holds its device list in memory, so a restarted gateway has forgotten every
                // device. A device that is told it is unknown introduces itself again.
                HttpStatusCode.NotFound => SendOutcome.NotRegistered,

                _ => SendOutcome.Refused,
            };
        }
        catch (HttpRequestException)
        {
            return SendOutcome.Unreachable;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout on one reading. The next one is along in a few seconds.
            return SendOutcome.Unreachable;
        }
    }

    /// <summary>Checks in: asks the gateway what this device should be doing, and what commands are waiting.</summary>
    /// <param name="macAddress">The device.</param>
    /// <param name="deliverCommands">
    /// True for a device that is there to receive commands. False for a device the harness has switched off,
    /// which must not collect anything: its commands stay pending, and time out, exactly as they would for
    /// hardware that is off. The desired scenario is still read, so it can be told to come back.
    /// </param>
    /// <returns><see langword="null"/> when nothing has been asked of it, which means "carry on".</returns>
    /// <summary>Sends a device's neighbour table. A failure is not worth stopping for: the next report is an interval away.</summary>
    public async Task<bool> ReportNeighboursAsync(string macAddress, NeighbourReport report, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostAsync($"api/devices/{Uri.EscapeDataString(macAddress)}/neighbours", macAddress, report, cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (IsTransient(exception, cancellationToken))
        {
            return false;
        }
    }

    public async Task<DeviceDesiredState?> GetDesiredStateAsync(
        string macAddress,
        CancellationToken cancellationToken,
        bool deliverCommands = true)
    {
        try
        {
            using var response = await GetAsync(
                $"api/devices/{Uri.EscapeDataString(macAddress)}/desired?deliver={(deliverCommands ? "true" : "false")}",
                macAddress,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<DeviceDesiredState>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            // A failed poll is not worth interrupting telemetry for; the next one is seconds away.
            return null;
        }
    }
}
