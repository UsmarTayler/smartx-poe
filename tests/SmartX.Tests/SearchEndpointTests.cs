using System.Net;
using System.Net.Http.Json;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Device search over HTTP: each filter, combinations, zones that include their sub-zones, and what is refused.
/// </summary>
public class SearchEndpointTests
{
    private const string Freezer1 = "B8:27:EB:10:00:17";   // environmental, Sub-Zone F, so under Zone 3
    private const string Chiller1 = "B8:27:EB:10:00:1A";   // environmental, Sub-Zone G, so under Zone 3
    private const string DockApron = "B8:27:EB:10:00:1D";  // environmental, Zone 4, so under Facility B
    private const string FreezerValve = "B8:27:EB:10:00:19";

    private static async Task<DeviceSearchResponse> SearchAsync(HttpClient http, string query = "") =>
        (await http.GetFromJsonAsync<DeviceSearchResponse>($"/api/devices/search{query}"))!;

    /// <summary>Drives a device into the fault band the way a real one gets there: by sending a bad reading.</summary>
    private static async Task FaultAsync(HttpClient http, params string[] macs)
    {
        foreach (var mac in macs)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/telemetry", Reading(mac, "99"))).StatusCode);
        }
    }

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

    [Theory]
    [InlineData("category=Actuator", 18)]
    [InlineData("category=PowerConsumption", 12)]
    [InlineData("category=Environmental", 18)]
    [InlineData("actuatorKind=Valve", 5)]
    [InlineData("actuatorKind=Pump", 4)]
    [InlineData("actuatorKind=Dimmer", 4)]
    [InlineData("actuatorKind=Relay", 5)]
    [InlineData("q=pump", 4)]
    [InlineData("q=B8:27:EB:10:00:2B", 1)]
    [InlineData("zone=zone-3", 11)]
    [InlineData("zone=sub-zone-f", 5)]
    [InlineData("band=Unproven", 48)]
    [InlineData("band=Fault", 0)]
    [InlineData("alert=Disconnected", 0)]
    public async Task Each_filter_alone_finds_the_right_devices(string query, int expected)
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var result = await SearchAsync(http, $"?{query}");

        Assert.Equal(expected, result.Count);
        Assert.Equal(expected, result.Devices.Count);
    }

    [Fact]
    public async Task The_zone_filter_includes_every_sub_zone_beneath_the_zone()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var result = await SearchAsync(http, "?zone=zone-3");

        // Zone 3 has no devices of its own. Everything is in the freezer and chiller bays below it.
        Assert.DoesNotContain(result.Devices, device => device.ZoneId == "zone-3");
        Assert.Contains(result.Devices, device => device.ZoneId == "sub-zone-f");
        Assert.Contains(result.Devices, device => device.ZoneId == "sub-zone-g");

        // And a facility reaches two levels down.
        var facility = await SearchAsync(http, "?zone=facility-a");
        Assert.Contains(facility.Devices, device => device.ZoneId == "sub-zone-b");
        Assert.DoesNotContain(facility.Devices, device => device.ZoneId == "zone-4");
    }

    [Fact]
    public async Task Faulted_devices_are_found_by_band_and_by_alert()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;
        await FaultAsync(http, Freezer1, Chiller1, DockApron);

        var byBand = await SearchAsync(http, "?band=Fault");
        var byAlert = await SearchAsync(http, "?alert=Fault");
        var byRange = await SearchAsync(http, "?alert=OutOfRange");

        string[] expected = [Freezer1, Chiller1, DockApron];
        Assert.Equal(expected, byBand.Devices.Select(device => device.MacAddress));
        Assert.Equal(expected, byAlert.Devices.Select(device => device.MacAddress));
        Assert.Equal(expected, byRange.Devices.Select(device => device.MacAddress));

        // Each hit says what is wrong with it.
        var hit = byBand.Devices[0];
        Assert.Equal(TrustBand.Fault, hit.Band);
        Assert.Equal([AlertKind.OutOfRange, AlertKind.Fault], hit.ActiveAlerts);
    }

    [Fact]
    public async Task Category_band_and_zone_combine_to_return_exactly_the_matching_devices()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        // Two environmental sensors under Zone 3, one under Facility B, all faulted; and a valve under Zone 3,
        // faulted, which is not environmental.
        await FaultAsync(http, Freezer1, Chiller1, DockApron);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/telemetry", Reading(FreezerValve, "5"))).StatusCode);

        var found = await SearchAsync(http, "?category=Environmental&band=Fault&zone=zone-3");

        Assert.Equal([Freezer1, Chiller1], found.Devices.Select(device => device.MacAddress));

        // Take the zone away and the dock sensor joins; take the category away and the valve does.
        Assert.Equal(3, (await SearchAsync(http, "?category=Environmental&band=Fault")).Count);
        Assert.Equal(3, (await SearchAsync(http, "?band=Fault&zone=zone-3")).Count);
    }

    [Fact]
    public async Task Text_is_matched_against_whatever_survived_the_other_filters()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var found = await SearchAsync(http, "?q=valve&zone=sub-zone-g");

        // Chiller Valve A and B, and not the Freezer or Dock valves that also say "valve".
        Assert.Equal(2, found.Count);
        Assert.All(found.Devices, device => Assert.Equal("sub-zone-g", device.ZoneId));
    }

    [Fact]
    public async Task A_device_leaves_a_result_when_it_recovers()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;
        await FaultAsync(http, Freezer1);
        Assert.Equal(1, (await SearchAsync(http, "?band=Fault")).Count);

        // Plenty of good readings: trust recovers out of fault.
        for (var i = 0; i < 40; i++)
        {
            await http.PostAsync("/api/telemetry", Reading(Freezer1, "-20"));
        }

        await DrainAsync(http, 40);

        Assert.Equal(0, (await SearchAsync(http, "?band=Fault")).Count);
        Assert.Equal(0, (await SearchAsync(http, "?alert=Fault")).Count);
    }

    [Fact]
    public async Task An_unknown_zone_is_a_404()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/devices/search?zone=zone-99")).StatusCode);
    }

    [Theory]
    [InlineData("band=Haunted")]
    [InlineData("category=Toaster")]
    [InlineData("actuatorKind=Rocket")]
    [InlineData("alert=Gremlins")]
    public async Task A_filter_that_is_not_a_known_value_is_a_400(string query)
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"/api/devices/search?{query}")).StatusCode);
    }

    [Fact]
    public async Task Filters_are_matched_ignoring_case_in_the_query_string()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(5, (await SearchAsync(http, "?actuatorKind=valve")).Count);
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
