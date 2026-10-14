using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartX.Api.Ingestion;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers what the API does with a valid reading now that it is queued: the receipt, the lane it lands in,
/// backpressure when a lane is full, and the desired-state channel devices poll.
/// </summary>
public class IngestionEndpointTests
{
    private const string Hygrometer = "AA:BB:CC:DD:EE:04";

    /// <summary>
    /// A host whose ingestion worker is switched off, so accepted readings stay in the queue.
    /// </summary>
    /// <remarks>
    /// Backpressure is about a queue that is not being drained fast enough. With the worker running, a test
    /// would have to race it to fill the lane. Removing it makes the lane's contents exactly what the test put
    /// there, which is deterministic where a race is not.
    /// </remarks>
    private static WebApplicationFactory<Program> HostWithoutWorker(int maxQueueDepth, int retryAfterSeconds = 5) =>
        new AuthenticatedFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Ingestion:MaxQueueDepth"] = maxQueueDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Ingestion:RetryAfterSeconds"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));

            builder.ConfigureServices(services =>
            {
                var worker = services.Single(service =>
                    service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(TelemetryProcessor));

                services.Remove(worker);
            });
        });

    private static StringContent Batch(string mac, string windowsJson) =>
        Json($$"""{ "macAddress": "{{mac}}", "windows": {{windowsJson}} }""");

    private static async Task<HttpClient> WithSensorsAsync(WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();

        await client.PostAsync("/api/sensors", Thermometer10To30());
        await client.PostAsync("/api/sensors", Registration(Hygrometer, "sub-zone-b", "Environmental", "%RH", 0, 100, "Real"));
        await client.PostAsync("/api/sensors", Registration(Meter, "zone-2", "PowerConsumption", "kWh", 0, 10000, "Integer"));

        return client;
    }

    [Fact]
    public async Task An_accepted_reading_returns_a_receipt_rather_than_the_stored_packet()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry", Reading(Thermometer, "21.5"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var receipt = await response.Content.ReadFromJsonAsync<TelemetryReceipt>();

        Assert.NotEqual(Guid.Empty, receipt?.ReceiptId);
        Assert.Equal(Thermometer, receipt?.MacAddress);
        Assert.Equal("standard", receipt?.Lane);
    }

    [Fact]
    public async Task A_rejected_reading_is_answered_immediately_and_never_queued()
    {
        // Validation stays synchronous: the caller needs the reason now, and a reading that will never be
        // stored has no business waiting in a queue.
        using var api = HostWithoutWorker(maxQueueDepth: 10);
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry", Reading(Meter, "21.5"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var stats = await client.GetFromJsonAsync<PipelineStats>("/api/pipeline/stats");
        Assert.Equal(0, stats?.TotalDepth);

        // And it still counted against the node, on the request thread, without waiting for any worker.
        var trust = await client.GetFromJsonAsync<NodeTrustSnapshot>($"/api/trust/{Meter}");
        Assert.Equal(1, trust?.RecentFailures);
    }

    [Fact]
    public async Task A_reading_from_an_unregistered_sensor_is_still_a_404_and_is_not_queued()
    {
        using var api = HostWithoutWorker(maxQueueDepth: 10);
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/telemetry", Reading("AA:BB:CC:DD:EE:99", "21.5"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<PipelineStats>("/api/pipeline/stats"))?.TotalDepth);
    }

    [Theory]
    [InlineData("20", "critical")]
    [InlineData("34", "critical")]
    [InlineData("55", "standard")]
    public async Task A_valid_reading_is_routed_to_the_lane_its_value_calls_for(string humidity, string expectedLane)
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry", Reading(Hygrometer, humidity));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(expectedLane, (await response.Content.ReadFromJsonAsync<TelemetryReceipt>())?.Lane);
    }

    [Fact]
    public async Task A_full_queue_returns_503_with_retry_after()
    {
        using var api = HostWithoutWorker(maxQueueDepth: 2, retryAfterSeconds: 7);
        var client = await WithSensorsAsync(api);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/telemetry", Reading(Thermometer, "21"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/telemetry", Reading(Thermometer, "22"))).StatusCode);

        var refused = await client.PostAsync("/api/telemetry", Reading(Thermometer, "23"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), refused.Headers.RetryAfter?.Delta);

        var stats = await client.GetFromJsonAsync<PipelineStats>("/api/pipeline/stats");
        Assert.Equal(1, stats?.RejectedWhenFull);
        Assert.Equal(2, stats?.TotalDepth);
    }

    [Fact]
    public async Task A_full_standard_lane_still_accepts_a_critical_reading()
    {
        using var api = HostWithoutWorker(maxQueueDepth: 1);
        var client = await WithSensorsAsync(api);

        await client.PostAsync("/api/telemetry", Reading(Thermometer, "21"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/telemetry", Reading(Thermometer, "22"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/telemetry", Reading(Hygrometer, "10"))).StatusCode);
    }

    [Fact]
    public async Task A_batch_that_meets_a_full_queue_returns_503()
    {
        using var api = HostWithoutWorker(maxQueueDepth: 2, retryAfterSeconds: 3);
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry/batch", Batch(Thermometer, "[[21, 22, 23]]"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task A_batch_is_queued_in_order_one_reading_at_a_time()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        await client.PostAsync("/api/telemetry/batch", Batch(Thermometer, "[[21, 22], [23, 24, 25]]"));
        await DrainAsync(client, expectedProcessed: 5);

        var history = await client.GetFromJsonAsync<List<ITelemetryPacket>>($"/api/sensors/{Thermometer}/telemetry");

        Assert.Equal([21f, 22f, 23f, 24f, 25f], history!.Cast<NumericPacket<float>>().Select(packet => packet.Value));
    }

    [Fact]
    public async Task Pipeline_stats_report_both_lanes_critical_first()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        await client.PostAsync("/api/telemetry", Reading(Thermometer, "21"));
        await client.PostAsync("/api/telemetry", Reading(Hygrometer, "10"));
        await DrainAsync(client, expectedProcessed: 2);

        var stats = await client.GetFromJsonAsync<PipelineStats>("/api/pipeline/stats");

        Assert.Equal(["critical", "standard"], stats?.Lanes.Select(lane => lane.Lane));
        Assert.All(stats!.Lanes, lane => Assert.Equal(1, lane.Processed));
        Assert.Equal(10_000, stats.Capacity);
    }

    // ---- device desired state -------------------------------------------------------------------------

    [Fact]
    public async Task Setting_a_scenario_records_desired_state_with_an_increasing_version()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var first = await client.PostAsync($"/api/simulation/{Thermometer}/scenario", Json("""{ "scenario": "Drift" }"""));
        var second = await client.PostAsync($"/api/simulation/{Thermometer}/scenario", Json("""{ "scenario": "Critical" }"""));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var one = await first.Content.ReadFromJsonAsync<DeviceDesiredState>();
        var two = await second.Content.ReadFromJsonAsync<DeviceDesiredState>();

        Assert.Equal((1L, SimulationScenario.Drift), (one!.Version, one.Scenario));
        Assert.Equal((2L, SimulationScenario.Critical), (two!.Version, two.Scenario));
    }

    [Fact]
    public async Task A_device_reads_its_own_desired_state_and_nothing_else_is_recorded_until_asked()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeviceGetAsync($"/api/devices/{Thermometer}/desired", Thermometer)).StatusCode);

        await client.PostAsync($"/api/simulation/{Thermometer}/scenario", Json("""{ "scenario": "Disconnect" }"""));

        var desired = await client.DeviceGetFromJsonAsync<DeviceDesiredState>($"/api/devices/{Thermometer}/desired", Thermometer);

        Assert.Equal(SimulationScenario.Disconnect, desired?.Scenario);
        Assert.Equal(Thermometer, desired?.MacAddress);
    }

    [Fact]
    public async Task Desired_state_is_found_however_the_mac_is_written()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        await client.PostAsync($"/api/simulation/{Thermometer.ToLowerInvariant()}/scenario", Json("""{ "scenario": "Spike" }"""));

        var desired = await client.DeviceGetFromJsonAsync<DeviceDesiredState>($"/api/devices/{Thermometer}/desired", Thermometer);

        Assert.Equal(SimulationScenario.Spike, desired?.Scenario);
    }

    [Fact]
    public async Task A_scenario_for_an_unregistered_sensor_is_a_404_and_creates_no_shadow()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/simulation/AA:BB:CC:DD:EE:99/scenario", Json("""{ "scenario": "Drift" }"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<DeviceDesiredState>>("/api/simulation"))!);
    }

    [Fact]
    public async Task An_unknown_scenario_is_a_400_that_names_the_choices()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync($"/api/simulation/{Thermometer}/scenario", Json("""{ "scenario": 99 }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Critical", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
