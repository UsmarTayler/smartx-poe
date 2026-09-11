using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the wording of rejection reasons, which the UI shows verbatim after "21.5 rejected:".
/// </summary>
/// <remarks>
/// These pin exact strings on purpose. The reason is user-facing text that has to read as the end of
/// a sentence, and a change to it is a change to what an operator sees, so it should fail a test
/// rather than slip through.
/// </remarks>
public class RejectionReasonTests
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

    private static async Task<string> ReasonForAsync(HttpClient client, string mac, string rawValue)
    {
        var response = await client.PostAsync("/api/telemetry", Reading(mac, rawValue));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();

        return Assert.Single(problem!.Errors["Value"]);
    }

    [Fact]
    public async Task A_fractional_reading_to_an_integer_sensor_says_whole_numbers_are_required()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        Assert.Equal(
            "sensor is registered as Integer, so readings must be whole numbers",
            await ReasonForAsync(client, Meter, "21.5"));
    }

    [Fact]
    public async Task Text_sent_to_a_numeric_sensor_names_what_was_received()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        Assert.Equal(
            "sensor is registered as Real, so readings must be numbers, not text",
            await ReasonForAsync(client, Thermometer, "\"warm\""));
    }

    [Fact]
    public async Task A_number_sent_to_an_actuator_says_it_must_be_on_or_off()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        Assert.Equal(
            "sensor is registered as State, so readings must be true or false, not a number",
            await ReasonForAsync(client, Switch, "1"));
    }

    [Fact]
    public async Task An_out_of_range_reading_names_the_range_and_unit()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        Assert.Equal("outside the expected range 10–30 °C", await ReasonForAsync(client, Thermometer, "40"));
    }

    [Fact]
    public async Task Batch_errors_prefix_the_same_reason_with_the_reading_position()
    {
        using var api = New();
        var client = await WithSensorsAsync(api);

        var response = await client.PostAsync("/api/telemetry/batch",
            Json($$"""{ "macAddress": "{{Thermometer}}", "windows": [[40]] }"""));
        var result = await response.Content.ReadFromJsonAsync<SmartX.Shared.TelemetryBatchResult>();

        Assert.Equal("Reading 0: outside the expected range 10–30 °C", Assert.Single(result!.Errors));
    }
}
