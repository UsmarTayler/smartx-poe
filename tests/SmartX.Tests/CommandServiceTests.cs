using Microsoft.Extensions.Options;
using SmartX.Api.Commands;
using SmartX.Api.Storage;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the command lifecycle, timeouts, validation and the undo stack, with no HTTP and a controlled clock.
/// </summary>
/// <remarks>
/// The device's side of every exchange is played by hand: <c>Collect</c> is the device checking in, and
/// <c>Acknowledge</c> is it confirming. That keeps each test about one rule, and the end-to-end tests with the
/// real simulator live in <see cref="CommandEndToEndTests"/>.
/// </remarks>
public class CommandServiceTests
{
    private const string ValveA = "AA:BB:CC:00:01:01";
    private const string ValveB = "AA:BB:CC:00:01:02";
    private const string PumpA = "AA:BB:CC:00:01:03";
    private const string Lamp = "AA:BB:CC:00:01:04";
    private const string Thermometer = "AA:BB:CC:00:01:05";

    private sealed class Harness
    {
        public required ManualTimeProvider Clock { get; init; }

        public required InMemoryCommandStore Store { get; init; }

        public required ICommandLog Log { get; init; }

        public required MeshTestHost Mesh { get; init; }

        public required InMemoryActuatorStateStore States { get; init; }

        public required Api.Trust.TrustEngine Trust { get; init; }

        public required CommandService Service { get; init; }

        public Dictionary<string, SensorRegistration> Sensors { get; } = [];

        public static Harness Create(Action<CommandOptions>? configure = null)
        {
            var options = new CommandOptions();
            configure?.Invoke(options);

            var (trust, clock) = TrustTestHost.Create();
            var store = new InMemoryCommandStore();
            var mesh = MeshTestHost.Create(clock);
            var log = mesh.CommandLog;
            var states = new InMemoryActuatorStateStore();

            var harness = new Harness
            {
                Clock = clock,
                Store = store,
                Log = log,
                Mesh = mesh,
                States = states,
                Trust = trust,
                Service = new CommandService(store, log, states, trust, mesh.Alerts, Options.Create(options), clock),
            };

            harness.Add(ValveA, ActuatorKind.Valve, TelemetryPayloadKind.State, 0, 1);
            harness.Add(ValveB, ActuatorKind.Valve, TelemetryPayloadKind.State, 0, 1);
            harness.Add(PumpA, ActuatorKind.Pump, TelemetryPayloadKind.State, 0, 1);
            harness.Add(Lamp, ActuatorKind.Dimmer, TelemetryPayloadKind.Integer, 0, 100);
            harness.Sensors[Thermometer] = new SensorRegistration(
                Thermometer, new DeploymentLocation("Room", "sub-zone-b", "N1"), SensorCategory.Environmental, "°C",
                new ValueRange(0, 50), TelemetryPayloadKind.Real);

            return harness;
        }

        private void Add(string mac, ActuatorKind kind, TelemetryPayloadKind payload, double min, double max) =>
            Sensors[mac] = new SensorRegistration(
                mac, new DeploymentLocation("Room", "sub-zone-b", "N1"), SensorCategory.Actuator,
                payload == TelemetryPayloadKind.State ? "state" : "%", new ValueRange(min, max), payload, null, kind);

        public void Observe(string mac, double value) => States.Observe(mac, value, Clock.GetUtcNow(), ObservationSource.Telemetry);

        public Command Issue(string mac, CommandAction action, double? level = null)
        {
            var issue = Service.Issue(Sensors[mac], action, level);
            Assert.NotNull(issue.Command);

            return issue.Command;
        }

        /// <summary>The device checks in and collects, then applies and confirms, as a working device does.</summary>
        public Command DeliverAndAcknowledge(Command command, bool success = true)
        {
            Assert.Contains(Service.Collect(command.MacAddress), delivered => delivered.Id == command.Id);
            var result = Service.Acknowledge(command.Id, new CommandAckRequest(success, success ? command.TargetValue : null, success ? null : "stuck"));
            Assert.Equal(AckOutcome.Applied, result.Outcome);

            return Store.Find(command.Id)!;
        }
    }

    // ---- lifecycle ----------------------------------------------------------------------------------------

