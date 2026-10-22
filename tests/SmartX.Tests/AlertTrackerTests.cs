using SmartX.Api.Alerts;
using SmartX.Api.History;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// The active alert set: raised once, repeats suppressed and counted, cleared, raised again.
/// </summary>
public class AlertTrackerTests
{
    private static readonly AlertKey Disconnected = new(NodeA, AlertKind.Disconnected);
    private static readonly AlertKey OutOfRange = new(NodeA, AlertKind.OutOfRange);

    private static (MeshTestHost Mesh, ManualTimeProvider Clock) Create(Action<AlertOptions>? configure = null)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        return (MeshTestHost.Create(clock, configure), clock);
    }

    [Fact]
    public void A_condition_that_fires_is_raised_once()
    {
        var (mesh, _) = Create();

        Assert.Equal(RaiseOutcome.Raised, mesh.Alerts.Raise(Disconnected, "silent"));

        var alert = Assert.Single(mesh.Alerts.Active());
        Assert.Equal(Disconnected, alert.Key);
        Assert.Equal(0, alert.SuppressedCount);
        Assert.False(alert.Acknowledged);
    }

    [Fact]
    public void The_same_condition_firing_again_is_suppressed_and_counted_and_not_raised_again()
    {
        var (mesh, _) = Create();

        mesh.Alerts.Raise(Disconnected);

        for (var repeat = 0; repeat < 14; repeat++)
        {
            Assert.Equal(RaiseOutcome.Suppressed, mesh.Alerts.Raise(Disconnected));
        }

        // One alert, however many times the condition fired.
        var alert = Assert.Single(mesh.Alerts.Active());
        Assert.Equal(14, alert.SuppressedCount);

        var stats = mesh.Alerts.Stats();
        Assert.Equal(1, stats.Raised);
        Assert.Equal(14, stats.Suppressed);
        Assert.Equal(0, stats.Cleared);
        Assert.Equal(1, stats.Active);

        // The same suppression through the path a rejected reading takes: counted, and the latest reason is the one kept.
        var (rejected, _) = Create();
        rejected.Alerts.ReadingRejected(NodeA, TrustOutcome.RangeViolation, "31 is above 30");
        rejected.Alerts.ReadingRejected(NodeA, TrustOutcome.RangeViolation, "45 is above 30");

        var kept = Assert.Single(rejected.Alerts.Active());
        Assert.Equal(1, kept.SuppressedCount);
        Assert.Equal("45 is above 30", kept.Detail);
    }

    [Fact]
    public void A_repeat_does_not_write_history_and_only_the_first_occurrence_does()
    {
        var (mesh, clock) = Create();

        mesh.Alerts.Raise(Disconnected);
        mesh.Alerts.Raise(Disconnected);
        mesh.Alerts.Raise(Disconnected);

        // The history is for what happened. A repeat is the same thing continuing, and recording it would
        // bury the record under noise.
        var events = mesh.History.GetEvents(clock.GetUtcNow().AddHours(-1), clock.GetUtcNow().AddHours(1));

        var raised = Assert.Single(events);
        Assert.Equal(HistoryKind.AlertRaised, raised.Kind);
        Assert.Equal(AlertKind.Disconnected, raised.Event!.Alert);
    }

    [Fact]
    public void Different_kinds_on_one_node_and_the_same_kind_on_different_nodes_are_separate_alerts()
    {
        var (mesh, _) = Create();

        Assert.Equal(RaiseOutcome.Raised, mesh.Alerts.Raise(new AlertKey(NodeA, AlertKind.Disconnected)));
        Assert.Equal(RaiseOutcome.Raised, mesh.Alerts.Raise(new AlertKey(NodeA, AlertKind.OutOfRange)));
        Assert.Equal(RaiseOutcome.Raised, mesh.Alerts.Raise(new AlertKey(NodeB, AlertKind.Disconnected)));

        Assert.Equal(3, mesh.Alerts.Active().Count);
        Assert.Equal(0, mesh.Alerts.Stats().Suppressed);
    }

    [Fact]
    public void A_cleared_alert_leaves_the_set_and_the_next_occurrence_raises_it_again()
    {
        var (mesh, _) = Create();

        mesh.Alerts.Raise(Disconnected);
        mesh.Alerts.Raise(Disconnected);

        Assert.True(mesh.Alerts.Clear(Disconnected));
        Assert.Empty(mesh.Alerts.Active());

        // A new occurrence of a condition that had cleared is news, and starts its count from nothing.
        Assert.Equal(RaiseOutcome.Raised, mesh.Alerts.Raise(Disconnected));
        Assert.Equal(0, Assert.Single(mesh.Alerts.Active()).SuppressedCount);

        var stats = mesh.Alerts.Stats();
        Assert.Equal(2, stats.Raised);
        Assert.Equal(1, stats.Suppressed);
        Assert.Equal(1, stats.Cleared);
    }

    [Fact]
    public void Clearing_an_alert_that_is_not_active_changes_nothing()
    {
        var (mesh, _) = Create();

        Assert.False(mesh.Alerts.Clear(Disconnected));
        Assert.Equal(0, mesh.Alerts.Stats().Cleared);
    }

    [Fact]
    public void Raising_and_clearing_are_both_recorded_in_history()
    {
        var (mesh, clock) = Create();

        mesh.Alerts.Raise(OutOfRange, "42 is above 30");
        clock.Advance(TimeSpan.FromMinutes(1));
        mesh.Alerts.Clear(OutOfRange);

        var events = mesh.History.GetEvents(clock.GetUtcNow().AddHours(-1), clock.GetUtcNow().AddHours(1));

        Assert.Equal([HistoryKind.AlertRaised, HistoryKind.AlertCleared], events.Select(entry => entry.Kind));
        Assert.Equal("42 is above 30", events[0].Event!.Detail);
    }

    // ---- the key --------------------------------------------------------------------------------------------

    [Fact]
    public void Identical_keys_are_equal_hash_alike_and_one_entry_in_a_hash_set()
    {
        var first = new AlertKey("AA:BB:CC:00:00:01", AlertKind.Disconnected);
        var second = new AlertKey(string.Concat("AA:BB:CC:00:", "00:01"), AlertKind.Disconnected);

        Assert.False(ReferenceEquals(first.Mac, second.Mac));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        // The set is what the alert tracker uses: Add returning false is the "already raised" decision.
        var set = new HashSet<AlertKey>();
        Assert.True(set.Add(first));
        Assert.False(set.Add(second));
        Assert.Single(set);
    }

    [Fact]
    public void Keys_differing_in_either_member_are_different_and_a_class_key_would_not_be_compared_by_value()
    {
        Assert.NotEqual(new AlertKey(NodeA, AlertKind.Fault), new AlertKey(NodeB, AlertKind.Fault));
        Assert.NotEqual(new AlertKey(NodeA, AlertKind.Fault), new AlertKey(NodeA, AlertKind.Disconnected));

        // Why the key is a record struct: the same two members in a class compare by reference and defeat the set.
        var set = new HashSet<ClassKey>();
        Assert.True(set.Add(new ClassKey(NodeA, AlertKind.Fault)));
        Assert.True(set.Add(new ClassKey(NodeA, AlertKind.Fault)));
        Assert.Equal(2, set.Count);
    }

    private sealed class ClassKey(string mac, AlertKind kind)
    {
        public string Mac { get; } = mac;

        public AlertKind Kind { get; } = kind;
    }

    // ---- acknowledging --------------------------------------------------------------------------------------

    [Fact]
    public void Acknowledging_marks_the_alert_seen_and_does_not_clear_it()
    {
        var (mesh, _) = Create();
        mesh.Alerts.Raise(Disconnected);

        Assert.Equal(AcknowledgeOutcome.Acknowledged, mesh.Alerts.Acknowledge(Disconnected));

        // The node is still disconnected whether or not anyone has noticed.
        var alert = Assert.Single(mesh.Alerts.Active());
        Assert.True(alert.Acknowledged);
        Assert.Equal(1, mesh.Alerts.Stats().Active);
        Assert.Equal(0, mesh.Alerts.Stats().Cleared);

        // And the condition still firing is still a repeat of the same, now-acknowledged alert.
        Assert.Equal(RaiseOutcome.Suppressed, mesh.Alerts.Raise(Disconnected));
        Assert.True(Assert.Single(mesh.Alerts.Active()).Acknowledged);
    }

    [Fact]
    public void Acknowledging_twice_is_harmless()
    {
        var (mesh, _) = Create();
        mesh.Alerts.Raise(Disconnected);

        mesh.Alerts.Acknowledge(Disconnected);

        Assert.Equal(AcknowledgeOutcome.AlreadyAcknowledged, mesh.Alerts.Acknowledge(Disconnected));
        Assert.Equal(1, mesh.Alerts.Stats().Acknowledged);
    }

    [Fact]
    public void Acknowledging_an_alert_that_is_not_active_is_reported_and_creates_nothing()
    {
        var (mesh, _) = Create();

        Assert.Equal(AcknowledgeOutcome.NotActive, mesh.Alerts.Acknowledge(Disconnected));
        Assert.Empty(mesh.Alerts.Active());
    }

    [Fact]
    public void An_acknowledgement_does_not_carry_over_to_the_next_occurrence()
    {
        var (mesh, _) = Create();

        mesh.Alerts.Raise(Disconnected);
        mesh.Alerts.Acknowledge(Disconnected);
        mesh.Alerts.Clear(Disconnected);
        mesh.Alerts.Raise(Disconnected);

        // A new problem that nobody has seen yet.
        Assert.False(Assert.Single(mesh.Alerts.Active()).Acknowledged);
    }

    // ---- the heartbeat monitor's set -------------------------------------------------------------------------

    [Fact]
    public void A_silent_node_is_added_to_the_disconnected_set_once_and_raises_one_alert()
    {
        var (mesh, _) = Create();

        Assert.True(mesh.Alerts.NodeSilent(NodeA, "No readings for 20s."));

        for (var sweep = 0; sweep < 5; sweep++)
        {
            Assert.False(mesh.Alerts.NodeSilent(NodeA, "No readings for a while."));
        }

        Assert.Equal([NodeA], mesh.Alerts.DisconnectedNodes);

        var alert = Assert.Single(mesh.Alerts.Active());
        Assert.Equal(AlertKind.Disconnected, alert.Key.Kind);
        Assert.Equal(5, alert.SuppressedCount);
        Assert.True(mesh.Alerts.DisconnectedSetMatchesAlerts());
    }

    [Fact]
    public void A_node_that_is_heard_leaves_the_set_and_its_alert_clears()
    {
        var (mesh, _) = Create();
        mesh.Alerts.NodeSilent(NodeA, "silent");

        mesh.Alerts.NodeHeard(NodeA);

        Assert.Empty(mesh.Alerts.DisconnectedNodes);
        Assert.Empty(mesh.Alerts.Active());
        Assert.True(mesh.Alerts.DisconnectedSetMatchesAlerts());

        // And going silent again is a new alert.
        Assert.True(mesh.Alerts.NodeSilent(NodeA, "silent again"));
        Assert.Equal(2, mesh.Alerts.Stats().Raised);
    }

    [Fact]
    public void Hearing_a_node_that_was_never_silent_does_nothing()
    {
        var (mesh, _) = Create();

        mesh.Alerts.NodeHeard(NodeA);

        Assert.Equal(0, mesh.Alerts.Stats().Cleared);
        Assert.Empty(mesh.History.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void An_accepted_reading_ends_a_disconnect_straight_away()
    {
        var (mesh, _) = Create();
        mesh.Alerts.NodeSilent(NodeA, "silent");

        mesh.Alerts.ReadingAccepted(NodeA);

        Assert.Empty(mesh.Alerts.DisconnectedNodes);
        Assert.Empty(mesh.Alerts.Active());
    }

    // ---- readings: out of range and wrong type ---------------------------------------------------------------

    [Fact]
    public void A_rejected_reading_raises_the_alert_that_matches_why_it_was_rejected()
    {
        var (mesh, _) = Create();

        mesh.Alerts.ReadingRejected(NodeA, TrustOutcome.RangeViolation, "99 is above 30");
        mesh.Alerts.ReadingRejected(NodeB, TrustOutcome.SchemaFailure, "wrong type");

        Assert.Contains(mesh.Alerts.Active(), alert => alert.Key == new AlertKey(NodeA, AlertKind.OutOfRange));
        Assert.Contains(mesh.Alerts.Active(), alert => alert.Key == new AlertKey(NodeB, AlertKind.TypeViolation));
    }

    [Fact]
    public void Enough_consecutive_valid_readings_clear_an_out_of_range_alert()
    {
        var (mesh, _) = Create(options => options.ClearAfterValidReadings = 3);
        mesh.Alerts.ReadingRejected(NodeA, TrustOutcome.RangeViolation, "bad");

        mesh.Alerts.ReadingAccepted(NodeA);
        mesh.Alerts.ReadingAccepted(NodeA);
        Assert.Single(mesh.Alerts.Active());

        mesh.Alerts.ReadingAccepted(NodeA);
        Assert.Empty(mesh.Alerts.Active());
    }

    [Fact]
    public void A_bad_reading_restarts_the_count_towards_clearing()
    {
        var (mesh, _) = Create(options => options.ClearAfterValidReadings = 3);
        mesh.Alerts.ReadingRejected(NodeA, TrustOutcome.RangeViolation, "bad");

        mesh.Alerts.ReadingAccepted(NodeA);
        mesh.Alerts.ReadingAccepted(NodeA);
        mesh.Alerts.ReadingRejected(NodeA, TrustOutcome.RangeViolation, "bad again");
        mesh.Alerts.ReadingAccepted(NodeA);
        mesh.Alerts.ReadingAccepted(NodeA);

        // Four good readings in total but never three in a row since the last bad one, so it is still on.
        Assert.Single(mesh.Alerts.Active());

        mesh.Alerts.ReadingAccepted(NodeA);
        Assert.Empty(mesh.Alerts.Active());
    }

    [Fact]
    public void A_node_that_flaps_between_good_and_bad_does_not_clear_and_re_raise()
    {
        var (mesh, _) = Create(options => options.ClearAfterValidReadings = 3);

        for (var cycle = 0; cycle < 10; cycle++)
        {
            mesh.Alerts.ReadingRejected(NodeA, TrustOutcome.RangeViolation, "bad");
            mesh.Alerts.ReadingAccepted(NodeA);
        }

        // One alert, raised once, with nine repeats: the noise the set exists to remove.
        Assert.Equal(1, mesh.Alerts.Stats().Raised);
        Assert.Equal(9, mesh.Alerts.Stats().Suppressed);
        Assert.Equal(0, mesh.Alerts.Stats().Cleared);
    }

    [Fact]
    public void Valid_readings_from_a_healthy_node_cost_nothing_and_change_nothing()
    {
        var (mesh, _) = Create();

        for (var i = 0; i < 100; i++)
        {
            mesh.Alerts.ReadingAccepted(NodeA);
        }

        Assert.Empty(mesh.Alerts.Active());
        Assert.Equal(0, mesh.History.Count);
    }

    // ---- context ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_active_keys_are_a_stable_snapshot_for_recording_with_other_events()
    {
        var (mesh, _) = Create();
        mesh.Alerts.Raise(new AlertKey(NodeB, AlertKind.Fault));
        mesh.Alerts.Raise(new AlertKey(NodeA, AlertKind.OutOfRange));
        mesh.Alerts.Raise(new AlertKey(NodeA, AlertKind.Disconnected));

        var snapshot = mesh.Alerts.ActiveKeys();

        Assert.Equal(
            [new AlertKey(NodeA, AlertKind.Disconnected), new AlertKey(NodeA, AlertKind.OutOfRange), new AlertKey(NodeB, AlertKind.Fault)],
            snapshot);

        // A copy: later changes do not alter what was recorded.
        mesh.Alerts.Clear(new AlertKey(NodeB, AlertKind.Fault));
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public async Task Raising_from_many_threads_at_once_raises_exactly_once()
    {
        var (mesh, _) = Create();

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                mesh.Alerts.Raise(Disconnected);
            }
        }));

        await Task.WhenAll(tasks);

        // The Add-returns-false decision is atomic, so sixteen threads cannot each think they raised it.
        var stats = mesh.Alerts.Stats();
        Assert.Equal(1, stats.Raised);
        Assert.Equal((16 * 100) - 1, stats.Suppressed);
    }
}
