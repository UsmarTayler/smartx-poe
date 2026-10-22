using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Commands;
using SmartX.DeviceSimulator;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers commands from the operator's request to the simulated device's actuator and back, over HTTP.
/// </summary>
/// <remarks>
/// The real <see cref="DeviceFleet"/> devices are driven one check-in at a time against the real API, so what
/// is asserted is what a device really did: that its actuator moved, that its acknowledgement landed, and that
/// its next telemetry reported the new state. Nothing here calls an API service to make a command happen.
/// </remarks>
public class CommandEndToEndTests
{
    private static string Mac(byte suffix) => $"B8:27:EB:10:00:{suffix:X2}";

    private sealed class Rig : IDisposable
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public required WebApplicationFactory<Program> Api { get; init; }

        public required HttpClient Http { get; init; }

        public required DeviceFleet Fleet { get; init; }

        public static async Task<Rig> CreateAsync(TimeSpan? pendingTimeout = null)
        {
            var (api, http) = await CommandEndpointTests.HostAsync(pendingTimeout);

            var fleet = new DeviceFleet(
                new GatewayClient(http, NullLogger<GatewayClient>.Instance, new DeviceKeyring(TestAuth.FleetSecret)),
                Options.Create(new SimulatorOptions { DeviceCount = 55 }),
                TimeProvider.System,
                new NoopLifetime(),
                NullLogger<DeviceFleet>.Instance);

            await fleet.RegisterFleetForTestAsync();

            return new Rig { Api = api, Http = http, Fleet = fleet };
        }

        public SimulatedDevice Device(byte suffix) => Fleet.DevicesForTest.Single(device => device.MacAddress == Mac(suffix));

        public IActuatorStateStore States => Api.Services.GetRequiredService<IActuatorStateStore>();

        public CommandService Commands => Api.Services.GetRequiredService<CommandService>();

        /// <summary>One check-in and one report for each device, six seconds later than the last round.</summary>
        public async Task Round(params SimulatedDevice[] devices)
        {
            _now += TimeSpan.FromSeconds(6);

            foreach (var device in devices)
            {
                await Fleet.TickDeviceForTestAsync(device, _now, CancellationToken.None);
            }

            await SettleAsync();
        }

        /// <summary>Waits for the ingestion worker to finish everything it has been given.</summary>
        private async Task SettleAsync()
        {
            long last = -1;
            var stable = 0;

            while (stable < 3)
            {
                var stats = (await Http.GetFromJsonAsync<PipelineStats>("/api/pipeline/stats"))!;
                stable = stats.TotalDepth == 0 && stats.TotalProcessed == last ? stable + 1 : 0;
                last = stats.TotalProcessed;
                await Task.Delay(15);
            }
        }

        public async Task<Command> IssueAsync(byte device, CommandAction action, double? level = null)
        {
            var response = await Http.PostAsJsonAsync("/api/commands", new IssueCommandRequest(Mac(device), action, level));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            return (await response.Content.ReadFromJsonAsync<Command>())!;
        }

        public async Task<Command> CommandAsync(Guid id) =>
            (await Http.GetFromJsonAsync<CommandDetail>($"/api/commands/{id}"))!.Command;

        public Task<HttpResponseMessage> SetScenarioAsync(byte device, SimulationScenario scenario) =>
            Http.PostAsJsonAsync($"/api/simulation/{Mac(device)}/scenario", new ScenarioChangeRequest(scenario));