    [Fact]
    public void A_command_runs_from_issued_to_delivered_to_acknowledged_and_the_log_records_each_step()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);

        var command = harness.Issue(ValveA, CommandAction.ValveClose);
        Assert.Equal(CommandState.Pending, command.State);
        Assert.Equal(0, command.TargetValue);

        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        var delivered = Assert.Single(harness.Service.Collect(ValveA));
        Assert.Equal(CommandState.Delivered, delivered.State);
        Assert.NotNull(delivered.DeliveredAt);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AckOutcome.Applied, harness.Service.Acknowledge(command.Id, new CommandAckRequest(true, 0)).Outcome);
        var acknowledged = harness.Store.Find(command.Id)!;
        Assert.Equal(CommandState.Acknowledged, acknowledged.State);
        Assert.Equal(0, acknowledged.ResultingValue);

        var events = harness.Log.Query(null, command.Id, 10).Reverse().Select(entry => entry.Event).ToList();
        Assert.Equal([CommandEvent.Issued, CommandEvent.Delivered, CommandEvent.Acknowledged], events);
    }

    [Fact]
    public void Every_log_entry_carries_the_targets_trust_band_at_that_moment()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);

        // Tracked, with no evidence yet: unproven when the command is issued.
        harness.Trust.Track(ValveA);
        var command = harness.Issue(ValveA, CommandAction.ValveClose);

        // Then the device fails badly. Later log entries must show its band then, not its band at issue time.
        RecordMany(harness.Trust, ValveA, TrustOutcome.SchemaFailure, 6);
        harness.DeliverAndAcknowledge(command);

        var entries = harness.Log.Query(null, command.Id, 10).Reverse().ToList();

        Assert.Equal(TrustBand.Unproven, entries[0].TrustBand);
        Assert.Equal(TrustBand.Fault, entries[^1].TrustBand);
    }

    [Fact]
    public void A_command_is_handed_over_once_and_a_second_check_in_gets_nothing()
    {
        // The gateway half of at-least-once delivery made safe: never re-send a delivered command.
        var harness = Harness.Create();
        var command = harness.Issue(ValveA, CommandAction.ValveOpen);

        Assert.Single(harness.Service.Collect(ValveA));
        Assert.Empty(harness.Service.Collect(ValveA));
        Assert.Equal(CommandState.Delivered, harness.Store.Find(command.Id)!.State);
    }

    [Fact]
    public void Commands_reach_a_device_in_the_order_they_were_issued()
    {
        var harness = Harness.Create();
        var open = harness.Issue(ValveA, CommandAction.ValveOpen);
        var close = harness.Issue(ValveA, CommandAction.ValveClose);

        Assert.Equal([open.Id, close.Id], harness.Service.Collect(ValveA).Select(command => command.Id));
    }

    [Fact]
    public void A_command_is_only_delivered_to_the_device_it_is_for()
    {
        var harness = Harness.Create();
        harness.Issue(ValveA, CommandAction.ValveOpen);

        Assert.Empty(harness.Service.Collect(ValveB));
    }

    [Fact]
    public void A_repeated_acknowledgement_is_accepted_and_changes_nothing_but_a_contradiction_is_a_conflict()
    {
        var harness = Harness.Create();
        var command = harness.Issue(ValveA, CommandAction.ValveOpen);
        harness.DeliverAndAcknowledge(command);

        Assert.Equal(AckOutcome.Repeated, harness.Service.Acknowledge(command.Id, new CommandAckRequest(true, 1)).Outcome);
        Assert.Equal(AckOutcome.Conflict, harness.Service.Acknowledge(command.Id, new CommandAckRequest(false, null, "stuck")).Outcome);
        Assert.Equal(CommandState.Acknowledged, harness.Store.Find(command.Id)!.State);
    }

    [Fact]
    public void A_command_that_was_not_delivered_cannot_be_acknowledged()
    {
        var harness = Harness.Create();
        var command = harness.Issue(ValveA, CommandAction.ValveOpen);

        var result = harness.Service.Acknowledge(command.Id, new CommandAckRequest(true, 1));

        Assert.Equal(AckOutcome.Conflict, result.Outcome);
        Assert.Contains("not been delivered", result.Message, StringComparison.Ordinal);
        Assert.Equal(AckOutcome.NotFound, harness.Service.Acknowledge(Guid.NewGuid(), new CommandAckRequest(true, 1)).Outcome);
    }

    [Fact]
    public void A_failure_reported_by_the_device_is_recorded_with_its_reason()
    {
        var harness = Harness.Create();
        var command = harness.Issue(ValveA, CommandAction.ValveOpen);

        var failed = harness.DeliverAndAcknowledge(command, success: false);

        Assert.Equal(CommandState.Failed, failed.State);
        Assert.Equal("stuck", failed.FailureReason);
    }

    [Fact]
    public void An_acknowledgement_teaches_the_gateway_the_actuators_new_state()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);

        harness.DeliverAndAcknowledge(harness.Issue(ValveA, CommandAction.ValveClose));

        Assert.True(harness.States.TryGet(ValveA, out var state));
        Assert.Equal(0, state.Value);
    }

    // ---- timeouts -----------------------------------------------------------------------------------------

    [Fact]
    public void A_pending_command_nobody_collects_times_out()
    {
        var harness = Harness.Create(options => options.PendingTimeout = TimeSpan.FromSeconds(30));
        var command = harness.Issue(ValveA, CommandAction.ValveOpen);

        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, harness.Service.TimeOutStale());
        Assert.Equal(CommandState.Pending, harness.Store.Find(command.Id)!.State);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, harness.Service.TimeOutStale());

        var timedOut = harness.Store.Find(command.Id)!;
        Assert.Equal(CommandState.TimedOut, timedOut.State);
        Assert.Contains("did not collect", timedOut.FailureReason, StringComparison.Ordinal);
        Assert.Contains(harness.Log.Query(null, command.Id, 10), entry => entry.Event == CommandEvent.TimedOut);
    }

    [Fact]
    public void A_delivered_command_nobody_confirms_times_out_too()
    {
        var harness = Harness.Create(options => options.DeliveredTimeout = TimeSpan.FromSeconds(10));
        var command = harness.Issue(ValveA, CommandAction.ValveOpen);
        harness.Service.Collect(ValveA);

        harness.Clock.Advance(TimeSpan.FromSeconds(11));
        harness.Service.TimeOutStale();

        var timedOut = harness.Store.Find(command.Id)!;
        Assert.Equal(CommandState.TimedOut, timedOut.State);
        Assert.Contains("did not confirm", timedOut.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_acknowledged_command_is_never_timed_out()
    {
        var harness = Harness.Create();
        var command = harness.Issue(ValveA, CommandAction.ValveOpen);
        harness.DeliverAndAcknowledge(command);

        harness.Clock.Advance(TimeSpan.FromHours(1));
        harness.Service.TimeOutStale();

        Assert.Equal(CommandState.Acknowledged, harness.Store.Find(command.Id)!.State);
    }

    // ---- validation ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ValveA, CommandAction.PumpOn, "PumpOn is a Pump command, and this device is a Valve")]
    [InlineData(PumpA, CommandAction.ValveClose, "ValveClose is a Valve command, and this device is a Pump")]
    [InlineData(Lamp, CommandAction.RelayOn, "RelayOn is a Relay command, and this device is a Dimmer")]
    public void An_action_that_does_not_match_the_actuator_kind_is_refused_with_a_reason(string mac, CommandAction action, string reason)
    {
        var harness = Harness.Create();

        var issue = harness.Service.Issue(harness.Sensors[mac], action, null);

        Assert.Null(issue.Command);
        Assert.Equal(IssueFailure.Invalid, issue.Failure);
        Assert.Contains(reason, issue.Error, StringComparison.Ordinal);
        Assert.Empty(harness.Store.List(null, null, 10));
        Assert.Empty(harness.Service.UndoStack());
    }

    [Fact]
    public void A_sensor_that_is_not_an_actuator_cannot_be_commanded()
    {
        var harness = Harness.Create();

        var issue = harness.Service.Issue(harness.Sensors[Thermometer], CommandAction.PumpOn, null);

        Assert.Equal(IssueFailure.NotAnActuator, issue.Failure);
        Assert.Contains("not an actuator", issue.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_actuator_registered_without_a_kind_cannot_be_commanded_and_the_reason_says_so()
    {
        var harness = Harness.Create();
        var unkinded = new SensorRegistration(
            "AA:BB:CC:00:01:09", new DeploymentLocation("Room", "sub-zone-b", "N1"), SensorCategory.Actuator, "state",
            new ValueRange(0, 1), TelemetryPayloadKind.State);

        var issue = harness.Service.Issue(unkinded, CommandAction.RelayOn, null);

        Assert.Equal(IssueFailure.Invalid, issue.Failure);
        Assert.Contains("registered without an actuator kind", issue.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "needs a level")]
    [InlineData(150.0, "outside this device's range")]
    [InlineData(-1.0, "outside this device's range")]
    public void A_set_level_command_needs_a_level_inside_the_devices_range(double? level, string reason)
    {
        var harness = Harness.Create();

        var issue = harness.Service.Issue(harness.Sensors[Lamp], CommandAction.SetLevel, level);

        Assert.Equal(IssueFailure.Invalid, issue.Failure);
        Assert.Contains(reason, issue.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_action_that_takes_no_level_refuses_one()
    {
        var harness = Harness.Create();

        Assert.Equal(IssueFailure.Invalid, harness.Service.Issue(harness.Sensors[ValveA], CommandAction.ValveOpen, 50).Failure);
    }

    // ---- undo: previous state, not the opposite -----------------------------------------------------------

    [Fact]
    public void Undo_restores_the_previous_state_and_does_not_do_the_opposite()
    {
        // The central design point. The valve is already open. An operator sends ValveOpen, which changes
        // nothing. Undoing that must leave the valve open. A design that undoes with the opposite action
        // would send ValveClose, putting the valve in a state it was never in.
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);

        var command = harness.Issue(ValveA, CommandAction.ValveOpen);
        harness.DeliverAndAcknowledge(command);

        var undo = harness.Service.Undo();

        Assert.Equal(UndoOutcome.Reverted, undo.Outcome);
        var restore = harness.Store.Find(Assert.Single(undo.Items).RestoreCommandId!.Value)!;

        Assert.Equal(CommandAction.ValveOpen, restore.Action);
        Assert.NotEqual(CommandAction.ValveClose, restore.Action);
        Assert.Equal(1, restore.TargetValue);
        Assert.Equal(command.Id, restore.RevertsCommandId);
    }

    [Fact]
    public void Undo_of_a_level_command_restores_the_previous_level()
    {
        var harness = Harness.Create();
        harness.Observe(Lamp, 35);
        harness.DeliverAndAcknowledge(harness.Issue(Lamp, CommandAction.SetLevel, 90));

        var restore = harness.Store.Find(harness.Service.Undo().Items[0].RestoreCommandId!.Value)!;

        Assert.Equal(CommandAction.SetLevel, restore.Action);
        Assert.Equal(35, restore.Level);
        Assert.Equal(35, restore.TargetValue);
    }

    [Fact]
    public void A_command_records_the_state_it_replaced()
    {
        var harness = Harness.Create();
        harness.Observe(Lamp, 35);

        var command = harness.Issue(Lamp, CommandAction.SetLevel, 90);

        Assert.True(command.PreviousObserved);
        Assert.Equal(35, command.PreviousValue);
    }

    [Fact]
    public void A_command_issued_while_another_is_in_flight_records_that_ones_target_as_its_previous_state()
    {
        // Two quick commands. Both would otherwise record the old reading, and undoing the second would skip
        // over the first. The second must start from where the first was heading.
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);

        var close = harness.Issue(ValveA, CommandAction.ValveClose);
        var open = harness.Issue(ValveA, CommandAction.ValveOpen);

        Assert.Equal(1, close.PreviousValue);
        Assert.Equal(0, open.PreviousValue);
        Assert.Contains(close.Id.ToString(), open.Note, StringComparison.Ordinal);
    }

    // ---- undo: one test for each row of the table ---------------------------------------------------------

    [Fact]
    public void Undo_of_a_pending_command_cancels_it_because_nothing_reached_the_device()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);
        var command = harness.Issue(ValveA, CommandAction.ValveClose);

        var undo = harness.Service.Undo();

        Assert.Equal(UndoOutcome.Reverted, undo.Outcome);
        Assert.Equal(UndoItemResult.Cancelled, Assert.Single(undo.Items).Result);
        Assert.Equal(CommandState.Cancelled, harness.Store.Find(command.Id)!.State);

        // And the device, checking in afterwards, finds nothing to do.
        Assert.Empty(harness.Service.Collect(ValveA));
        Assert.Contains(harness.Log.Query(null, command.Id, 10), entry => entry.Event == CommandEvent.Cancelled);
    }

    [Fact]
    public void Undo_of_a_delivered_command_queues_a_restore_behind_it_because_it_cannot_be_recalled()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);
        var command = harness.Issue(ValveA, CommandAction.ValveClose);
        harness.Service.Collect(ValveA);

        var undo = harness.Service.Undo();

        var item = Assert.Single(undo.Items);
        Assert.Equal(UndoItemResult.RestoreIssued, item.Result);

        // The original is still with the device, untouched. The restore waits behind it.
        Assert.Equal(CommandState.Delivered, harness.Store.Find(command.Id)!.State);
        var restore = harness.Store.Find(item.RestoreCommandId!.Value)!;
        Assert.Equal(CommandState.Pending, restore.State);
        Assert.Equal(CommandAction.ValveOpen, restore.Action);
        Assert.Equal(item.RestoreCommandId, Assert.Single(harness.Service.Collect(ValveA)).Id);
    }

    [Fact]
    public void Undo_of_an_acknowledged_command_issues_a_restore_to_the_recorded_previous_state()
    {
        var harness = Harness.Create();
        harness.Observe(PumpA, 0);
        var command = harness.DeliverAndAcknowledge(harness.Issue(PumpA, CommandAction.PumpOn));

        var undo = harness.Service.Undo();

        var restore = harness.Store.Find(Assert.Single(undo.Items).RestoreCommandId!.Value)!;
        Assert.Equal(CommandAction.PumpOff, restore.Action);
        Assert.Equal(command.Id, restore.RevertsCommandId);
        Assert.Contains(harness.Log.Query(null, command.Id, 10), entry => entry.Event == CommandEvent.Reverted);
    }

    [Fact]
    public void Undo_of_a_timed_out_command_is_refused_because_the_devices_real_state_is_unknown()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);
        harness.Issue(ValveA, CommandAction.ValveClose);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        harness.Service.TimeOutStale();

        var undo = harness.Service.Undo();

        Assert.Equal(UndoOutcome.Refused, undo.Outcome);
        var item = Assert.Single(undo.Items);
        Assert.Equal(UndoItemResult.Refused, item.Result);
        Assert.Contains("real state is unknown", item.Reason, StringComparison.Ordinal);
        Assert.Contains("could do harm", item.Reason, StringComparison.Ordinal);
        Assert.Null(item.RestoreCommandId);

        // Nothing was issued: the only command is the one that timed out.
        Assert.Single(harness.Store.List(null, null, 10));
    }

    [Fact]
    public void Undo_of_a_failed_command_is_refused()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);
        harness.DeliverAndAcknowledge(harness.Issue(ValveA, CommandAction.ValveClose), success: false);

        var undo = harness.Service.Undo();

        Assert.Equal(UndoOutcome.Refused, undo.Outcome);
        Assert.Contains("state it is in is unknown", Assert.Single(undo.Items).Reason, StringComparison.Ordinal);
        Assert.Single(harness.Store.List(null, null, 10));
    }

    [Fact]
    public void A_refused_undo_still_removes_the_entry_so_the_ones_beneath_it_can_be_reached()
    {
        var harness = Harness.Create();
        harness.Observe(ValveB, 1);
        harness.DeliverAndAcknowledge(harness.Issue(ValveB, CommandAction.ValveClose));
        harness.Observe(ValveA, 1);
        harness.Issue(ValveA, CommandAction.ValveClose);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        harness.Service.TimeOutStale();

        Assert.Equal(UndoOutcome.Refused, harness.Service.Undo().Outcome);
        Assert.Equal(UndoOutcome.Reverted, harness.Service.Undo().Outcome);
    }

    // ---- undo: the unobserved case, and the stack itself --------------------------------------------------

    [Fact]
    public void A_command_to_an_actuator_that_never_reported_says_its_previous_state_is_unknown()
    {
        var harness = Harness.Create();

        var command = harness.Issue(ValveA, CommandAction.ValveClose);

        Assert.False(command.PreviousObserved);
        Assert.Null(command.PreviousValue);
        Assert.Contains("never observed", command.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_of_an_unobserved_previous_state_is_refused_rather_than_guessed()
    {
        var harness = Harness.Create();
        var command = harness.Issue(ValveA, CommandAction.ValveClose);

        // Delivered but not acknowledged: the device has it, and there is no recorded state to return to.
        harness.Service.Collect(ValveA);
        var undo = harness.Service.Undo();

        Assert.Equal(UndoOutcome.Refused, undo.Outcome);
        Assert.Contains("never observed", Assert.Single(undo.Items).Reason, StringComparison.Ordinal);
        Assert.Single(harness.Store.List(null, null, 10));
        Assert.Equal(CommandState.Delivered, harness.Store.Find(command.Id)!.State);
    }

    [Fact]
    public void A_pending_command_can_still_be_undone_when_its_previous_state_was_never_observed()
    {
        // Cancelling needs no recorded state: nothing reached the device, so there is nothing to restore.
        var harness = Harness.Create();
        harness.Issue(ValveA, CommandAction.ValveClose);

        Assert.Equal(UndoItemResult.Cancelled, Assert.Single(harness.Service.Undo().Items).Result);
    }

    [Fact]
    public void Restore_commands_never_land_on_the_undo_stack()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);
        harness.DeliverAndAcknowledge(harness.Issue(ValveA, CommandAction.ValveClose));

        Assert.Single(harness.Service.UndoStack());

        var undo = harness.Service.Undo();
        var restoreId = Assert.Single(undo.Items).RestoreCommandId!.Value;

        // The restore exists, and is a real command in the store...
        Assert.True(harness.Store.Find(restoreId)!.IsRestore);

        // ...but it is on no stack entry, so undo cannot undo the undo and loop.
        Assert.Empty(harness.Service.UndoStack());
        Assert.Equal(UndoOutcome.NothingToUndo, harness.Service.Undo().Outcome);
    }

    [Fact]
    public void The_last_command_issued_is_the_first_one_undone()
    {
        var harness = Harness.Create();
        var first = harness.Issue(ValveA, CommandAction.ValveOpen);
        var second = harness.Issue(PumpA, CommandAction.PumpOn);
        var third = harness.Issue(Lamp, CommandAction.SetLevel, 40);

        Assert.Equal([third.Id, second.Id, first.Id], harness.Service.UndoStack().Select(entry => entry.Commands[0].Id));

        Assert.Equal(third.Id, harness.Service.Undo().Items[0].CommandId);
        Assert.Equal(second.Id, harness.Service.Undo().Items[0].CommandId);
        Assert.Equal(first.Id, harness.Service.Undo().Items[0].CommandId);
        Assert.Equal(UndoOutcome.NothingToUndo, harness.Service.Undo().Outcome);
    }

    [Fact]
    public void Undo_with_nothing_on_the_stack_says_so()
    {
        var undo = Harness.Create().Service.Undo();

        Assert.Equal(UndoOutcome.NothingToUndo, undo.Outcome);
        Assert.Null(undo.EntryId);
        Assert.Empty(undo.Items);
    }

    [Fact]
    public void The_stack_forgets_its_oldest_entry_once_it_is_full()
    {
        var harness = Harness.Create(options => options.MaxUndoDepth = 3);
        var issued = Enumerable.Range(0, 5)
            .Select(i => harness.Issue(Lamp, CommandAction.SetLevel, i * 10))
            .ToList();

        var remembered = harness.Service.UndoStack().Select(entry => entry.Commands[0].Id).ToList();

        // The three most recent, most recent first. The two oldest fell off the bottom.
        Assert.Equal([issued[4].Id, issued[3].Id, issued[2].Id], remembered);

        // Undo works down to the oldest one that was kept, and then there is nothing.
        harness.Service.Undo();
        harness.Service.Undo();
        harness.Service.Undo();
        Assert.Equal(UndoOutcome.NothingToUndo, harness.Service.Undo().Outcome);
    }

    [Fact]
    public void Two_quick_commands_undo_in_reverse_and_land_on_the_original_state()
    {
        var harness = Harness.Create();
        harness.Observe(ValveA, 1);
        var close = harness.Issue(ValveA, CommandAction.ValveClose);
        var open = harness.Issue(ValveA, CommandAction.ValveOpen);

        // Both still pending: undoing withdraws the later one, then the earlier, and nothing ever ran.
        Assert.Equal(open.Id, harness.Service.Undo().Items[0].CommandId);
        Assert.Equal(close.Id, harness.Service.Undo().Items[0].CommandId);
        Assert.Empty(harness.Service.Collect(ValveA));
    }
}
