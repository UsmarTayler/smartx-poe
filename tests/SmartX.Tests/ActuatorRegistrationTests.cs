using System.Net;
using System.Net.Http.Json;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers registering actuators with a kind, and that Part 1 registration is unaffected.
/// </summary>
public class ActuatorRegistrationTests
{
    private const string Valve = "AA:BB:CC:DD:EE:11";

    private static StringContent Actuator(string mac, string payload, string? kind, string category = "Actuator", string unit = "state") =>
        Json($$"""
        {
          "macAddress": "{{mac}}", "room": "Plant Room", "zoneId": "sub-zone-b", "nodeId": "NODE-1",
          "category": "{{category}}", "unit": "{{unit}}", "minimum": 0, "maximum": {{(payload == "State" ? 1 : 100)}},
          "payloadKind": "{{payload}}"{{(kind is null ? "" : $$""", "actuatorKind": "{{kind}}" """)}}
        }
        """);

    [Fact]
    public async Task Part_1_registration_without_an_actuator_kind_still_works_for_every_category()
    {
        using var api = New();
        var client = api.CreateClient();

        // The exact requests Part 1 sent: none of them mentions an actuator kind.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/sensors", Thermometer10To30())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/sensors",
            Registration(Meter, "zone-2", "PowerConsumption", "kWh", 0, 10000, "Integer"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/sensors",
            Registration(Switch, "sub-zone-b", "Actuator", "state", 0, 1, "State"))).StatusCode);

        var sensors = await client.GetFromJsonAsync<List<SensorRegistration>>("/api/sensors");

        Assert.All(sensors!, sensor => Assert.Null(sensor.ActuatorKind));
    }

    [Theory]
    [InlineData("Pump", "State")]
    [InlineData("Relay", "State")]
    [InlineData("Valve", "State")]
    [InlineData("Dimmer", "Integer")]
    [InlineData("Dimmer", "Real")]
    public async Task An_actuator_registered_with_a_kind_keeps_it(string kind, string payload)
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/sensors", Actuator(Valve, payload, kind));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await client.GetFromJsonAsync<SensorRegistration>($"/api/sensors/{Valve}");
        Assert.Equal(Enum.Parse<ActuatorKind>(kind), stored?.ActuatorKind);
    }

    [Fact]
    public async Task An_actuator_kind_on_a_sensor_that_is_not_an_actuator_is_ignored()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/sensors", Actuator(Valve, "Real", "Pump", category: "Environmental", unit: "%RH"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await client.GetFromJsonAsync<SensorRegistration>($"/api/sensors/{Valve}"))?.ActuatorKind);
    }

    [Theory]
    [InlineData("Dimmer", "State", "Dimmer reports a level")]
    [InlineData("Valve", "Real", "either on or off")]
    [InlineData("Pump", "Integer", "either on or off")]
    public async Task A_kind_that_does_not_suit_the_payload_is_rejected_with_a_reason(string kind, string payload, string reason)
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/sensors", Actuator(Valve, payload, kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_actuator_kind_is_rejected()
    {
        using var api = New();
        var client = api.CreateClient();

        var response = await client.PostAsync("/api/sensors", Actuator(Valve, "State", "Toaster"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void The_form_rule_insists_on_a_kind_for_an_actuator_but_the_api_rule_does_not()
    {
        Assert.NotNull(SensorRegistrationRules.ActuatorKind(null, SensorCategory.Actuator, TelemetryPayloadKind.State, required: true));
        Assert.Null(SensorRegistrationRules.ActuatorKind(null, SensorCategory.Actuator, TelemetryPayloadKind.State, required: false));
        Assert.Null(SensorRegistrationRules.ActuatorKind(null, SensorCategory.Environmental, TelemetryPayloadKind.Real, required: true));
    }

    [Fact]
    public void The_demo_fleet_has_several_actuators_of_every_kind_spread_across_zones()
    {
        var actuators = DemoDevices.All.Where(device => device.Registration.Category == SensorCategory.Actuator).ToList();

        foreach (var kind in Enum.GetValues<ActuatorKind>())
        {
            var ofKind = actuators.Where(device => device.Registration.ActuatorKind == kind).ToList();

            Assert.True(ofKind.Count >= 3, $"Only {ofKind.Count} {kind} devices.");
            Assert.True(ofKind.Select(device => device.Registration.ZoneId).Distinct().Count() >= 2, $"{kind} devices share one zone.");
        }

        // No actuator is left without a kind, or it could never be commanded.
        Assert.All(actuators, device => Assert.NotNull(device.Registration.ActuatorKind));
    }
}
