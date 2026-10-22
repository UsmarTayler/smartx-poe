using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Api.History;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>The sampler that feeds the forecast is wired into the running API and stores what the trust engine scores.</summary>
public class TrustSamplingHostTests
{
    [Fact]
    public async Task The_running_api_stores_a_trust_sample_for_a_node_that_reports()
    {
        await using var host = RealtimeHost.Create(new Dictionary<string, string?> { ["Forecast:SampleInterval"] = "00:00:00.001" });

        await host.RegisterSensorAsync(Thermometer);

        for (var index = 0; index < 5; index++)
        {
            await host.Http.PostAsync("api/telemetry", Reading(Thermometer, "20"));
            await Task.Delay(5);
        }

        await DrainAsync(host.Http, expectedProcessed: 5);

        var history = host.Services.GetRequiredService<IHistoryStore>();
        var samples = history.GetRange(Thermometer, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Where(entry => entry.Kind == HistoryKind.TrustSample).ToList();

        Assert.NotEmpty(samples);
        Assert.All(samples, sample => Assert.InRange(sample.Value, 0, 1));
    }

    [Fact]
    public async Task Trust_samples_are_not_in_the_timeline_or_the_mesh_event_stream()
    {
        await using var host = RealtimeHost.Create(new Dictionary<string, string?> { ["Forecast:SampleInterval"] = "00:00:00.001" });

        await host.RegisterSensorAsync(Thermometer);
        await host.Http.PostAsync("api/telemetry", Reading(Thermometer, "20"));
        await DrainAsync(host.Http, expectedProcessed: 1);

        var history = host.Services.GetRequiredService<IHistoryStore>();

        Assert.DoesNotContain(history.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), entry => entry.Kind == HistoryKind.TrustSample);

        using var response = await host.Http.GetAsync($"api/sensors/{Thermometer}/timeline");
        var timeline = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("TrustSample", timeline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forecast_is_pushed_to_a_client_that_is_watching_the_suggestions_topic()
    {
        await using var host = RealtimeHost.Create(new Dictionary<string, string?>
        {
            ["Forecast:SampleInterval"] = "00:00:00.005",
            ["Forecast:Interval"] = "00:00:00.020",
            ["Forecast:EtaChange"] = "00:00:00",
        });

        var client = await host.ConnectAsync(RealtimeTopics.Suggestions);

        await host.RegisterSensorAsync(Thermometer);

        for (var index = 0; index < 60; index++)
        {
            await host.Http.PostAsync("api/telemetry", Reading(Thermometer, "20"));
            await Task.Delay(8);
        }

        Insight? pushed = null;

        for (var index = 0; index < 400 && pushed is null; index++)
        {
            await host.Http.PostAsync("api/telemetry", Reading(Thermometer, "99"));
            await Task.Delay(30);

            pushed = client.Messages<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated)
                .SelectMany(update => update.Insights)
                .FirstOrDefault(insight => insight.Kind == InsightKind.Anticipated && insight.MacAddress == Thermometer);
        }

        Assert.NotNull(pushed);
        Assert.Contains("trust falling", pushed.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_that_starts_to_fail_while_the_api_runs_is_forecast_before_it_is_in_fault()
    {
        // The timers are shortened so the whole thing happens in seconds: a sample every 20 ms, a forecast pass every 50 ms. The
        // line is fitted to however many samples there are inside the lookback, so the shape is the same as over minutes.
        await using var host = RealtimeHost.Create(new Dictionary<string, string?>
        {
            ["Forecast:SampleInterval"] = "00:00:00.005",
            ["Forecast:Interval"] = "00:00:00.020",
            ["Forecast:EtaChange"] = "00:00:00",
        });

        await host.RegisterSensorAsync(Thermometer);

        // Healthy for a while, so the node has earned a score worth losing.
        for (var index = 0; index < 60; index++)
        {
            await host.Http.PostAsync("api/telemetry", Reading(Thermometer, "20"));
            await Task.Delay(8);
        }

        var forecast = default(Insight);
        var faulted = false;

        // Then every reading is out of the 10 to 30 range, and is refused, and counts against it.
        for (var index = 0; index < 400 && forecast is null && !faulted; index++)
        {
            await host.Http.PostAsync("api/telemetry", Reading(Thermometer, "99"));
            await Task.Delay(30);

            var insights = (await host.Http.GetFromJsonAsync<List<Insight>>("api/insights"))!;

            forecast = insights.FirstOrDefault(insight => insight.Kind == InsightKind.Anticipated && insight.MacAddress == Thermometer);
            faulted = insights.Any(insight => insight.Kind == InsightKind.Current && insight.Band == TrustBand.Fault && insight.MacAddress == Thermometer);
        }

        Assert.NotNull(forecast);
        Assert.False(faulted, "It was in Fault before it was forecast.");
        Assert.Contains("trust falling", forecast.Headline, StringComparison.Ordinal);
        Assert.NotNull(forecast.ProjectedFaultAt);
    }
}
