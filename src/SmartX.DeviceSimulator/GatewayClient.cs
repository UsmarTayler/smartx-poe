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

    /// <summary>The gateway could not be reached at all.</summary>
    Unreachable,
}

/// <summary>
/// The only way this program talks to the gateway: over HTTP, exactly as a real device would.
/// </summary>
/// <remarks>
/// Everything a device does is here — registering itself, posting readings, asking what it should be doing —
/// and there is nothing else it can do. The simulator has no reference to SmartX.Api, so there is no
/// in-process shortcut available even by accident.
/// </remarks>
internal sealed class GatewayClient(HttpClient http, ILogger<GatewayClient> logger)
{
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
            catch (HttpRequestException)
            {
                // Not up yet. Expected, and not worth a stack trace each time.
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
            using var response = await http.PostAsJsonAsync("api/sensors", registration, cancellationToken);

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
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Could not register {Mac}.", registration.MacAddress);

            return false;
        }
    }

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
            using var response = await http.PostAsJsonAsync(
                "api/telemetry",
                new TelemetryIngestionRequest(macAddress, value, timestamp),
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return SendOutcome.Accepted;
            }

            // 503 is backpressure, not failure: the gateway is telling the fleet to slow down.
            return response.StatusCode == HttpStatusCode.ServiceUnavailable ? SendOutcome.Busy : SendOutcome.Refused;
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

    /// <summary>Asks the gateway what this device should be doing.</summary>
    /// <returns><see langword="null"/> when nothing has been asked of it, which means "carry on".</returns>
    public async Task<DeviceDesiredState?> GetDesiredStateAsync(string macAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(
                $"api/devices/{Uri.EscapeDataString(macAddress)}/desired", cancellationToken);

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
