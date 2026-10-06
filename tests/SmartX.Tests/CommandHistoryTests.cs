using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.History;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// The command log now lives in the history store, and commands feed the alert set.
/// </summary>
public class CommandHistoryTests
{
    private const string Valve = "AA:BB:CC:00:01:01";
    private const string Pump = "AA:BB:CC:00:01:02";

    private static readonly SensorRegistration ValveRegistration = new(
        Valve, new DeploymentLocation("Plant Room", "zone-1", "N1"), SensorCategory.Actuator, "state",
        new ValueRange(0, 1), TelemetryPayloadKind.State, null, ActuatorKind.Valve);

    private static readonly SensorRegistration PumpRegistration = new(
        Pump, new DeploymentLocation("Plant Room", "zone-1", "N1"), SensorCategory.Actuator, "state",
        new ValueRange(0, 1), TelemetryPayloadKind.State, null, ActuatorKind.Pump);

    private sealed class Rig
    {
        public required ManualTimeProvider Clock { get; init; }

        public required MeshTestHost Mesh { get; init; }

        public required CommandService Service { get; init; }

        public required InMemoryActuatorStateStore States { get; init; }

        public static Rig Create()
        {
            var (trust, clock) = TrustTestHost.Create();
            var mesh = MeshTestHost.Create(clock);
            var states = new InMemoryActuatorStateStore();

            return new Rig
            {
                Clock = clock,
                Mesh = mesh,
                States = states,
                Service = new CommandService(
                    new InMemoryCommandStore(), mesh.CommandLog, states, trust, mesh.Alerts,
                    Options.Create(new CommandOptions()), clock),
            };
        }

        public Command Issue(SensorRegistration sensor, CommandAction action) =>
            Service.Issue(sensor, action, null).Command!;

        public void Deliver(Command command) => Service.Collect(command.MacAddress);

        public void Confirm(Command command) => Service.Acknowledge(command.Id, new CommandAckRequest(true, null));

        public IReadOnlyList<HistoryEntry> Events() =>
            Mesh.History.GetEvents(Clock.GetUtcNow().AddDays(-2), Clock.GetUtcNow().AddDays(2));
    }

    // ---- command timeouts as alerts ---------------------------------------------------------------------------

