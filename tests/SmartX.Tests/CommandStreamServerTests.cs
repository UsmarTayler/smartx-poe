using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartX.DeviceSimulator;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// The server pieces the Command Stream page needs: the zone action preview, the per-device snapshot, and the
/// simulator's reporting rate.
/// </summary>
public class CommandStreamServerTests
{
    private const string FreezerValve = "B8:27:EB:10:00:19";
    private const string Thermometer1 = "B8:27:EB:10:00:01";

    // ---- zone action preview ------------------------------------------------------------------------------

    [Fact]
    public async Task The_preview_lists_exactly_the_devices_the_real_zone_action_then_reaches()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var preview = (await http.GetFromJsonAsync<ZonePreview>("/api/commands/zone/preview?zoneId=zone-3&action=ValveClose"))!;

        var issued = await http.PostAsync("/api/commands/zone", Json("""{ "zoneId": "zone-3", "action": "ValveClose" }"""));
        var composite = (await issued.Content.ReadFromJsonAsync<CompositeCommandResult>())!;

        // What was shown is what was done, because it is the same selection.
        Assert.Equal(composite.Commands.Select(command => command.MacAddress).Order(), preview.Devices.Select(device => device.MacAddress).Order());
        Assert.Equal(4, preview.Devices.Count);
        Assert.All(preview.Devices, device => Assert.Equal(ActuatorKind.Valve, device.Kind));
        Assert.Equal("Zone 3 – Cold Store", preview.ZoneName);
    }

    [Fact]
    public async Task The_preview_reaches_through_sub_zones_and_says_which_zone_each_device_is_in()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var preview = (await http.GetFromJsonAsync<ZonePreview>("/api/commands/zone/preview?zoneId=zone-3&action=ValveClose"))!;

        // Zone 3 has no valves of its own. They are in the freezer and chiller bays beneath it.
        Assert.DoesNotContain(preview.Devices, device => device.ZoneId == "zone-3");
        Assert.Contains(preview.Devices, device => device.ZoneId == "sub-zone-f");
        Assert.Contains(preview.Devices, device => device.ZoneId == "sub-zone-g");
    }

    [Fact]
    public async Task A_preview_changes_nothing()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.GetAsync("/api/commands/zone/preview?zoneId=zone-3&action=ValveClose");

        Assert.Empty((await http.GetFromJsonAsync<List<Command>>("/api/commands"))!);
        Assert.Empty((await http.GetFromJsonAsync<List<UndoStackEntry>>("/api/commands/undo-stack"))!);
    }

    [Fact]
    public async Task The_preview_refuses_what_the_real_action_refuses_with_the_same_status_codes()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        // Zone 2 is the Energy Centre. No valves.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/commands/zone/preview?zoneId=zone-2&action=ValveClose")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/commands/zone/preview?zoneId=zone-99&action=ValveClose")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/commands/zone/preview?zoneId=zone-3&action=Explode")).StatusCode);

        // A level one device cannot take refuses the whole preview, as it would the whole action.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/commands/zone/preview?zoneId=facility-b&action=SetLevel&level=150")).StatusCode);
    }

    [Fact]
    public async Task The_preview_accepts_the_action_in_any_case()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        var response = await http.GetAsync("/api/commands/zone/preview?zoneId=zone-3&action=valveclose");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_preview_shows_each_devices_last_observed_state()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.PostAsync("/api/telemetry", Reading(FreezerValve, "true"));
        await DrainAsync(http, 1);

        var preview = (await http.GetFromJsonAsync<ZonePreview>("/api/commands/zone/preview?zoneId=zone-3&action=ValveClose"))!;

        Assert.Equal(1, preview.Devices.Single(device => device.MacAddress == FreezerValve).CurrentValue);
        Assert.Null(preview.Devices.First(device => device.MacAddress != FreezerValve).CurrentValue);
    }

    // ---- the device snapshot ------------------------------------------------------------------------------

    [Fact]
    public async Task A_devices_snapshot_has_its_recent_readings_oldest_first_and_the_topics_sequence()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        foreach (var value in new[] { "20", "21", "22" })
        {
            await http.PostAsync("/api/telemetry", Reading(Thermometer1, value));
        }

        await DrainAsync(http, 3);

        var snapshot = (await http.GetFromJsonAsync<DeviceSnapshot>($"/api/realtime/device/{Thermometer1}"))!;

        Assert.Equal(Thermometer1, snapshot.MacAddress);
        Assert.Equal([20d, 21d, 22d], snapshot.Readings.Select(reading => reading.Value));
        Assert.All(snapshot.Readings, reading => Assert.False(reading.IsState));

        // Nobody has joined the device's topic, so nothing has been numbered on it.
        Assert.Equal(0, snapshot.Sequence);
    }

    [Fact]
    public async Task A_state_devices_snapshot_marks_its_readings_as_states()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        await http.PostAsync("/api/telemetry", Reading(FreezerValve, "true"));
        await DrainAsync(http, 1);

        var snapshot = (await http.GetFromJsonAsync<DeviceSnapshot>($"/api/realtime/device/{FreezerValve}"))!;

        var reading = Assert.Single(snapshot.Readings);
        Assert.True(reading.IsState);
        Assert.Equal(1, reading.Value);
    }

    [Fact]
    public async Task A_snapshot_for_an_unregistered_device_is_a_404()
    {
        var (api, http) = await CommandEndpointTests.HostAsync();
        using var _ = api;

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/realtime/device/AA:BB:CC:DD:EE:99")).StatusCode);
    }

    // ---- the simulator's reporting rate -------------------------------------------------------------------

    [Fact]
    public async Task The_reporting_rate_multiplier_divides_each_devices_reporting_interval()
    {
        // One gateway for the three multipliers (each Theory row used to start its own); each gets a fleet of its own.
        using var api = CommandEndpointTests_Demo();
        var gateway = new GatewayClient(api.CreateClient(), NullLogger<GatewayClient>.Instance, new DeviceKeyring(TestAuth.FleetSecret));

        foreach (var (multiplier, expectedMilliseconds) in new[] { (1d, 4000d), (10d, 400d), (40d, 100d) })
        {
            var fleet = new DeviceFleet(
                gateway,
                Microsoft.Extensions.Options.Options.Create(new SimulatorOptions { DeviceCount = 13, ReportingRateMultiplier = multiplier }),
                TimeProvider.System,
                new NoopLifetime(),
                NullLogger<DeviceFleet>.Instance);

            await fleet.RegisterFleetForTestAsync();

            // A nominal thermometer with a five-second heartbeat reports every four seconds at the normal pace.
            var device = fleet.DevicesForTest.First(candidate => candidate.Scenario == SimulationScenario.Nominal && candidate.Registration.HeartbeatIntervalSeconds == 5);
            var now = DateTimeOffset.UtcNow;
            device.NextDue = now;

            await fleet.TickDeviceForTestAsync(device, now, CancellationToken.None);

            Assert.True(
                Math.Abs(expectedMilliseconds - (device.NextDue - now).TotalMilliseconds) < 0.5,
                $"x{multiplier}: expected {expectedMilliseconds} ms, got {(device.NextDue - now).TotalMilliseconds} ms.");
        }
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CommandEndpointTests_Demo() =>
        new AuthenticatedFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Zones:Hierarchy"] = "Demo" })));

    private sealed class NoopLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
