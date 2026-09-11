using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the trust endpoints end to end: registration tracks, ingestion scores, reads order.
/// </summary>
public class TrustEndpointTests
{
    /// <summary>
    /// Three nodes, one per band: a thermometer with three clean readings (nominal), a switch that
    /// has said nothing (the 0.5 prior, degrading), and a meter whose only reading was the wrong
    /// type (0.25, fault).
    /// </summary>
    private static async Task<HttpClient> WithOneNodePerBandAsync(WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();

        await client.PostAsync("/api/sensors", Thermometer10To30());
        await client.PostAsync("/api/sensors",
            Registration(Meter, "zone-2", "PowerConsumption", "kWh", 0, 10000, "Integer"));
        await client.PostAsync("/api/sensors",
            Registration(Switch, "sub-zone-b", "Actuator", "state", 0, 1, "State"));

        for (var reading = 0; reading < 3; reading++)
        {
            await client.PostAsync("/api/telemetry", Reading(Thermometer, "21.5"));
        }

        await client.PostAsync("/api/telemetry", Reading(Meter, "21.5"));

        return client;
    }

    [Fact]
    public async Task Registering_a_sensor_starts_tracking_it_at_the_prior_score()
    {
        using var api = New();
        var client = api.CreateClient();
        await client.PostAsync("/api/sensors", Thermometer10To30());

        var trust = await client.GetFromJsonAsync<NodeTrustSnapshot>($"/api/trust/{Thermometer}");

        Assert.Equal(0.5, trust?.Score);
        Assert.Equal("no telemetry yet", trust?.Reason);
    }

    [Fact]
    public async Task The_trust_list_is_ordered_worst_first()
    {
        using var api = New();
        var client = await WithOneNodePerBandAsync(api);

        var nodes = await client.GetFromJsonAsync<List<NodeTrustSnapshot>>("/api/trust");

        Assert.Equal([Meter, Switch, Thermometer], nodes?.Select(node => node.MacAddress));
        Assert.Equal(
            [TrustBand.Fault, TrustBand.Degrading, TrustBand.Nominal],
            nodes?.Select(node => node.Band));
    }

    [Fact]
    public async Task A_single_node_comes_back_with_its_reason()
    {
        using var api = New();
        var client = await WithOneNodePerBandAsync(api);

        var meter = await client.GetFromJsonAsync<NodeTrustSnapshot>($"/api/trust/{Meter}");

        Assert.Equal(0.25, meter?.Score);
        Assert.Equal("1 type failure", meter?.Reason);
    }

    [Fact]
    public async Task A_range_violation_through_the_api_is_named_in_the_reason()
    {
        using var api = New();
        var client = api.CreateClient();
        await client.PostAsync("/api/sensors", Thermometer10To30());

        await client.PostAsync("/api/telemetry", Reading(Thermometer, "40.0"));

        var trust = await client.GetFromJsonAsync<NodeTrustSnapshot>($"/api/trust/{Thermometer}");
        Assert.Equal("1 range violation", trust?.Reason);
    }

    [Fact]
    public async Task Readings_inside_a_batch_are_scored_individually()
    {
        using var api = New();
        var client = api.CreateClient();
        await client.PostAsync("/api/sensors", Thermometer10To30());

        await client.PostAsync("/api/telemetry/batch",
            Json($$"""{ "macAddress": "{{Thermometer}}", "windows": [[21.5, 40.0], [22.0]] }"""));

        var trust = await client.GetFromJsonAsync<NodeTrustSnapshot>($"/api/trust/{Thermometer}");
        Assert.Equal(2, trust?.RecentSuccesses);
        Assert.Equal(1, trust?.RecentFailures);
    }

    [Fact]
    public async Task History_includes_the_moment_a_node_changed_band()
    {
        using var api = New();
        var client = await WithOneNodePerBandAsync(api);

        var history = await client.GetFromJsonAsync<List<TrustHistoryPoint>>($"/api/trust/{Meter}/history");

        Assert.Equal([TrustBand.Degrading, TrustBand.Fault], history?.Select(point => point.Band));
    }

    [Fact]
    public async Task The_summary_counts_each_band_and_names_the_worst_node()
    {
        using var api = New();
        var client = await WithOneNodePerBandAsync(api);

        var summary = await client.GetFromJsonAsync<TrustSummary>("/api/trust/summary");

        Assert.Equal(3, summary?.NodeCount);
        Assert.Equal((1, 1, 1), (summary?.Nominal, summary?.Degrading, summary?.Fault));
        Assert.Equal(Meter, summary?.WorstNode);
        Assert.Equal(0.25, summary?.LowestScore);
        Assert.InRange(summary!.MeshConfidence!.Value, 0.51, 0.52);
    }

    [Theory]
    [InlineData("/api/trust/AA:BB:CC:DD:EE:09")]
    [InlineData("/api/trust/AA:BB:CC:DD:EE:09/history")]
    public async Task An_untracked_node_returns_404(string route)
    {
        using var api = New();
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
    }
}
