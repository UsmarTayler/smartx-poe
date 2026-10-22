using System.Net;
using System.Net.Http.Json;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Patterns, investigations and the selection report over HTTP: the brief's example driven through the real pipeline, and
/// a fault driven through to a verdict.
/// </summary>
public class PatternEndpointTests
{
    private const string Humidity = "B8:27:EB:10:00:04";   // Rack Row 1 humidity, 30 to 70, in Sub-Zone B: low watch level 38, re-armed above 40
    private const string CracPump = "B8:27:EB:10:00:06";   // CRAC Water Pump, in the same sub-zone
    private const string Thermometer = "B8:27:EB:10:00:01";

    private static StringContent Selection(string mac) => Json($$"""{ "macAddress": "{{mac}}" }""");

    private static StringContent VerdictBody(string verdict, string? cause = null) =>
        Json(cause is null ? $$"""{ "verdict": "{{verdict}}" }""" : $$"""{ "verdict": "{{verdict}}", "cause": "{{cause}}" }""");

    private static async Task<PatternsResponse> PatternsAsync(HttpClient http, string query) =>
        (await http.GetFromJsonAsync<PatternsResponse>($"/api/patterns{query}"))!;

    [Fact]
    public async Task Opening_a_device_is_recorded_as_activity_and_a_stranger_is_a_404()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync("/api/activity/selection", Selection(CracPump))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/activity/selection", Selection("AA:BB:CC:DD:EE:99"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/activity/selection", Selection("not a mac"))).StatusCode);

        var activity = await http.GetFromJsonAsync<ActivityEntry[]>("/api/activity");

        var opened = Assert.Single(activity!, entry => entry.Kind == ActivityKind.Selection);
        Assert.Equal(CracPump, opened.MacAddress);
        Assert.Contains("CRAC Water Pump", opened.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_selection_is_not_a_marker_on_the_devices_timeline_or_an_event_in_the_mesh_stream()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.PostAsync("/api/activity/selection", Selection(CracPump));

        var timeline = await http.GetFromJsonAsync<SensorTimeline>($"/api/sensors/{CracPump}/timeline");
        var events = await http.GetFromJsonAsync<TimelineEvents>("/api/timeline/events");

        Assert.Empty(timeline!.Markers);
        Assert.Empty(events!.Events);
    }

    [Fact]
    public async Task The_patterns_report_the_thresholds_a_pattern_has_to_clear()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var response = await PatternsAsync(http, string.Empty);

        Assert.Empty(response.Associations);
        Assert.Equal(TimeSpan.FromMinutes(10), response.Settings.Window);
        Assert.Equal(3, response.Settings.MinimumSupport);
        Assert.Equal(1.5, response.Settings.MinimumLift);
        Assert.Equal(TimeSpan.FromDays(7), response.Settings.HalfLife);
    }

    [Fact]
    public async Task A_bad_pattern_filter_is_a_400_that_names_it()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        foreach (var (query, field) in new[] { ("trigger=weather", "trigger"), ("level=sideways", "level"), ("limit=0", "limit") })
        {
            var response = await http.GetAsync($"/api/patterns?{query}");
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"?{query}: expected 400, got {(int)response.StatusCode}.");
            Assert.True(body.Contains(field, StringComparison.OrdinalIgnoreCase), $"?{query}: the answer should name '{field}' but was {body}");
        }
    }

    [Fact]
    public async Task A_pattern_question_about_an_unknown_device_is_a_404_and_a_known_one_with_no_history_is_empty()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/patterns?mac=AA:BB:CC:DD:EE:99")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/patterns/for/AA:BB:CC:DD:EE:99")).StatusCode);

        var known = await http.GetFromJsonAsync<PatternAnswer[]>($"/api/patterns/for/{Humidity}");

        Assert.Empty(known!);
    }

    [Fact]
    public async Task The_brief_example_driven_through_the_pipeline_is_learned_at_both_levels()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;
        var processed = 0;

        // Moisture drops below its low watch level (38, which is 20% of the way up 30 to 70), then the operator opens the
        // pump. Between rounds the reading recovers past the re-arm level so the next drop is a new crossing.
        for (var round = 0; round < 4; round++)
        {
            await http.PostAsync("/api/telemetry", Reading(Humidity, "35"));
            await http.PostAsync("/api/telemetry", Reading(Humidity, "35.5"));
            await DrainAsync(http, processed += 2);

            await http.PostAsync("/api/activity/selection", Selection(CracPump));

            await http.PostAsync("/api/telemetry", Reading(Humidity, "60"));
            await DrainAsync(http, processed += 1);
        }

        // Asked with the weak ones included: all four rounds were seconds apart, and an engine that has watched for seconds
        // cannot yet tell a habit from a burst. What is checked here is what was counted.
        var specific = await PatternsAsync(http, $"?mac={Humidity}&trigger=ThresholdCrossing&level=Specific&includeWeak=true");
        var pairing = Assert.Single(specific.Associations, item => item.Action.Target == CracPump);

        Assert.Equal("Low", pairing.Trigger.Detail);
        Assert.Equal(OperatorActionKind.Viewed, pairing.Action.Kind);

        // Four rounds, and not eight: the second low reading in each round did not cross again.
        Assert.InRange(pairing.Support, 3.9, 4.0);
        Assert.InRange(pairing.TriggerCount, 3.9, 4.0);
        Assert.InRange(pairing.Confidence, 0.99, 1.0);

        // The same example at the general level: a humidity sensor in Sub-Zone B, then a pump in it.
        var general = await PatternsAsync(http, $"?mac={Humidity}&trigger=ThresholdCrossing&level=General&includeWeak=true");
        var kind = Assert.Single(general.Associations, item => item.Action.Target.StartsWith("Actuator/Pump@", StringComparison.Ordinal));

        Assert.StartsWith("pump in Zone 1", kind.Action.TargetLabel, StringComparison.Ordinal);
        Assert.Contains("%RH sensor", kind.Trigger.ScopeLabel, StringComparison.Ordinal);
        Assert.InRange(kind.Support, 3.9, 4.0);
    }

    [Fact]
    public async Task A_command_an_operator_issues_is_learned_as_following_the_trigger()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.PostAsync("/api/telemetry", Reading(Humidity, "35"));
        await DrainAsync(http, 1);

        var issued = await http.PostAsync("/api/commands", Json($$"""{ "macAddress": "{{CracPump}}", "action": "PumpOn" }"""));
        Assert.Equal(HttpStatusCode.Accepted, issued.StatusCode);

        var found = await PatternsAsync(http, $"?mac={Humidity}&trigger=ThresholdCrossing&level=Specific&includeWeak=true");
        var pairing = Assert.Single(found.Associations);

        Assert.Equal(OperatorActionKind.Command, pairing.Action.Kind);
        Assert.Equal("PumpOn", pairing.Action.Command);
        Assert.Equal(CracPump, pairing.Action.Target);
    }

    [Fact]
    public async Task A_node_falling_into_fault_opens_an_investigation_that_a_verdict_can_close()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        // One reading outside the registered range is a failure, and a node with nothing else on record is in fault.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/telemetry", Reading(Thermometer, "99"))).StatusCode);

        var list = await http.GetFromJsonAsync<InvestigationDetails[]>("/api/investigations?status=Open");
        var opened = Assert.Single(list!);

        Assert.Equal(Thermometer, opened.MacAddress);
        Assert.Equal(InvestigationStatus.Open, opened.Status);
        Assert.Equal(TrustOutcome.RangeViolation, opened.CausedBy);
        Assert.True(opened.WindowStart <= opened.WindowEnd);
        Assert.Contains(opened.ActiveAlerts, alert => alert.Mac == Thermometer && alert.Kind == AlertKind.Fault);

        var fetched = await http.GetFromJsonAsync<InvestigationDetails>($"/api/investigations/{opened.Id}");
        Assert.Equal(opened.Id, fetched!.Id);

        // Dismissed: a false alarm, so that node is a little less sensitive to range violations.
        var dismissed = await http.PostAsync($"/api/investigations/{opened.Id}/verdict", VerdictBody("Dismiss"));
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);

        var after = (await dismissed.Content.ReadFromJsonAsync<InvestigationDetails>())!;

        Assert.Equal(Verdict.Dismiss, after.Verdict);
        Assert.Equal(InvestigationStatus.Resolved, after.Status);
        Assert.Equal(0.85, after.SensitivityChange?.After);
        Assert.Equal(0.85, after.Sensitivity.Single(item => item.Outcome == TrustOutcome.RangeViolation).Scale);
        Assert.Equal(0.5, after.Sensitivity[0].Floor);

        // Changed to Confirm: given back.
        var confirmed = (await (await http.PostAsync($"/api/investigations/{opened.Id}/verdict", VerdictBody("Confirm")))
            .Content.ReadFromJsonAsync<InvestigationDetails>())!;

        Assert.Null(confirmed.SensitivityChange);
        Assert.Equal(1, confirmed.Sensitivity.Single(item => item.Outcome == TrustOutcome.RangeViolation).Scale);
    }

    [Fact]
    public async Task Bad_investigation_requests_are_refused_with_a_reason()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.PostAsync("/api/telemetry", Reading(Thermometer, "99"));
        var id = (await http.GetFromJsonAsync<InvestigationDetails[]>("/api/investigations"))![0].Id;

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/investigations/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/investigations/{Guid.NewGuid()}/verdict", VerdictBody("Confirm"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/investigations?status=sleeping")).StatusCode);

        var relabel = await http.PostAsync($"/api/investigations/{id}/verdict", VerdictBody("Relabel"));

        Assert.Equal(HttpStatusCode.BadRequest, relabel.StatusCode);
        Assert.Contains("cause", await relabel.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var labelled = (await (await http.PostAsync($"/api/investigations/{id}/verdict", VerdictBody("Relabel", "Network")))
            .Content.ReadFromJsonAsync<InvestigationDetails>())!;

        Assert.Equal(FaultCause.Network, labelled.Cause);
    }
}
