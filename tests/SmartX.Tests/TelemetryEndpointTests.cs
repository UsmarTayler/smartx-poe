using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the routing of a raw JSON value to the right packet type, and the checks a reading has to
/// pass before it is accepted.
/// </summary>
/// <remarks>
/// The interesting assertions here are on what the response body deserialises <i>into</i>. A bare
/// JSON number could have become either numeric packet type, so a test that only checked the status
/// code would pass whichever one the endpoint picked.
/// </remarks>
public class TelemetryEndpointTests
{
    private static async Task<HttpClient> WithSensorsAsync(WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();

        await client.PostAsync("/api/sensors", Thermometer10To30());
        await client.PostAsync("/api/sensors",
            Registration(Meter, "zone-2", "PowerConsumption", "kWh", 0, 10000, "Integer"));
        await client.PostAsync("/api/sensors",
            Registration(Switch, "sub-zone-b", "Actuator", "state", 0, 1, "State"));

        return client;
    }

    /// <summary>
    /// Sends one reading, waits for the ingestion worker, and returns the packet as it was stored.
    /// </summary>
    /// <remarks>
    /// The API answers with a receipt as soon as the reading is validated and stores it just afterwards, so
    /// the packet a reading became is read back from history rather than from the response. That is also the
    /// stronger assertion: it checks what the worker actually retained, not what the endpoint said it would.
    /// </remarks>
    private static async Task<TPacket> SendAndReadBackAsync<TPacket>(HttpClient client, string mac, string rawValue)
        where TPacket : class, ITelemetryPacket
    {
        var response = await client.PostAsync("/api/telemetry", Reading(mac, rawValue));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var receipt = await response.Content.ReadFromJsonAsync<TelemetryReceipt>();
        Assert.Equal("standard", receipt?.Lane);

        await DrainAsync(client, expectedProcessed: 1);

        var history = await client.GetFromJsonAsync<List<ITelemetryPacket>>($"/api/sensors/{mac}/telemetry");

        return Assert.IsType<TPacket>(Assert.Single(history!));
    }

    [Fact]
    public async Task A_fractional_reading_becomes_a_float_packet()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var packet = await SendAndReadBackAsync<NumericPacket<float>>(client, Thermometer, "21.5");

        Assert.Equal(21.5f, packet.Value);
        Assert.Equal(SensorCategory.Environmental, packet.Category);
        Assert.Equal(Thermometer, packet.NodeId);
    }

    [Fact]
    public async Task A_whole_reading_from_an_integer_sensor_becomes_an_int_packet()
    {
        // The same bare JSON number shape as above. What separates the two is the registration,
        // not the payload.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var packet = await SendAndReadBackAsync<NumericPacket<int>>(client, Meter, "1200");

        Assert.Equal(1200, packet.Value);
        Assert.Equal("kWh", packet.Unit);
    }

    [Fact]
    public async Task A_whole_number_sent_to_a_real_sensor_is_still_a_float_packet()
    {
        // 21 is a perfectly good int on the wire. The thermometer is registered as Real, so it
        // becomes a float packet — which is the behaviour that would be impossible to guarantee if
        // the type were inferred from the JSON.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var packet = await SendAndReadBackAsync<NumericPacket<float>>(client, Thermometer, "21");

        Assert.Equal(21f, packet.Value);
    }

    [Fact]
    public async Task A_boolean_reading_becomes_a_state_packet()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var packet = await SendAndReadBackAsync<StatePacket>(client, Switch, "true");

        Assert.True(packet.Value);
        Assert.Equal(SensorCategory.Actuator, packet.Category);
    }

    [Fact]
    public async Task A_fractional_reading_from_an_integer_sensor_is_rejected()
    {
        // Truncating 21.5 to 21 would be a silent data loss on a meter reading.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry", Reading(Meter, "21.5"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("\"warm\"")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task A_payload_of_the_wrong_shape_is_rejected(string rawValue)
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry", Reading(Thermometer, rawValue));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_number_sent_to_a_state_sensor_is_rejected()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry", Reading(Switch, "1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("40.0")]
    [InlineData("-5.0")]
    public async Task A_reading_outside_the_registered_range_is_rejected(string rawValue)
    {
        // The thermometer promised 10–30 °C at registration. A reading beyond that is either a
        // faulty device or a misconfigured one, and neither should quietly enter the record.
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry", Reading(Thermometer, rawValue));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("expected range", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Telemetry_from_an_unregistered_sensor_returns_404()
    {
        // Without a registration there is no unit, category or range, so there is nothing to check
        // the reading against.
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/telemetry", Reading(Thermometer, "21.5"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_reading_at_the_boundary_of_the_range_is_accepted()
    {
        // The range is inclusive at both ends, so this is the case most likely to be off by one.
        using var api = New();
        var client = await WithSensorsAsync(api);

        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsync("/api/telemetry", Reading(Thermometer, "10"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsync("/api/telemetry", Reading(Thermometer, "30"))).StatusCode);
    }
}
