using System.Net;
using System.Net.Http.Json;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Device search over HTTP: each filter, what is refused, and registration. How filters combine and how zones nest is proved against the index in DeviceIndexTests.
/// </summary>
public class SearchEndpointTests
{
    private static async Task<DeviceSearchResponse> SearchAsync(HttpClient http, string query = "") =>
        (await http.GetFromJsonAsync<DeviceSearchResponse>($"/api/devices/search{query}"))!;

    [Fact]
    public async Task With_no_filters_every_registered_device_is_found()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var result = await SearchAsync(http);

        Assert.Equal(DemoDevices.All.Count, result.Count);
        Assert.Equal(result.Devices.Select(device => device.MacAddress).Order(StringComparer.Ordinal), result.Devices.Select(device => device.MacAddress));
        Assert.True(result.Filters.IsEmpty);
    }

    /// <summary>
    /// Each filter on its own. One host and a loop, because every Theory row used to boot its own host (fourteen of them) to ask one question.
    /// The assertion message names the query, so a failure still says which filter broke.
    /// </summary>
    [Fact]
    public async Task Each_filter_alone_finds_the_right_devices()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        (string Query, int Expected)[] cases =
        [
            ("category=Actuator", 22),
            ("category=PowerConsumption", 12),
            ("category=Environmental", 21),
            ("actuatorKind=Valve", 6),
            ("actuatorKind=Pump", 6),
            ("actuatorKind=Dimmer", 5),
            ("actuatorKind=Relay", 5),
            ("q=pump", 6),
            ("q=B8:27:EB:10:00:2B", 1),
            ("zone=zone-3", 11),
            ("zone=sub-zone-f", 5),
            ("band=Unproven", 55),
            ("band=Fault", 0),
            ("alert=Disconnected", 0),
        ];

        foreach (var (query, expected) in cases)
        {
            var result = await SearchAsync(http, $"?{query}");

            Assert.True(expected == result.Count && expected == result.Devices.Count, $"?{query}: expected {expected}, got {result.Count} ({result.Devices.Count} listed).");
        }
    }

    [Fact]
    public async Task An_unknown_zone_is_a_404()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/devices/search?zone=zone-99")).StatusCode);
    }

    [Fact]
    public async Task A_filter_that_is_not_a_known_value_is_a_400()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        foreach (var query in new[] { "band=Haunted", "category=Toaster", "actuatorKind=Rocket", "alert=Gremlins" })
        {
            var status = (await http.GetAsync($"/api/devices/search?{query}")).StatusCode;

            Assert.True(status == HttpStatusCode.BadRequest, $"?{query}: expected 400, got {(int)status}.");
        }
    }

    [Fact]
    public async Task Filters_are_matched_ignoring_case_in_the_query_string()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(6, (await SearchAsync(http, "?actuatorKind=valve")).Count);
        Assert.Equal(11, (await SearchAsync(http, "?zone=ZONE-3")).Count);
    }

    [Fact]
    public async Task A_blank_filter_is_the_same_as_no_filter()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(DemoDevices.All.Count, (await SearchAsync(http, "?q=%20&zone=")).Count);
    }

    [Fact]
    public async Task A_device_registered_after_start_up_is_searchable_straight_away()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var response = await http.PostAsync("/api/sensors", Registration(
            "AA:BB:CC:DD:EE:77", "zone-1", "Environmental", "°C", 10, 30, "Real", room: "Annexe", nodeId: "N-7"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var found = await SearchAsync(http, "?q=annexe");

        Assert.Equal("AA:BB:CC:DD:EE:77", Assert.Single(found.Devices).MacAddress);
        Assert.Equal(DemoDevices.All.Count + 1, (await SearchAsync(http)).Count);
    }
}
