using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the full path: raw jagged JSON, staged, flattened, validated, retained, snapshotted.
/// </summary>
public class PipelineTests
{
    private static StringContent Batch(string mac, string windowsJson) =>
        Json($$"""{ "macAddress": "{{mac}}", "windows": {{windowsJson}} }""");

    private static async Task<HttpClient> WithSensorsAsync(WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();

        await client.PostAsync("/api/sensors", Thermometer10To30());
        await client.PostAsync("/api/sensors",
            Registration(Meter, "zone-2", "PowerConsumption", "kWh", 0, 10000, "Integer"));

        return client;
    }

    [Fact]
    public async Task A_batch_of_uneven_windows_is_staged_and_ingested()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync(
            "/api/telemetry/batch", Batch(Thermometer, "[[21.5, 21.75, 22.0], [18.0], [30.0, 29.5]]"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<TelemetryBatchResult>();

        Assert.Equal(6, result?.Accepted);
        Assert.Equal(0, result?.Rejected);
        Assert.Equal(6, result?.Retained);
        Assert.Equal(3, result?.Staging.WindowCount);
    }

    [Fact]
    public async Task The_batch_result_reports_what_a_rectangular_layout_would_have_wasted()
    {
        // Three windows, longest holding three readings: a rectangle would allocate nine cells to
        // carry six. This figure is what justifies the jagged layout, so the API surfaces it.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync(
            "/api/telemetry/batch", Batch(Thermometer, "[[21.5, 21.75, 22.0], [18.0], [30.0, 29.5]]"));

        var staging = (await response.Content.ReadFromJsonAsync<TelemetryBatchResult>())!.Staging;

        Assert.Equal(3, staging.LongestWindow);
        Assert.Equal(9, staging.RectangularCells);
        Assert.Equal(3, staging.WastedCells);
    }

    [Fact]
    public async Task One_bad_reading_does_not_discard_the_rest_of_the_batch()
    {
        // 40.0 is outside the thermometer's 10-30 range, and "warm" is not a number at all. The
        // other two readings are fine, and discarding them over a faulty sample would lose good
        // telemetry.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync(
            "/api/telemetry/batch", Batch(Thermometer, """[[21.5, 40.0], ["warm", 22.0]]"""));

        var result = await response.Content.ReadFromJsonAsync<TelemetryBatchResult>();

        Assert.Equal(2, result?.Accepted);
        Assert.Equal(2, result?.Rejected);

        // The position in the flattened batch is reported, so the caller can find the sample.
        Assert.Contains(result!.Errors, error => error.StartsWith("Reading 1:", StringComparison.Ordinal));
        Assert.Contains(result!.Errors, error => error.StartsWith("Reading 2:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_window_is_staged_without_producing_readings()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync(
            "/api/telemetry/batch", Batch(Thermometer, "[[21.5], [], [22.0]]"));

        var result = await response.Content.ReadFromJsonAsync<TelemetryBatchResult>();

        Assert.Equal(3, result?.Staging.WindowCount);
        Assert.Equal(2, result?.Accepted);
    }

    [Fact]
    public async Task A_batch_for_an_unregistered_sensor_returns_404()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/telemetry/batch", Batch(Thermometer, "[[21.5]]"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retained_history_comes_back_oldest_first_with_its_values_intact()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        await client.PostAsync("/api/telemetry/batch", Batch(Thermometer, "[[21.5, 22.0], [23.5]]"));

        var history = await client.GetFromJsonAsync<List<ITelemetryPacket>>(
            $"/api/sensors/{Thermometer}/telemetry");

        Assert.Equal(3, history?.Count);

        // The polymorphic discriminator is what keeps the reading itself in the response; without
        // it these would deserialise as metadata with no value.
        var readings = history!.Cast<NumericPacket<float>>().Select(packet => packet.Value);
        Assert.Equal([21.5f, 22.0f, 23.5f], readings);
    }

    [Fact]
    public async Task A_registered_sensor_that_has_not_reported_has_an_empty_history()
    {
        // Empty rather than 404: "no readings yet" and "no such device" are different answers.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.GetAsync($"/api/sensors/{Thermometer}/telemetry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ITelemetryPacket>>() ?? []);
    }

    [Fact]
    public async Task History_for_an_unregistered_sensor_returns_404()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.GetAsync("/api/sensors/AA:BB:CC:DD:EE:09/telemetry");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_batch_larger_than_the_retention_window_keeps_only_the_newest_readings()
    {
        // 300 readings into a 256-reading window. The batch pushes its own earliest readings out as
        // it is ingested, which is the sliding window doing exactly what it exists for.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var readings = string.Join(", ", Enumerable.Range(0, 300));

        var response = await client.PostAsync("/api/telemetry/batch", Batch(Meter, $"[[{readings}]]"));
        var result = await response.Content.ReadFromJsonAsync<TelemetryBatchResult>();

        Assert.Equal(300, result?.Accepted);
        Assert.Equal(256, result?.Retained);

        var history = await client.GetFromJsonAsync<List<ITelemetryPacket>>(
            $"/api/sensors/{Meter}/telemetry");

        Assert.Equal(256, history?.Count);

        // The first 44 were evicted, so the window now runs from 44 to 299.
        Assert.Equal(44, ((NumericPacket<int>)history![0]).Value);
        Assert.Equal(299, ((NumericPacket<int>)history![^1]).Value);
    }

    [Fact]
    public async Task Single_and_batch_ingestion_share_one_set_of_rules()
    {
        // 21.5 is not a whole number, so the integer meter must reject it identically on both
        // paths. Two implementations of this rule would eventually disagree.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var single = await client.PostAsync("/api/telemetry", Reading(Meter, "21.5"));
        var batch = await client.PostAsync("/api/telemetry/batch", Batch(Meter, "[[21.5]]"));

        Assert.Equal(HttpStatusCode.BadRequest, single.StatusCode);
        Assert.Equal(1, (await batch.Content.ReadFromJsonAsync<TelemetryBatchResult>())?.Rejected);
    }
}
