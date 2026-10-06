using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Alerts end to end over HTTP: raised by real rejected readings and real silence, suppressed when repeated,
/// acknowledged and cleared.
/// </summary>
public class AlertEndpointTests
{
    private const string Sensor = "AA:BB:CC:DD:EE:11";
    private const string Quiet = "AA:BB:CC:DD:EE:12";

    internal static WebApplicationFactory<Program> Host(Dictionary<string, string?>? extra = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                Dictionary<string, string?> settings = new() { ["Zones:Hierarchy"] = "Demo" };

                foreach (var (key, value) in extra ?? [])
                {
                    settings[key] = value;
                }

                configuration.AddInMemoryCollection(settings);
            }));

    private static async Task RegisterAsync(HttpClient http, string mac, int? heartbeatSeconds = null)
    {
        var heartbeat = heartbeatSeconds is { } seconds ? $""", "heartbeatIntervalSeconds": {seconds}""" : string.Empty;

        var response = await http.PostAsync("/api/sensors", Json($$"""
        {
          "macAddress": "{{mac}}", "room": "Plant Room", "zoneId": "zone-1", "nodeId": "N1",
          "category": "Environmental", "unit": "°C", "minimum": 10, "maximum": 30, "payloadKind": "Real"{{heartbeat}}
        }
        """));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<IReadOnlyList<ActiveAlert>> AlertsAsync(HttpClient http) =>
        (await http.GetFromJsonAsync<List<ActiveAlert>>("/api/alerts"))!;

    private static Task<AlertStats> StatsAsync(HttpClient http) =>
        http.GetFromJsonAsync<AlertStats>("/api/alerts/stats")!;

    private static Task<HttpResponseMessage> Send(HttpClient http, string mac, string value) =>
        http.PostAsync("/api/telemetry", Reading(mac, value));

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    // ---- out of range -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rejected_reading_raises_an_out_of_range_alert_that_names_the_node()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);

        var response = await Send(http, Sensor, "99.5");

        // Part 1 is unchanged: the same 400 as before.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var alert = Assert.Single(await AlertsAsync(http), alert => alert.Key.Kind == AlertKind.OutOfRange);
        Assert.Equal(Sensor, alert.Key.Mac);
        Assert.Equal("Plant Room", alert.Room);
        Assert.Equal(0, alert.SuppressedCount);
        Assert.False(alert.Acknowledged);
        Assert.Contains("outside the expected range 10–30 °C", alert.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_rejections_raise_one_alert_whose_suppressed_count_climbs()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);

        for (var i = 0; i < 15; i++)
        {
            await Send(http, Sensor, "99.5");
        }

        var alert = Assert.Single(await AlertsAsync(http), alert => alert.Key.Kind == AlertKind.OutOfRange);
        Assert.Equal(14, alert.SuppressedCount);

        var stats = await StatsAsync(http);
        Assert.Equal(14, stats.Suppressed);

        // Raised: the out-of-range alert, and the fault alert the node's trust fell into. Not fifteen.
        Assert.Equal(2, stats.Raised);
    }

    [Fact]
    public async Task A_wrong_type_raises_a_type_violation_alert()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);

        var response = await Send(http, Sensor, "\"hot\"");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await AlertsAsync(http), alert => alert.Key == new AlertKey(Sensor, AlertKind.TypeViolation));
    }

    [Fact]
    public async Task A_rejected_reading_also_drives_the_node_into_fault_which_raises_a_fault_alert()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);

        await Send(http, Sensor, "99.5");

        // The existing trust fault event feeds the same set: one mechanism, not two.
        Assert.Contains(await AlertsAsync(http), alert => alert.Key == new AlertKey(Sensor, AlertKind.Fault));
    }

    [Fact]
    public async Task Valid_readings_clear_the_out_of_range_alert_after_the_configured_streak()
    {
        using var api = Host(new() { ["Alerts:ClearAfterValidReadings"] = "3" });
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);
        await Send(http, Sensor, "99.5");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await Send(http, Sensor, "20")).StatusCode);
        }

        await WaitForAsync(
            async () => !(await AlertsAsync(http)).Any(alert => alert.Key.Kind == AlertKind.OutOfRange),
            "the out-of-range alert to clear");

        // And the next bad reading raises it afresh, with its count back at nothing.
        await Send(http, Sensor, "99.5");

        var again = Assert.Single(await AlertsAsync(http), alert => alert.Key.Kind == AlertKind.OutOfRange);
        Assert.Equal(0, again.SuppressedCount);
    }

    // ---- silence ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_silent_node_raises_a_disconnected_alert_whose_count_climbs_until_it_reports_again()
    {
        // A one-second heartbeat, called disconnected after one missed interval, and swept ten times a second,
        // so the whole story plays out in a couple of seconds.
        using var api = Host(new()
        {
            ["Alerts:DisconnectedAfterIntervals"] = "1",
            ["Trust:HeartbeatCheckInterval"] = "00:00:00.100",
        });
        var http = api.CreateClient();
        await RegisterAsync(http, Quiet, heartbeatSeconds: 1);

        await WaitForAsync(
            async () => (await AlertsAsync(http)).FirstOrDefault(alert => alert.Key == new AlertKey(Quiet, AlertKind.Disconnected))?.SuppressedCount >= 5,
            "the disconnected alert's repeat count to climb");

        // Climbing, not multiplying: still one alert.
        Assert.Single(await AlertsAsync(http), alert => alert.Key == new AlertKey(Quiet, AlertKind.Disconnected));

        await Send(http, Quiet, "20");

        await WaitForAsync(
            async () => !(await AlertsAsync(http)).Any(alert => alert.Key.Kind == AlertKind.Disconnected),
            "the disconnected alert to clear when the node reports");

        var stats = await StatsAsync(http);
        Assert.True(stats.Cleared >= 1);
        Assert.True(stats.Suppressed >= 5);
    }

    // ---- acknowledging ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Acknowledging_an_alert_marks_it_seen_and_leaves_it_active()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);
        await Send(http, Sensor, "99.5");

        var response = await http.PostAsync("/api/alerts/acknowledge", Json($$"""{ "mac": "{{Sensor}}", "kind": "OutOfRange" }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<ActiveAlert>())!.Acknowledged);

        // Still on, and now marked as seen.
        var alert = Assert.Single(await AlertsAsync(http), alert => alert.Key.Kind == AlertKind.OutOfRange);
        Assert.True(alert.Acknowledged);
        Assert.Equal(1, (await StatsAsync(http)).Acknowledged);

        // The condition keeps firing, and it is the same acknowledged alert.
        await Send(http, Sensor, "99.5");
        Assert.True(Assert.Single(await AlertsAsync(http), alert => alert.Key.Kind == AlertKind.OutOfRange).Acknowledged);
    }

    [Fact]
    public async Task Acknowledging_twice_is_still_a_200()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);
        await Send(http, Sensor, "99.5");

        var body = Json($$"""{ "mac": "{{Sensor}}", "kind": "OutOfRange" }""");
        await http.PostAsync("/api/alerts/acknowledge", body);

        var again = await http.PostAsync("/api/alerts/acknowledge", Json($$"""{ "mac": "{{Sensor}}", "kind": "OutOfRange" }"""));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task The_mac_in_an_acknowledgement_may_be_written_in_any_accepted_format()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);
        await Send(http, Sensor, "99.5");

        var response = await http.PostAsync("/api/alerts/acknowledge", Json("""{ "mac": "aa-bb-cc-dd-ee-11", "kind": "OutOfRange" }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Acknowledging_an_alert_that_is_not_active_is_a_404()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);

        var response = await http.PostAsync("/api/alerts/acknowledge", Json($$"""{ "mac": "{{Sensor}}", "kind": "Disconnected" }"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("""{ "mac": "not-a-mac", "kind": "Disconnected" }""")]
    [InlineData("""{ "kind": "Disconnected" }""")]
    public async Task An_acknowledgement_with_a_bad_key_is_a_400(string body)
    {
        using var api = Host();
        var http = api.CreateClient();

        var response = await http.PostAsync("/api/alerts/acknowledge", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_alert_kind_is_a_400()
    {
        using var api = Host();
        var http = api.CreateClient();

        var response = await http.PostAsync("/api/alerts/acknowledge", Json($$"""{ "mac": "{{Sensor}}", "kind": "Haunted" }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_alert_list_and_the_totals_start_empty()
    {
        using var api = Host();
        var http = api.CreateClient();

        Assert.Empty(await AlertsAsync(http));
        Assert.Equal(new AlertStats(0, 0, 0, 0, 0), await StatsAsync(http));
    }

    [Fact]
    public async Task An_alert_key_round_trips_through_json_as_a_record_struct()
    {
        using var api = Host();
        var http = api.CreateClient();
        await RegisterAsync(http, Sensor);
        await Send(http, Sensor, "99.5");

        var raw = await http.GetStringAsync("/api/alerts");

        Assert.Contains("\"mac\":\"AA:BB:CC:DD:EE:11\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"OutOfRange\"", raw, StringComparison.Ordinal);
    }
}
