using System.Net;
using System.Net.Http.Json;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers registration and lookup, including the status code each outcome produces.
/// </summary>
public class SensorEndpointTests
{
    [Fact]
    public async Task Registering_a_valid_sensor_returns_201_and_its_location()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/sensors", Thermometer10To30());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/sensors/{Thermometer}", response.Headers.Location?.ToString());

        var registered = await response.Content.ReadFromJsonAsync<SensorRegistration>();
        Assert.Equal(Thermometer, registered?.MacAddress);
    }

    [Fact]
    public async Task A_registered_sensor_can_be_fetched_back()
    {
        using var api = New();
        var client = api.CreateClient();
        await client.PostAsync("/api/sensors", Thermometer10To30());

        var sensor = await client.GetFromJsonAsync<SensorRegistration>($"/api/sensors/{Thermometer}");

        Assert.Equal("sub-zone-b", sensor?.Location.ZoneId);
        Assert.Equal(new ValueRange(10, 30), sensor?.ExpectedRange);
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:01")]
    [InlineData("AA-BB-CC-DD-EE-01")]
    public async Task A_sensor_is_found_by_any_spelling_of_its_address(string spelling)
    {
        // The stored key is canonical, so a lookup has to be canonicalised the same way or the same
        // device 404s depending on how the caller typed it.
        using var api = New();
        var client = api.CreateClient();
        await client.PostAsync("/api/sensors", Thermometer10To30());

        var response = await client.GetAsync($"/api/sensors/{spelling}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Registering_the_same_address_twice_returns_409()
    {
        using var api = New();
        var client = api.CreateClient();
        await client.PostAsync("/api/sensors", Thermometer10To30());

        var second = await client.PostAsync("/api/sensors", Thermometer10To30());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_malformed_address_returns_400_naming_the_field()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync(
            "/api/sensors",
            Registration("not-a-mac", "sub-zone-b", "Environmental", "°C", 10, 30, "Real"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("macAddress", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_sensor_rejected_by_an_ancestor_zone_returns_400_listing_every_violation()
    {
        // Sub-Zone B itself is unrestricted. A -50 to 100 °C thermometer breaches Zone 1's 0–50 and
        // Facility A's -40–85, so both levels above it object and both are reported.
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync(
            "/api/sensors",
            Registration(Thermometer, "sub-zone-b", "Environmental", "°C", -50, 100, "Real"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("Zone 1", problem, StringComparison.Ordinal);
        Assert.Contains("Facility A", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_the_zone_forbids_returns_400()
    {
        // Zone 2 permits power metering only.
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync(
            "/api/sensors",
            Registration(Thermometer, "zone-2", "Environmental", "°C", 10, 30, "Real"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registering_into_an_unknown_zone_returns_400()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync(
            "/api/sensors",
            Registration(Thermometer, "no-such-zone", "Environmental", "°C", 10, 30, "Real"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Fetching_an_unregistered_sensor_returns_404()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.GetAsync($"/api/sensors/{Meter}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_sensor_list_starts_empty_and_reflects_registrations()
    {
        using var api = New();
        var client = api.CreateClient();

        Assert.Empty(await client.GetFromJsonAsync<List<SensorRegistration>>("/api/sensors") ?? []);

        await client.PostAsync("/api/sensors", Thermometer10To30());
        await client.PostAsync(
            "/api/sensors",
            Registration(Meter, "zone-2", "PowerConsumption", "kWh", 0, 10000, "Integer"));

        var sensors = await client.GetFromJsonAsync<List<SensorRegistration>>("/api/sensors");

        Assert.Equal([Thermometer, Meter], sensors?.Select(sensor => sensor.MacAddress));
    }
}