    [Fact]
    public void A_command_that_times_out_raises_a_command_timed_out_alert_for_its_device()
    {
        var rig = Rig.Create();
        rig.Issue(ValveRegistration, CommandAction.ValveClose);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Service.TimeOutStale();

        var alert = Assert.Single(rig.Mesh.Alerts.Active());
        Assert.Equal(new AlertKey(Valve, AlertKind.CommandTimedOut), alert.Key);
        Assert.Contains("did not collect", alert.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_timeout_while_the_alert_is_active_is_suppressed_and_counted()
    {
        var rig = Rig.Create();
        rig.Issue(ValveRegistration, CommandAction.ValveClose);
        rig.Issue(ValveRegistration, CommandAction.ValveOpen);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, rig.Service.TimeOutStale());

        var alert = Assert.Single(rig.Mesh.Alerts.Active());
        Assert.Equal(1, alert.SuppressedCount);
    }

    [Fact]
    public void A_timeout_on_one_device_does_not_alert_another()
    {
        var rig = Rig.Create();
        rig.Issue(ValveRegistration, CommandAction.ValveClose);
        var pump = rig.Issue(PumpRegistration, CommandAction.PumpOn);
        rig.Deliver(pump);
        rig.Confirm(pump);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Service.TimeOutStale();

        Assert.Equal([Valve], rig.Mesh.Alerts.Active().Select(alert => alert.Key.Mac));
    }

    [Fact]
    public void A_later_command_the_device_confirms_clears_the_alert()
    {
        var rig = Rig.Create();
        rig.Issue(ValveRegistration, CommandAction.ValveClose);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Service.TimeOutStale();
        Assert.Single(rig.Mesh.Alerts.Active());

        // The device answers the next one, so it is reachable.
        var next = rig.Issue(ValveRegistration, CommandAction.ValveOpen);
        rig.Deliver(next);
        rig.Confirm(next);

        Assert.Empty(rig.Mesh.Alerts.Active());
    }

    [Fact]
    public void A_command_that_is_confirmed_in_time_raises_nothing()
    {
        var rig = Rig.Create();
        var command = rig.Issue(ValveRegistration, CommandAction.ValveClose);
        rig.Deliver(command);
        rig.Confirm(command);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Service.TimeOutStale();

        Assert.Empty(rig.Mesh.Alerts.Active());
    }

    // ---- the log in history ------------------------------------------------------------------------------------

    [Fact]
    public void Command_events_are_history_events_with_a_readable_summary()
    {
        var rig = Rig.Create();
        var command = rig.Issue(ValveRegistration, CommandAction.ValveClose);
        rig.Deliver(command);
        rig.Confirm(command);

        var entries = rig.Events().Where(entry => entry.Kind == HistoryKind.Command).ToList();

        Assert.Equal(
            [CommandEvent.Issued, CommandEvent.Delivered, CommandEvent.Acknowledged],
            entries.Select(entry => entry.Event!.CommandEvent!.Value));
        Assert.Equal($"Issued: ValveClose on {Valve}", entries[0].Event!.Summary);
        Assert.All(entries, entry => Assert.Equal(command.Id, entry.Event!.CommandId));
    }

    [Fact]
    public void The_log_still_answers_by_command_and_by_device_newest_first()
    {
        var rig = Rig.Create();
        var first = rig.Issue(ValveRegistration, CommandAction.ValveClose);
        var other = rig.Issue(PumpRegistration, CommandAction.PumpOn);
        rig.Deliver(first);

        var byCommand = rig.Mesh.CommandLog.Query(null, first.Id, 10);
        Assert.Equal([CommandEvent.Delivered, CommandEvent.Issued], byCommand.Select(entry => entry.Event));

        var byDevice = rig.Mesh.CommandLog.Query(Pump, null, 10);
        Assert.Equal(other.Id, Assert.Single(byDevice).CommandId);

        // A limit takes the newest.
        Assert.Equal(CommandEvent.Delivered, Assert.Single(rig.Mesh.CommandLog.Query(null, first.Id, 1)).Event);
    }

    [Fact]
    public void Log_entries_keep_the_trust_band_the_device_was_in_at_the_time()
    {
        var rig = Rig.Create();
        var command = rig.Issue(ValveRegistration, CommandAction.ValveClose);

        // Not tracked by trust yet, so there is no band to record.
        Assert.Null(Assert.Single(rig.Mesh.CommandLog.Query(null, command.Id, 10)).TrustBand);
    }

    [Fact]
    public void A_command_entry_carries_the_alerts_that_were_active_when_it_was_written()
    {
        var rig = Rig.Create();

        var before = rig.Issue(ValveRegistration, CommandAction.ValveClose);

        rig.Mesh.Alerts.Raise(new AlertKey(Pump, AlertKind.Disconnected));
        rig.Mesh.Alerts.Raise(new AlertKey(Valve, AlertKind.OutOfRange));

        var during = rig.Issue(ValveRegistration, CommandAction.ValveOpen);

        rig.Mesh.Alerts.Clear(new AlertKey(Pump, AlertKind.Disconnected));

        var after = rig.Issue(ValveRegistration, CommandAction.ValveClose);

        // Recorded at the time, and not recomputed: the same log read later still says what was true then. The
        // snapshot is ordered by MAC address, so it is stable from one read to the next.
        Assert.Empty(rig.Mesh.CommandLog.Query(null, before.Id, 1)[0].ActiveAlerts!);
        Assert.Equal(
            [new AlertKey(Valve, AlertKind.OutOfRange), new AlertKey(Pump, AlertKind.Disconnected)],
            rig.Mesh.CommandLog.Query(null, during.Id, 1)[0].ActiveAlerts);
        Assert.Equal(
            [new AlertKey(Valve, AlertKind.OutOfRange)],
            rig.Mesh.CommandLog.Query(null, after.Id, 1)[0].ActiveAlerts);
    }

    [Fact]
    public void Command_entries_follow_the_retention_window_so_the_log_no_longer_grows_for_ever()
    {
        var rig = Rig.Create();
        var old = rig.Issue(ValveRegistration, CommandAction.ValveClose);

        rig.Clock.Advance(TimeSpan.FromHours(30));
        var recent = rig.Issue(ValveRegistration, CommandAction.ValveOpen);

        rig.Mesh.History.EvictOlderThan(rig.Clock.GetUtcNow().AddHours(-24));

        Assert.Empty(rig.Mesh.CommandLog.Query(null, old.Id, 10));
        Assert.NotEmpty(rig.Mesh.CommandLog.Query(null, recent.Id, 10));
    }

    [Fact]
    public void Undoing_a_command_is_logged_as_reverted()
    {
        var rig = Rig.Create();

        // The valve was open, so there is a previous state to go back to.
        rig.States.Observe(Valve, 1, rig.Clock.GetUtcNow(), ObservationSource.Telemetry);

        var command = rig.Issue(ValveRegistration, CommandAction.ValveClose);
        rig.Deliver(command);
        rig.Confirm(command);
        rig.Service.Undo();

        Assert.Contains(rig.Mesh.CommandLog.Query(null, command.Id, 10), entry => entry.Event == CommandEvent.Reverted);
    }
}
