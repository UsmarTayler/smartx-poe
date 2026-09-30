using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartX.Api.Commands;
using SmartX.DeviceSimulator;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the command endpoints: status codes, what the device's check-in returns, and undo responses.
/// </summary>
public class CommandEndpointTests
{
    private const string Valve = "B8:27:EB:10:00:2B";
    private const string Lamp = "B8:27:EB:10:00:2F";
    private const string Pump = "B8:27:EB:10:00:29";
    private const string Thermometer = "B8:27:EB:10:00:01";

    /// <summary>An API on the demo hierarchy with the whole demo fleet registered, over HTTP like a real one.</summary>
    internal static async Task<(WebApplicationFactory<Program> Api, HttpClient Http)> HostAsync(
        TimeSpan? pendingTimeout = null, TimeSpan? deliveredTimeout = null)
    {
        var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Zones:Hierarchy"] = "Demo",
                    ["Commands:PendingTimeout"] = (pendingTimeout ?? TimeSpan.FromSeconds(30)).ToString("c"),
                    ["Commands:DeliveredTimeout"] = (deliveredTimeout ?? TimeSpan.FromSeconds(30)).ToString("c"),
                })));

        var http = api.CreateClient();
        var gateway = new GatewayClient(http, NullLogger<GatewayClient>.Instance);

        foreach (var device in DemoDevices.All)
        {
            Assert.True(await gateway.RegisterAsync(device.Registration, CancellationToken.None));
        }

        return (api, http);
    }

    private static StringContent Issue(string mac, string action, double? level = null) =>
        Json($$"""{ "macAddress": "{{mac}}", "action": "{{action}}"{{(level is null ? "" : $", \"level\": {level}")}} }""");

    private static async Task<Command> IssuedAsync(HttpClient http, string mac, string action, double? level = null)
    {
        var response = await http.PostAsync("/api/commands", Issue(mac, action, level));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<Command>())!;
    }

    private static Task<HttpResponseMessage> DeviceChecksIn(HttpClient http, string mac, bool deliver = true) =>
        http.GetAsync($"/api/devices/{mac}/desired?deliver={(deliver ? "true" : "false")}");

    // ---- issuing ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Issuing_a_command_answers_202_with_the_pending_command_and_where_to_find_it()
    {
        var (api, http) = await HostAsync();
        using var _ = api;

        var response = await http.PostAsync("/api/commands", Issue(Valve, "ValveClose"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var command = await response.Content.ReadFromJsonAsync<Command>();
        Assert.Equal(CommandState.Pending, command?.State);
        Assert.Equal(ActuatorKind.Valve, command?.Kind);
        Assert.Equal(0, command?.TargetValue);
        Assert.Equal($"/api/commands/{command!.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_command_for_an_unregistered_device_is_a_404()
    {
        var (api, http) = await HostAsync();
        using var _ = api;

        var response = await http.PostAsync("/api/commands", Issue("AA:BB:CC:DD:EE:99", "ValveClose"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(Valve, "PumpOn", "PumpOn is a Pump command, and this device is a Valve")]
    [InlineData(Pump, "ValveOpen", "ValveOpen is a Valve command, and this device is a Pump")]
    [InlineData(Thermometer, "PumpOn", "not an actuator")]
    public async Task A_mismatched_command_is_a_400_with_a_clear_reason(string mac, string action, string reason)
    {
        var (api, http) = await HostAsync();
        using var _ = api;

        var response = await http.PostAsync("/api/commands", Issue(mac, action));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty((await http.GetFromJsonAsync<List<Command>>("/api/commands"))!);
    }

    [Fact]
    public async Task An_unknown_action_is_a_400_that_names_the_choices()
    {
        var (api, http) = await HostAsync();
        using var _ = api;

        var response = await http.PostAsync("/api/commands", Json($$"""{ "macAddress": "{{Valve}}", "action": 99 }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ValveClose", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ---- the device's check-in ----------------------------------------------------------------------------

    [Fact]
    public async Task The_check_in_carries_the_pending_command_and_marks_it_delivered()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        var command = await IssuedAsync(http, Valve, "ValveClose");

        var checkIn = await DeviceChecksIn(http, Valve);

        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        var desired = await checkIn.Content.ReadFromJsonAsync<DeviceDesiredState>();
        Assert.Equal(command.Id, Assert.Single(desired!.CommandList).Id);

        // No scenario was ever set for this device, so the response says so rather than inventing one.
        Assert.Null(desired.Scenario);
        Assert.Equal(0, desired.Version);

        Assert.Equal(CommandState.Delivered, (await http.GetFromJsonAsync<CommandDetail>($"/api/commands/{command.Id}"))?.Command.State);
    }

    [Fact]
    public async Task A_second_check_in_before_the_acknowledgement_gets_no_commands()
    {
        // The API half of "never apply a command twice": a delivered command is never handed over again.
        var (api, http) = await HostAsync();
        using var _ = api;
        await IssuedAsync(http, Valve, "ValveClose");

        Assert.Equal(HttpStatusCode.OK, (await DeviceChecksIn(http, Valve)).StatusCode);

        // Nothing waiting and no scenario set: the device is told to carry on.
        Assert.Equal(HttpStatusCode.NotFound, (await DeviceChecksIn(http, Valve)).StatusCode);
    }

    [Fact]
    public async Task Commands_and_a_desired_scenario_arrive_together_in_one_response()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        await http.PostAsync($"/api/simulation/{Valve}/scenario", Json("""{ "scenario": "Nominal" }"""));
        var command = await IssuedAsync(http, Valve, "ValveOpen");

        var desired = await (await DeviceChecksIn(http, Valve)).Content.ReadFromJsonAsync<DeviceDesiredState>();

        Assert.Equal(SimulationScenario.Nominal, desired?.Scenario);
        Assert.Equal(command.Id, Assert.Single(desired!.CommandList).Id);
    }

    [Fact]
    public async Task Looking_without_delivering_leaves_the_command_pending()
    {
        // The harness's control-plane read for a device it has switched off: it hears its instructions but
        // collects nothing, so the commands stay pending and time out.
        var (api, http) = await HostAsync();
        using var _ = api;
        var command = await IssuedAsync(http, Valve, "ValveClose");
        await http.PostAsync($"/api/simulation/{Valve}/scenario", Json("""{ "scenario": "Disconnect" }"""));

        var peek = await (await DeviceChecksIn(http, Valve, deliver: false)).Content.ReadFromJsonAsync<DeviceDesiredState>();

        Assert.Equal(SimulationScenario.Disconnect, peek?.Scenario);
        Assert.Empty(peek!.CommandList);
        Assert.Equal(CommandState.Pending, (await http.GetFromJsonAsync<CommandDetail>($"/api/commands/{command.Id}"))?.Command.State);
    }

    // ---- acknowledgement ----------------------------------------------------------------------------------

    [Fact]
    public async Task Acknowledging_a_delivered_command_answers_200_and_a_repeat_is_still_200()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        var command = await IssuedAsync(http, Valve, "ValveClose");
        await DeviceChecksIn(http, Valve);

        var ack = Json("""{ "success": true, "resultingValue": 0 }""");
        var first = await http.PostAsync($"/api/commands/{command.Id}/ack", ack);
        var second = await http.PostAsync($"/api/commands/{command.Id}/ack", Json("""{ "success": true, "resultingValue": 0 }"""));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(CommandState.Acknowledged, (await first.Content.ReadFromJsonAsync<Command>())?.State);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Acknowledging_a_command_that_was_not_delivered_is_a_409_and_an_unknown_one_a_404()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        var command = await IssuedAsync(http, Valve, "ValveClose");

        var early = await http.PostAsync($"/api/commands/{command.Id}/ack", Json("""{ "success": true }"""));
        var unknown = await http.PostAsync($"/api/commands/{Guid.NewGuid()}/ack", Json("""{ "success": true }"""));

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("not been delivered", await early.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---- reading the log ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_command_list_is_newest_first_and_filters_by_device_and_state()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        var first = await IssuedAsync(http, Valve, "ValveClose");
        var second = await IssuedAsync(http, Pump, "PumpOn");
        var third = await IssuedAsync(http, Valve, "ValveOpen");
        await DeviceChecksIn(http, Pump);

        var all = await http.GetFromJsonAsync<List<Command>>("/api/commands");
        Assert.Equal([third.Id, second.Id, first.Id], all!.Select(command => command.Id));

        var forValve = await http.GetFromJsonAsync<List<Command>>($"/api/commands?mac={Valve}");
        Assert.Equal([third.Id, first.Id], forValve!.Select(command => command.Id));

        var delivered = await http.GetFromJsonAsync<List<Command>>("/api/commands?state=Delivered");
        Assert.Equal(second.Id, Assert.Single(delivered!).Id);

        Assert.Single((await http.GetFromJsonAsync<List<Command>>("/api/commands?limit=1"))!);
    }

    [Fact]
    public async Task A_commands_detail_includes_its_history_and_an_unknown_id_is_a_404()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        var command = await IssuedAsync(http, Valve, "ValveClose");
        await DeviceChecksIn(http, Valve);
        await http.PostAsync($"/api/commands/{command.Id}/ack", Json("""{ "success": true, "resultingValue": 0 }"""));

        var detail = await http.GetFromJsonAsync<CommandDetail>($"/api/commands/{command.Id}");

        Assert.Equal(
            [CommandEvent.Issued, CommandEvent.Delivered, CommandEvent.Acknowledged],
            detail!.Events.Select(entry => entry.Event));
        Assert.All(detail.Events, entry => Assert.NotNull(entry.TrustBand));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/commands/{Guid.NewGuid()}")).StatusCode);
    }

    // ---- undo ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Undo_with_nothing_to_undo_is_a_404_that_says_so()
    {
        var (api, http) = await HostAsync();
        using var _ = api;

        var response = await http.PostAsync("/api/commands/undo", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(UndoOutcome.NothingToUndo, (await response.Content.ReadFromJsonAsync<UndoResult>())?.Outcome);
    }

    [Fact]
    public async Task Undo_of_a_pending_command_is_a_200_that_says_it_was_withdrawn()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        var command = await IssuedAsync(http, Valve, "ValveClose");

        var response = await http.PostAsync("/api/commands/undo", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UndoResult>();
        Assert.Equal(UndoOutcome.Reverted, result?.Outcome);
        Assert.Equal(UndoItemResult.Cancelled, Assert.Single(result!.Items).Result);
        Assert.Equal(command.Id, result.Items[0].CommandId);
        Assert.Equal(HttpStatusCode.NotFound, (await DeviceChecksIn(http, Valve)).StatusCode);
    }

    [Fact]
    public async Task A_refused_undo_is_a_409_with_the_reason_and_never_a_500()
    {
        var (api, http) = await HostAsync(pendingTimeout: TimeSpan.FromMilliseconds(100));
        using var _ = api;
        await IssuedAsync(http, Valve, "ValveClose");
        await Task.Delay(300);
        api.Services.GetRequiredService<CommandService>().TimeOutStale();

        var response = await http.PostAsync("/api/commands/undo", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UndoResult>();
        Assert.Equal(UndoOutcome.Refused, result?.Outcome);
        Assert.Contains("real state is unknown", Assert.Single(result!.Items).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_undo_stack_lists_the_most_recent_action_first()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        var first = await IssuedAsync(http, Valve, "ValveClose");
        var second = await IssuedAsync(http, Lamp, "SetLevel", 40);

        var stack = await http.GetFromJsonAsync<List<UndoStackEntry>>("/api/commands/undo-stack");

        Assert.Equal([second.Id, first.Id], stack!.Select(entry => entry.Commands[0].Id));
        Assert.Contains("SetLevel to 40", stack![0].Description, StringComparison.Ordinal);
    }

    // ---- zone-wide ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_zone_command_answers_202_with_one_command_per_matching_valve()
    {
        var (api, http) = await HostAsync();
        using var _ = api;

        var response = await http.PostAsync("/api/commands/zone", Json("""{ "zoneId": "zone-3", "action": "ValveClose" }"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CompositeCommandResult>();
        Assert.Equal(4, result?.Commands.Count);
        Assert.Equal(CompositeStatus.InProgress, result?.Status);
        Assert.Single((await http.GetFromJsonAsync<List<UndoStackEntry>>("/api/commands/undo-stack"))!);

        var composite = await http.GetFromJsonAsync<CompositeCommandResult>($"/api/commands/composite/{result!.CompositeId}");
        Assert.Equal(4, composite?.Commands.Count);
    }

    [Fact]
    public async Task A_zone_command_for_an_unknown_zone_is_a_404_and_for_nothing_to_act_on_a_400()
    {
        var (api, http) = await HostAsync();
        using var _ = api;

        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsync("/api/commands/zone", Json("""{ "zoneId": "zone-99", "action": "ValveClose" }"""))).StatusCode);

        var none = await http.PostAsync("/api/commands/zone", Json("""{ "zoneId": "zone-2", "action": "ValveClose" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("no Valve", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_can_be_read_across_commands_and_narrowed_to_one_device()
    {
        var (api, http) = await HostAsync();
        using var _ = api;
        await IssuedAsync(http, Valve, "ValveClose");
        await IssuedAsync(http, Pump, "PumpOn");

        var all = await http.GetFromJsonAsync<List<CommandLogEntry>>("/api/commands/log");
        var forPump = await http.GetFromJsonAsync<List<CommandLogEntry>>($"/api/commands/log?mac={Pump}");

        Assert.Equal(2, all?.Count);
        Assert.Equal(CommandEvent.Issued, Assert.Single(forPump!).Event);
        Assert.True(all![0].Sequence > all[1].Sequence);
    }
}
