using Microsoft.Extensions.Options;
using SmartX.Api.Commands;
using SmartX.Api.Storage;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers zone-wide commands: "shut every valve under this zone" as one operator action.
/// </summary>
/// <remarks>
/// Uses the real demo fleet and the real demo zone hierarchy, so the zone walk is exercised over a tree that
/// is three levels deep with siblings, not over a hand-built stub.
/// </remarks>
public class CompositeCommandTests
{
    private sealed class Fleet
    {
        public required ManualTimeProvider Clock { get; init; }

        public required InMemoryCommandStore Store { get; init; }

        public required InMemoryActuatorStateStore States { get; init; }

        public required CommandService Service { get; init; }

        public required IZoneCatalogue Catalogue { get; init; }

        public required IReadOnlyList<SensorRegistration> Sensors { get; init; }

        public static Fleet Create()
        {
            var (trust, clock) = TrustTestHost.Create();
            var store = new InMemoryCommandStore();
            var states = new InMemoryActuatorStateStore();
            var mesh = MeshTestHost.Create(clock);

            var sensors = DemoDevices.All
                .Select(device =>
                {
                    Assert.True(device.Registration.TryCreateRegistration(out var registration, out _));

                    return registration;
                })
                .ToList();

            return new Fleet
            {
                Clock = clock,
                Store = store,
                States = states,
                Service = new CommandService(store, mesh.CommandLog, states, trust, mesh.Alerts, Options.Create(new CommandOptions()), clock),
                Catalogue = new ZoneCatalogue(DemoZoneHierarchy.Zones),
                Sensors = sensors,
            };
        }

        public ZoneIssue CloseValves(string zoneId) =>
            Service.IssueForZone(zoneId, CommandAction.ValveClose, null, Catalogue, Sensors);

        public IEnumerable<string> ValvesUnder(string zone) =>
            Catalogue.NodesBeneath(zone, Sensors).Where(s => s.ActuatorKind == ActuatorKind.Valve).Select(s => s.MacAddress);
    }

    private static string Mac(byte suffix) => $"B8:27:EB:10:00:{suffix:X2}";

    [Fact]
    public void A_zone_command_reaches_every_matching_actuator_in_the_zone_and_beneath_it()
    {
        var fleet = Fleet.Create();

        // Zone 3 is a parent. Its valves sit in two different sub-zones one level down: three in the Chiller
        // and Freezer bays that the walk has to descend into, and none in Zone 3 itself.
        var issue = fleet.CloseValves("zone-3");

        var commands = issue.Result!.Commands;
        Assert.Equal(
            [Mac(0x19), Mac(0x2B), Mac(0x2C), Mac(0x2D)],
            commands.Select(command => command.MacAddress).Order());

        // The Loading Dock's drain valve is a valve too, but it is under a different facility, so it is not reached.
        Assert.DoesNotContain(commands, command => command.MacAddress == Mac(0x21));
        Assert.All(commands, command => Assert.Equal(CommandAction.ValveClose, command.Action));
    }

    [Fact]
    public void The_walk_descends_more_than_one_level_when_the_zone_is_a_facility()
    {
        var fleet = Fleet.Create();

        // Facility B > Zone 4 > Sub-Zone H: the dock drain valve is two levels below the zone that was named. The hydroponics bay's drain valve is one level below.
        var facilityB = fleet.CloseValves("facility-b");
        Assert.Equal([Mac(0x21), Mac(0x37)], facilityB.Result!.Commands.Select(command => command.MacAddress).Order());

        // And Facility A reaches all four of Zone 3's valves without being told about Zone 3.
        Assert.Equal(4, fleet.CloseValves("facility-a").Result!.Commands.Count);
    }