        public void Dispose()
        {
            Http.Dispose();
            Api.Dispose();
        }
    }

    private sealed class NoopLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    [Fact]
    public async Task A_command_runs_the_whole_lifecycle_with_the_simulator_and_the_next_telemetry_reports_the_new_state()
    {
        using var rig = await Rig.CreateAsync();
        var valve = rig.Device(0x2B);

        // The valve reports in, open, so the gateway has observed the state a command will replace.
        await rig.Round(valve);
        Assert.True(rig.States.TryGet(Mac(0x2B), out var before));
        Assert.Equal(1, before.Value);

        var issued = await rig.IssueAsync(0x2B, CommandAction.ValveClose);
        Assert.Equal(CommandState.Pending, issued.State);
        Assert.Equal(1, issued.PreviousValue);

        // The device's next check-in collects it, applies it, and confirms it.
        await rig.Round(valve);

        var finished = await rig.CommandAsync(issued.Id);
        Assert.Equal(CommandState.Acknowledged, finished.State);
        Assert.Equal(0, finished.ResultingValue);
        Assert.Equal(0, valve.ActuatorValue);

        // And its telemetry that round reported the valve closed, as a state packet.
        Assert.True(rig.States.TryGet(Mac(0x2B), out var after));
        Assert.Equal(0, after.Value);

        var history = await rig.Http.GetFromJsonAsync<List<ITelemetryPacket>>($"/api/sensors/{Mac(0x2B)}/telemetry");
        Assert.False(Assert.IsType<StatePacket>(history![^1]).Value);
    }

    [Fact]
    public async Task A_command_delivered_twice_is_applied_once()
    {
        using var rig = await Rig.CreateAsync();
        var valve = rig.Device(0x2B);
        await rig.Round(valve);

        var close = await rig.IssueAsync(0x2B, CommandAction.ValveClose);
        await rig.IssueAsync(0x2B, CommandAction.ValveOpen);

        // Both arrive in one check-in, in order: closed, then open again.
        await rig.Round(valve);
        Assert.Equal(1, valve.ActuatorValue);

        // The close is delivered a second time, as a duplicate on the network could do. Applying it again
        // would shut the valve. The device has already applied that id, so it does nothing.
        var repeat = valve.ApplyCommand(await rig.CommandAsync(close.Id), failureRate: 0, new Random(1));

        Assert.True(repeat.WasRepeat);
        Assert.True(repeat.Success);
        Assert.Equal(1, valve.ActuatorValue);
    }

    [Fact]
    public async Task A_command_to_a_silent_device_times_out_because_nothing_collects_it()
    {
        using var rig = await Rig.CreateAsync(pendingTimeout: TimeSpan.FromMilliseconds(300));
        var silent = rig.Device(0x20);
        Assert.Equal(SimulationScenario.Disconnect, silent.Scenario);

        var issued = await rig.IssueAsync(0x20, CommandAction.RelayOn);

        // The device is switched off. Its harness still polls, but does not collect commands.
        await rig.Round(silent);
        Assert.Equal(CommandState.Pending, (await rig.CommandAsync(issued.Id)).State);
        Assert.Equal(0, silent.ActuatorValue);

        await Task.Delay(500);
        rig.Commands.TimeOutStale();

        var timedOut = await rig.CommandAsync(issued.Id);
        Assert.Equal(CommandState.TimedOut, timedOut.State);
        Assert.Contains("did not collect", timedOut.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silent_device_can_be_switched_back_to_nominal_and_then_takes_commands()
    {
        using var rig = await Rig.CreateAsync();
        var silent = rig.Device(0x20);
        await rig.Round(silent);

        // Silent: the harness's control channel polls, but no telemetry goes out.
        Assert.Empty((await rig.Http.GetFromJsonAsync<List<ITelemetryPacket>>($"/api/sensors/{Mac(0x20)}/telemetry"))!);

        await rig.SetScenarioAsync(0x20, SimulationScenario.Nominal);
        await rig.Round(silent);

        Assert.Equal(SimulationScenario.Nominal, silent.Scenario);
        Assert.NotEmpty((await rig.Http.GetFromJsonAsync<List<ITelemetryPacket>>($"/api/sensors/{Mac(0x20)}/telemetry"))!);

        // Back on line, it collects and applies a command like any other device.
        var issued = await rig.IssueAsync(0x20, CommandAction.RelayOn);
        await rig.Round(silent);

        Assert.Equal(CommandState.Acknowledged, (await rig.CommandAsync(issued.Id)).State);
        Assert.Equal(1, silent.ActuatorValue);
    }

    [Fact]
    public async Task Undoing_an_open_command_on_an_already_open_valve_leaves_it_open()
    {
        using var rig = await Rig.CreateAsync();
        var valve = rig.Device(0x19);
        await rig.Round(valve);
        Assert.Equal(1, valve.ActuatorValue);

        // Open an already-open valve, then undo. The opposite of "open" is "close", and that is not what
        // must happen: the valve was open before, and must still be.
        await rig.IssueAsync(0x19, CommandAction.ValveOpen);
        await rig.Round(valve);

        var undo = await rig.Http.PostAsync("/api/commands/undo", null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);

        await rig.Round(valve);

        Assert.Equal(1, valve.ActuatorValue);
    }

    [Fact]
    public async Task Undoing_a_real_change_puts_the_actuator_back_where_it_was()
    {
        using var rig = await Rig.CreateAsync();
        var lamp = rig.Device(0x2E);
        await rig.Round(lamp);
        Assert.Equal(50, lamp.ActuatorValue);

        await rig.IssueAsync(0x2E, CommandAction.SetLevel, 90);
        await rig.Round(lamp);
        Assert.Equal(90, lamp.ActuatorValue);

        var undo = await (await rig.Http.PostAsync("/api/commands/undo", null)).Content.ReadFromJsonAsync<UndoResult>();
        Assert.Equal(UndoOutcome.Reverted, undo?.Outcome);
        await rig.Round(lamp);

        Assert.Equal(50, lamp.ActuatorValue);

        // The restore was acknowledged like any command, and is not on the stack.
        var restore = await rig.CommandAsync(undo!.Items[0].RestoreCommandId!.Value);
        Assert.Equal(CommandState.Acknowledged, restore.State);
        Assert.Empty((await rig.Http.GetFromJsonAsync<List<UndoStackEntry>>("/api/commands/undo-stack"))!);
    }

    [Fact]
    public async Task Shutting_every_valve_in_a_zone_with_one_silent_reverts_the_three_that_applied()
    {
        using var rig = await Rig.CreateAsync(pendingTimeout: TimeSpan.FromMilliseconds(400));
        var valves = new[] { rig.Device(0x19), rig.Device(0x2B), rig.Device(0x2C), rig.Device(0x2D) };
        var responsive = valves.Where(device => device.MacAddress != Mac(0x2C)).ToArray();

        // All four report, so each has an observed state. Then one goes silent.
        await rig.Round(valves);
        await rig.SetScenarioAsync(0x2C, SimulationScenario.Disconnect);
        await rig.Round(valves);
        Assert.Equal(SimulationScenario.Disconnect, valves[2].Scenario);

        var response = await rig.Http.PostAsJsonAsync("/api/commands/zone", new ZoneCommandRequest("zone-3", CommandAction.ValveClose));
        var composite = (await response.Content.ReadFromJsonAsync<CompositeCommandResult>())!;
        Assert.Equal(4, composite.Commands.Count);

        // Three collect and confirm at once. The silent device's harness only listens for its scenario.
        await rig.Round(valves);
        Assert.All(responsive, device => Assert.Equal(0, device.ActuatorValue));

        await Task.Delay(600);
        rig.Commands.TimeOutStale();

        var status = await rig.Http.GetFromJsonAsync<CompositeCommandResult>($"/api/commands/composite/{composite.CompositeId}");
        Assert.Equal(CompositeStatus.PartiallyApplied, status?.Status);

        // One undo: three restores, one refusal.
        var undoResponse = await rig.Http.PostAsync("/api/commands/undo", null);
        Assert.Equal(HttpStatusCode.OK, undoResponse.StatusCode);
        var undo = (await undoResponse.Content.ReadFromJsonAsync<UndoResult>())!;

        Assert.Equal(UndoOutcome.PartiallyReverted, undo.Outcome);
        Assert.Equal(3, undo.Items.Count(item => item.Result == UndoItemResult.RestoreIssued));
        Assert.Equal(Mac(0x2C), Assert.Single(undo.Items, item => item.Result == UndoItemResult.Refused).MacAddress);

        await rig.Round(valves);

        // The three that were shut are open again. The silent one was never touched.
        Assert.All(responsive, device => Assert.Equal(1, device.ActuatorValue));
        Assert.Equal(1, valves[2].ActuatorValue);
    }

    [Fact]
    public async Task An_actuator_that_fails_to_apply_a_command_reports_failure_and_the_command_is_failed()
    {
        using var rig = await Rig.CreateAsync();
        var valve = rig.Device(0x2B);
        await rig.Round(valve);
        var issued = await rig.IssueAsync(0x2B, CommandAction.ValveClose);

        // The device collects it and the actuator does not respond.
        var command = (await rig.Http.DeviceGetFromJsonAsync<DeviceDesiredState>($"/api/devices/{Mac(0x2B)}/desired", Mac(0x2B)))!.CommandList.Single();
        var outcome = valve.ApplyCommand(command, failureRate: 1, new Random(1));
        Assert.False(outcome.Success);

        foreach (var (id, acknowledgement) in valve.PendingAcks.ToArray())
        {
            Assert.Equal(HttpStatusCode.OK, (await rig.Http.DevicePostAsJsonAsync($"/api/commands/{id}/ack", Mac(0x2B), acknowledgement)).StatusCode);
        }

        var failed = await rig.CommandAsync(issued.Id);
        Assert.Equal(CommandState.Failed, failed.State);
        Assert.Equal("The actuator did not respond.", failed.FailureReason);
        Assert.Equal(1, valve.ActuatorValue);
    }

    [Fact]
    public async Task A_device_refuses_a_command_meant_for_a_different_kind_of_actuator()
    {
        using var rig = await Rig.CreateAsync();
        var valve = rig.Device(0x2B);
        var pumpCommand = new Command(
            Guid.NewGuid(), Mac(0x2B), ActuatorKind.Pump, CommandAction.PumpOn, null, 1, DateTimeOffset.UtcNow,
            CommandState.Delivered, 0, true);

        var outcome = valve.ApplyCommand(pumpCommand, failureRate: 0, new Random(1));

        Assert.False(outcome.Success);
        Assert.Contains("This device is a Valve", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(1, valve.ActuatorValue);
    }

    [Fact]
    public async Task Acknowledgements_that_could_not_be_sent_are_kept_and_sent_again()
    {
        using var rig = await Rig.CreateAsync();
        var valve = rig.Device(0x2B);
        await rig.Round(valve);
        var issued = await rig.IssueAsync(0x2B, CommandAction.ValveClose);

        // The device collects and applies the command but has not yet told the gateway.
        var command = (await rig.Http.DeviceGetFromJsonAsync<DeviceDesiredState>($"/api/devices/{Mac(0x2B)}/desired", Mac(0x2B)))!.CommandList.Single();
        valve.ApplyCommand(command, failureRate: 0, new Random(1));
        Assert.Single(valve.PendingAcks);
        Assert.Equal(CommandState.Delivered, (await rig.CommandAsync(issued.Id)).State);

        // On its next tick it sends the acknowledgement it owed.
        await rig.Round(valve);

        Assert.Empty(valve.PendingAcks);
        Assert.Equal(CommandState.Acknowledged, (await rig.CommandAsync(issued.Id)).State);
    }
}
