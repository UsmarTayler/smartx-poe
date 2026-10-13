using System.Net;
using System.Net.Http.Json;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Timelines, the event stream and the activity record, over HTTP against the demo fleet.
/// </summary>
public class HistoryEndpointTests
{
    private const string Compressor = "B8:27:EB:10:00:0C";   // 0–7500 W, integers
    private const string Valve = "B8:27:EB:10:00:19";
    private const string Freezer = "B8:27:EB:10:00:17";      // -28 to -12 °C

    private static string Time(DateTimeOffset at) => Uri.EscapeDataString(at.ToString("O"));

    private static StringContent Timed(string mac, string value, DateTimeOffset at) =>
        Json($$"""{ "macAddress": "{{mac}}", "value": {{value}}, "timestamp": "{{at:O}}" }""");

    private static async Task<SensorTimeline> TimelineAsync(HttpClient http, string mac, string query = "") =>
        (await http.GetFromJsonAsync<SensorTimeline>($"/api/sensors/{mac}/timeline{query}"))!;

    // ---- timelines --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_timeline_downsamples_a_large_range_and_a_single_spike_is_still_visible()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        // Half an hour of readings every fifteen seconds at 100 W, with one at 7,000 W in the middle.
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);

        for (var i = 0; i < 120; i++)
        {
            var response = await http.PostAsync("/api/telemetry", Timed(Compressor, i == 60 ? "7000" : "100", start.AddSeconds(i * 15)));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DrainAsync(http, 120);

        var timeline = await TimelineAsync(http, Compressor, $"?from={Time(start.AddSeconds(-1))}&to={Time(DateTimeOffset.UtcNow)}&maxPoints=10");

        // A hundred and twenty readings, ten buckets at most, and the spike is in one of them as a maximum.
        Assert.Equal(TimelineShape.Buckets, timeline.Shape);
        Assert.Equal(120, timeline.ReadingCount);
        Assert.InRange(timeline.Buckets.Count, 1, 10);
        Assert.Equal(120, timeline.Buckets.Sum(bucket => bucket.Count));
        Assert.Equal(7000, timeline.Buckets.Max(bucket => bucket.Max));
        Assert.Equal(100, timeline.Buckets.Min(bucket => bucket.Min));

        // Averaged away: the bucket's mean is a few hundred watts, which looks nothing like a seven-kilowatt spike.
        var spiked = timeline.Buckets.Single(bucket => bucket.Max == 7000);
        Assert.True(spiked.Average < 1000, $"The bucket averaged {spiked.Average:N0} W.");
        Assert.Equal("W", timeline.Unit);
    }

    [Fact]
    public async Task A_state_sensors_timeline_is_its_transitions()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        string[] states = ["false", "false", "true", "true", "true", "false"];

        for (var i = 0; i < states.Length; i++)
        {
            await http.PostAsync("/api/telemetry", Timed(Valve, states[i], start.AddSeconds(i * 10)));
        }

        await DrainAsync(http, states.Length);

        var timeline = await TimelineAsync(http, Valve, $"?from={Time(start.AddSeconds(-1))}&to={Time(DateTimeOffset.UtcNow)}");

        Assert.Equal(TimelineShape.Transitions, timeline.Shape);
        Assert.Empty(timeline.Buckets);
        Assert.Equal(6, timeline.ReadingCount);
        Assert.Equal([false, true, false], timeline.Transitions.Select(transition => transition.State));
    }

    [Fact]
    public async Task Commands_in_the_range_come_back_as_markers_on_the_devices_timeline()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var issued = await http.PostAsync("/api/commands", Json($$"""{ "macAddress": "{{Valve}}", "action": "ValveClose" }"""));
        Assert.Equal(HttpStatusCode.Accepted, issued.StatusCode);

        var timeline = await TimelineAsync(http, Valve);

        var marker = Assert.Single(timeline.Markers, marker => marker.Kind == HistoryKind.Command);
        Assert.Equal($"Issued: ValveClose on {Valve}", marker.Label);
    }

    [Fact]
    public async Task A_registered_sensor_with_no_readings_has_an_empty_timeline()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var timeline = await TimelineAsync(http, Compressor);

        Assert.Empty(timeline.Buckets);
        Assert.Equal(0, timeline.ReadingCount);
    }

    [Fact]
    public async Task The_timeline_range_is_inclusive_at_the_start_and_exclusive_at_the_end()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);

        await http.PostAsync("/api/telemetry", Timed(Compressor, "100", at));
        await DrainAsync(http, 1);

        var onStart = await TimelineAsync(http, Compressor, $"?from={Time(at)}&to={Time(at.AddMinutes(1))}");
        var onEnd = await TimelineAsync(http, Compressor, $"?from={Time(at.AddMinutes(-1))}&to={Time(at)}");

        Assert.Equal(1, onStart.ReadingCount);
        Assert.Equal(0, onEnd.ReadingCount);
    }

    [Fact]
    public async Task A_timeline_for_an_unregistered_sensor_is_a_404()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var response = await http.GetAsync("/api/sensors/AA:BB:CC:DD:EE:99/timeline");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("maxPoints=0")]
    [InlineData("maxPoints=-3")]
    [InlineData("maxPoints=5000")]
    public async Task A_timeline_with_a_bad_point_limit_is_a_400(string query)
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var response = await http.GetAsync($"/api/sensors/{Compressor}/timeline?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_timeline_whose_range_is_empty_or_backwards_is_a_400()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;
        var now = DateTimeOffset.UtcNow;

        var same = await http.GetAsync($"/api/sensors/{Compressor}/timeline?from={Time(now)}&to={Time(now)}");
        var backwards = await http.GetAsync($"/api/sensors/{Compressor}/timeline?from={Time(now)}&to={Time(now.AddHours(-1))}");

        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    // ---- the mesh-wide stream ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_event_stream_holds_commands_alerts_and_trust_transitions_in_time_order()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.PostAsync("/api/telemetry", Reading(Freezer, "99"));
        await http.PostAsync("/api/commands", Json($$"""{ "macAddress": "{{Valve}}", "action": "ValveClose" }"""));

        var stream = (await http.GetFromJsonAsync<TimelineEvents>("/api/timeline/events"))!;

        Assert.False(stream.Truncated);
        Assert.Contains(stream.Events, e => e.Kind == HistoryKind.AlertRaised && e.MacAddress == Freezer);
        Assert.Contains(stream.Events, e => e.Kind == HistoryKind.TrustTransition && e.MacAddress == Freezer);
        Assert.Contains(stream.Events, e => e.Kind == HistoryKind.Command && e.MacAddress == Valve);

        // Searches are not part of what happened to the mesh.
        Assert.DoesNotContain(stream.Events, e => e.Kind == HistoryKind.Search);

        Assert.Equal(stream.Events.OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence).Select(e => e.Sequence), stream.Events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task A_limit_returns_the_most_recent_events_and_says_it_truncated()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        for (var i = 0; i < 5; i++)
        {
            await http.PostAsync("/api/commands", Json($$"""{ "macAddress": "{{Valve}}", "action": "{{(i % 2 == 0 ? "ValveClose" : "ValveOpen")}}" }"""));
        }

        var all = (await http.GetFromJsonAsync<TimelineEvents>("/api/timeline/events"))!;
        var limited = (await http.GetFromJsonAsync<TimelineEvents>("/api/timeline/events?limit=2"))!;

        Assert.True(limited.Truncated);
        Assert.Equal(2, limited.Events.Count);
        Assert.Equal(all.Events.TakeLast(2).Select(e => e.Sequence), limited.Events.Select(e => e.Sequence));
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=100000")]
    public async Task An_event_stream_with_a_bad_limit_is_a_400(string query)
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"/api/timeline/events?{query}")).StatusCode);
    }

    [Fact]
    public async Task An_event_range_outside_what_happened_is_empty()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;
        await http.PostAsync("/api/commands", Json($$"""{ "macAddress": "{{Valve}}", "action": "ValveClose" }"""));

        var past = DateTimeOffset.UtcNow.AddDays(-10);
        var stream = (await http.GetFromJsonAsync<TimelineEvents>($"/api/timeline/events?from={Time(past)}&to={Time(past.AddHours(1))}"))!;

        Assert.Empty(stream.Events);
        Assert.False(stream.Truncated);
    }

    // ---- activity ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_search_is_recorded_with_its_filters_its_result_count_and_the_alerts_active_at_the_time()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        // Something is wrong when the operator searches.
        await http.PostAsync("/api/telemetry", Reading(Freezer, "99"));
        await http.GetAsync("/api/devices/search?category=Actuator&actuatorKind=Valve");

        var activity = (await http.GetFromJsonAsync<List<ActivityEntry>>("/api/activity"))!;

        var search = Assert.Single(activity, entry => entry.Kind == ActivityKind.Search);
        Assert.Equal(SensorCategory.Actuator, search.Filters!.Category);
        Assert.Equal(ActuatorKind.Valve, search.Filters.ActuatorKind);
        Assert.Equal(6, search.ResultCount);
        Assert.Contains(new AlertKey(Freezer, AlertKind.OutOfRange), search.ActiveAlerts);
        Assert.Contains(new AlertKey(Freezer, AlertKind.Fault), search.ActiveAlerts);
        Assert.Contains("category Actuator", search.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_is_recorded_with_the_alerts_active_when_it_was_issued_and_only_operator_actions_appear()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.PostAsync("/api/telemetry", Reading(Freezer, "99"));

        var issued = await http.PostAsync("/api/commands", Json($$"""{ "macAddress": "{{Valve}}", "action": "ValveClose" }"""));
        var command = (await issued.Content.ReadFromJsonAsync<Command>())!;

        // The device collects and confirms it: those are the device's doing and are not the operator's activity.
        await http.GetAsync($"/api/devices/{Valve}/desired");
        await http.PostAsJsonAsync($"/api/commands/{command.Id}/ack", new CommandAckRequest(true, null));

        var activity = (await http.GetFromJsonAsync<List<ActivityEntry>>("/api/activity"))!;

        var entry = Assert.Single(activity, e => e.Kind == ActivityKind.Command);
        Assert.Equal(command.Id, entry.CommandId);
        Assert.Equal(Valve, entry.MacAddress);
        Assert.Contains(new AlertKey(Freezer, AlertKind.Fault), entry.ActiveAlerts);

        // The command's whole life is still in the command log.
        var log = (await http.GetFromJsonAsync<List<CommandLogEntry>>($"/api/commands/log?commandId={command.Id}"))!;
        Assert.Contains(log, line => line.Event == CommandEvent.Acknowledged);
        Assert.All(log, line => Assert.NotNull(line.ActiveAlerts));
    }

    [Fact]
    public async Task Searches_and_commands_come_back_in_time_order()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.GetAsync("/api/devices/search?q=pump");
        await http.PostAsync("/api/commands", Json($$"""{ "macAddress": "{{Valve}}", "action": "ValveClose" }"""));
        await http.GetAsync("/api/devices/search?band=Fault");

        var activity = (await http.GetFromJsonAsync<List<ActivityEntry>>("/api/activity"))!;

        Assert.Equal([ActivityKind.Search, ActivityKind.Command, ActivityKind.Search], activity.Select(entry => entry.Kind));
        Assert.Equal(activity.OrderBy(entry => entry.Sequence).Select(entry => entry.Sequence), activity.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task A_search_with_no_filters_is_recorded_too()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.GetAsync("/api/devices/search");

        var activity = (await http.GetFromJsonAsync<List<ActivityEntry>>("/api/activity"))!;

        var search = Assert.Single(activity);
        Assert.True(search.Filters!.IsEmpty);
        Assert.Equal(DemoDevices.All.Count, search.ResultCount);
    }

    [Fact]
    public async Task A_search_that_fails_validation_is_not_recorded()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.GetAsync("/api/devices/search?zone=zone-99");
        await http.GetAsync("/api/devices/search?band=Haunted");

        Assert.Empty((await http.GetFromJsonAsync<List<ActivityEntry>>("/api/activity"))!);
    }

    [Fact]
    public async Task The_activity_range_is_bounded_and_validated()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;
        await http.GetAsync("/api/devices/search?q=pump");

        var past = DateTimeOffset.UtcNow.AddDays(-5);
        var none = (await http.GetFromJsonAsync<List<ActivityEntry>>($"/api/activity?from={Time(past)}&to={Time(past.AddHours(1))}"))!;
        var backwards = await http.GetAsync($"/api/activity?from={Time(past)}&to={Time(past.AddHours(-1))}");

        Assert.Empty(none);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }
}