    [Fact]
    public void Only_actuators_of_the_actions_kind_are_reached()
    {
        var fleet = Fleet.Create();

        // Zone 3 has pumps and dimmers as well as valves. A valve command must leave them alone.
        var pumps = fleet.Service.IssueForZone("zone-3", CommandAction.PumpOn, null, fleet.Catalogue, fleet.Sensors);

        Assert.Equal([Mac(0x29), Mac(0x2A)], pumps.Result!.Commands.Select(command => command.MacAddress).Order());
    }

    [Fact]
    public void A_zone_command_is_one_undo_stack_entry_however_many_devices_it_reached()
    {
        var fleet = Fleet.Create();

        fleet.CloseValves("zone-3");

        var entry = Assert.Single(fleet.Service.UndoStack());
        Assert.Equal(4, entry.Commands.Count);
        Assert.NotNull(entry.CompositeId);
        Assert.Contains("4 Valve devices", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Undoing_a_zone_command_reverts_every_child_to_its_own_previous_state()
    {
        var fleet = Fleet.Create();

        // The valves were not all in the same state: two open, two already closed. "Shut them all" then
        // "undo" has to put each back as it was, which an opposite-of-the-action design cannot do at all.
        var before = new Dictionary<string, double> { [Mac(0x19)] = 1, [Mac(0x2B)] = 1, [Mac(0x2C)] = 0, [Mac(0x2D)] = 0 };

        foreach (var (mac, value) in before)
        {
            fleet.States.Observe(mac, value, fleet.Clock.GetUtcNow(), ObservationSource.Telemetry);
        }

        var issue = fleet.CloseValves("zone-3");

        foreach (var command in issue.Result!.Commands)
        {
            fleet.Service.Collect(command.MacAddress);
            fleet.Service.Acknowledge(command.Id, new CommandAckRequest(true, 0));
        }

        var undo = fleet.Service.Undo();

        Assert.Equal(UndoOutcome.Reverted, undo.Outcome);
        Assert.Equal(4, undo.Items.Count(item => item.Result == UndoItemResult.RestoreIssued));

        foreach (var item in undo.Items)
        {
            var restore = fleet.Store.Find(item.RestoreCommandId!.Value)!;

            Assert.Equal(before[item.MacAddress], restore.TargetValue);
            Assert.Equal(before[item.MacAddress] == 1 ? CommandAction.ValveOpen : CommandAction.ValveClose, restore.Action);
        }

        // One undo covered all of it.
        Assert.Empty(fleet.Service.UndoStack());
    }

    [Fact]
    public void When_some_children_apply_and_one_times_out_the_composite_is_partially_applied()
    {
        var fleet = Fleet.Create();
        var issue = fleet.CloseValves("zone-3");
        var commands = issue.Result!.Commands;

        Assert.Equal(CompositeStatus.InProgress, issue.Result.Status);

        // Three valves collect and confirm. The fourth's device is silent and never collects.
        foreach (var command in commands.Take(3))
        {
            fleet.Service.Collect(command.MacAddress);
            fleet.Service.Acknowledge(command.Id, new CommandAckRequest(true, 0));
        }

        fleet.Clock.Advance(TimeSpan.FromMinutes(5));
        fleet.Service.TimeOutStale();

        var status = CommandService.StatusOf([.. fleet.Service.Composite(issue.Result.CompositeId)]);

        Assert.Equal(CompositeStatus.PartiallyApplied, status);
        Assert.Equal(3, fleet.Service.Composite(issue.Result.CompositeId).Count(command => command.State == CommandState.Acknowledged));
        Assert.Equal(CommandState.TimedOut, fleet.Service.Composite(issue.Result.CompositeId)[3].State);
    }

    [Fact]
    public void Undoing_a_partially_applied_composite_reverts_the_three_and_reports_the_one_it_cannot()
    {
        var fleet = Fleet.Create();

        foreach (var mac in fleet.ValvesUnder("zone-3"))
        {
            fleet.States.Observe(mac, 1, fleet.Clock.GetUtcNow(), ObservationSource.Telemetry);
        }

        var commands = fleet.CloseValves("zone-3").Result!.Commands;

        foreach (var command in commands.Take(3))
        {
            fleet.Service.Collect(command.MacAddress);
            fleet.Service.Acknowledge(command.Id, new CommandAckRequest(true, 0));
        }

        fleet.Clock.Advance(TimeSpan.FromMinutes(5));
        fleet.Service.TimeOutStale();

        var undo = fleet.Service.Undo();

        Assert.Equal(UndoOutcome.PartiallyReverted, undo.Outcome);
        Assert.Equal(3, undo.Items.Count(item => item.Result == UndoItemResult.RestoreIssued));

        var refused = Assert.Single(undo.Items, item => item.Result == UndoItemResult.Refused);
        Assert.Equal(commands[3].MacAddress, refused.MacAddress);
        Assert.Contains("real state is unknown", refused.Reason, StringComparison.Ordinal);

        // The message tells an operator which device it could not revert.
        Assert.Contains(commands[3].MacAddress, undo.Message, StringComparison.Ordinal);
        Assert.StartsWith("Partly undid", undo.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_applied_only_when_every_child_was_and_not_applied_when_none_was()
    {
        var fleet = Fleet.Create();
        var all = fleet.CloseValves("zone-3").Result!.Commands;

        foreach (var command in all)
        {
            fleet.Service.Collect(command.MacAddress);
            fleet.Service.Acknowledge(command.Id, new CommandAckRequest(true, 0));
        }

        Assert.Equal(CompositeStatus.Applied, CommandService.StatusOf([.. fleet.Service.Composite(all[0].CompositeId!.Value)]));

        var second = Fleet.Create();
        var none = second.CloseValves("zone-3").Result!;
        second.Clock.Advance(TimeSpan.FromMinutes(5));
        second.Service.TimeOutStale();

        Assert.Equal(CompositeStatus.NotApplied, CommandService.StatusOf([.. second.Service.Composite(none.CompositeId)]));
    }

    [Fact]
    public void A_zone_command_for_an_unknown_zone_or_with_nothing_to_act_on_is_refused()
    {
        var fleet = Fleet.Create();

        Assert.Equal(IssueFailure.UnknownZone, fleet.CloseValves("zone-99").Failure);

        // Zone 2 is the Energy Centre. It holds power meters and no valves.
        var none = fleet.CloseValves("zone-2");
        Assert.Equal(IssueFailure.NoMatchingActuators, none.Failure);
        Assert.Contains("no Valve", none.Error, StringComparison.Ordinal);
        Assert.Empty(fleet.Service.UndoStack());
        Assert.Empty(fleet.Store.List(null, null, 10));
    }

    [Fact]
    public void A_level_that_one_device_cannot_take_refuses_the_whole_zone_action_instead_of_issuing_half()
    {
        var fleet = Fleet.Create();

        // Every dimmer in Facility B takes 0 to 100. 150 is out of range for all of them, and nothing is issued.
        var issue = fleet.Service.IssueForZone("facility-b", CommandAction.SetLevel, 150, fleet.Catalogue, fleet.Sensors);

        Assert.Equal(IssueFailure.Invalid, issue.Failure);
        Assert.Empty(fleet.Store.List(null, null, 10));
        Assert.Empty(fleet.Service.UndoStack());
    }

    [Fact]
    public void A_zone_wide_level_command_reaches_every_dimmer_beneath_the_zone()
    {
        var fleet = Fleet.Create();

        var issue = fleet.Service.IssueForZone("facility-b", CommandAction.SetLevel, 20, fleet.Catalogue, fleet.Sensors);

        // Warehouse lights (Zone 4), the hydroponics grow light (Zone 6), and meeting room lights and office lights (Zone 5, two levels down).
        Assert.Equal([Mac(0x27), Mac(0x2E), Mac(0x2F), Mac(0x36)], issue.Result!.Commands.Select(command => command.MacAddress).Order());
        Assert.All(issue.Result.Commands, command => Assert.Equal(20, command.Level));
    }
}
