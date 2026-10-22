using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers scoring, recency, transitions, the degraded set and reasons, all against a manual clock.
/// </summary>
/// <remarks>
/// Every outcome in a test is recorded at the same instant unless the test advances the clock, so
/// decay is exactly 1 and the scores can be asserted as exact fractions.
/// </remarks>
public class TrustEngineTests
{
    [Fact]
    public void A_fresh_node_is_unproven_rather_than_nominal()
    {
        // No outcomes at all, so nothing to judge: unproven, even though 0.5 lies in the degrading range.
        var (engine, _) = Create();

        engine.Track(NodeA);

        Assert.Equal(TrustBand.Unproven, Trust(engine, NodeA).Band);
    }

    [Fact]
    public void Two_clean_packets_are_what_it_takes_to_earn_nominal()
    {
        var (engine, _) = Create();

        engine.Record(NodeA, TrustOutcome.Success);
        Assert.Equal(TrustBand.Degrading, Trust(engine, NodeA).Band);

        // (2 + 1) / (2 + 0 + 2) = 0.75, exactly on the nominal threshold.
        engine.Record(NodeA, TrustOutcome.Success);
        Assert.Equal(0.75, Trust(engine, NodeA).Score);
        Assert.Equal(TrustBand.Nominal, Trust(engine, NodeA).Band);
    }

    [Fact]
    public void Consecutive_successes_drive_trust_up()
    {
        var (engine, _) = Create();
        engine.Track(NodeA);

        var previous = Trust(engine, NodeA).Score;

        for (var packet = 0; packet < 6; packet++)
        {
            engine.Record(NodeA, TrustOutcome.Success);

            var current = Trust(engine, NodeA).Score;
            Assert.True(current > previous, $"Packet {packet} did not raise trust.");
            previous = current;
        }
    }

    [Fact]
    public void Consecutive_failures_drive_trust_down()
    {
        var (engine, _) = Create();
        engine.Track(NodeA);

        var previous = Trust(engine, NodeA).Score;

        for (var packet = 0; packet < 6; packet++)
        {
            engine.Record(NodeA, TrustOutcome.RangeViolation);

            var current = Trust(engine, NodeA).Score;
            Assert.True(current < previous, $"Failure {packet} did not lower trust.");
            previous = current;
        }

        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);
    }

    [Fact]
    public void Heavier_failures_move_the_score_further()
    {
        // One of each, all at the same instant: f = 1, 2 and 3 respectively.
        var (engine, clock) = Create();
        engine.Track(NodeC);
        clock.Advance(TimeSpan.FromSeconds(60));

        engine.Record(NodeA, TrustOutcome.RangeViolation);
        engine.Record(NodeB, TrustOutcome.SchemaFailure);
        engine.CheckHeartbeat(NodeC, TimeSpan.FromSeconds(60));

        Assert.Equal(1.0 / 3, Trust(engine, NodeA).Score, 12);
        Assert.Equal(1.0 / 4, Trust(engine, NodeB).Score, 12);
        Assert.Equal(1.0 / 5, Trust(engine, NodeC).Score, 12);
    }

    [Fact]
    public void A_node_that_failed_then_recovered_returns_to_nominal()
    {
        var (engine, clock) = Create();

        // A bad burst: f = 12, deep in the fault band.
        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 6);
        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);

        // An hour of clean reporting, one packet a minute. Six half-lives on, the burst counts for
        // 1/64 of what it did.
        for (var minute = 0; minute < 60; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            engine.Record(NodeA, TrustOutcome.Success);
        }

        var trust = Trust(engine, NodeA);
        Assert.Equal(TrustBand.Nominal, trust.Band);
        Assert.True(trust.Score > 0.9, $"Expected near-full recovery, got {trust.Score}.");
    }

    [Fact]
    public void Without_decay_the_same_history_would_not_have_recovered()
    {
        // The counterfactual that shows the recency weighting is doing the work. With a half-life
        // so long that nothing ages, the old burst still weighs as much as it did.
        var (engine, clock) = Create(options => options.HalfLife = TimeSpan.FromDays(3650));

        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 6);

        for (var minute = 0; minute < 10; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            engine.Record(NodeA, TrustOutcome.Success);
        }

        // s = 10, f = 12: (11) / (24) ≈ 0.46, still a fault.
        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);
    }

    [Fact]
    public void Decay_alone_returns_old_failures_toward_uncertainty_not_toward_trust()
    {
        // With no new evidence, time can only erase what is known, not invent good behaviour. The
        // score drifts back toward the 0.5 prior and stops short of it.
        var (engine, clock) = Create();
        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 4);

        clock.Advance(TimeSpan.FromHours(1));
        engine.Reevaluate();

        Assert.InRange(Trust(engine, NodeA).Score, 0.45, 0.5);
    }

    [Fact]
    public void Falling_into_fault_raises_the_event_exactly_once_however_many_packets_fail()
    {
        // The node crosses into fault within the first few failures, then keeps failing for another
        // ninety-odd packets. One crossing, one event. An event per packet would be an alert flood.
        var (engine, _) = Create();
        var raised = new List<TrustTransition>();
        engine.FaultEntered += raised.Add;

        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 100);

        var transition = Assert.Single(raised);
        Assert.Equal(NodeA, transition.MacAddress);
        Assert.Equal(TrustBand.Fault, transition.Band);
        Assert.Equal(TrustBand.Unproven, transition.PreviousBand);
        Assert.False(string.IsNullOrWhiteSpace(transition.Reason));
    }

    [Fact]
    public void Recovering_out_of_fault_raises_the_upward_transition_once()
    {
        var (engine, clock) = Create();
        var entered = 0;
        var cleared = new List<TrustTransition>();
        engine.FaultEntered += _ => entered++;
        engine.FaultCleared += cleared.Add;

        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 6);
        var faultedAt = clock.GetUtcNow();

        for (var minute = 0; minute < 60; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            engine.Record(NodeA, TrustOutcome.Success);
        }

        Assert.Equal(1, entered);

        var recovery = Assert.Single(cleared);
        Assert.Equal(TrustBand.Fault, recovery.PreviousBand);
        Assert.NotEqual(TrustBand.Fault, recovery.Band);

        // The recovery window starts when the node entered fault.
        Assert.Equal(faultedAt, recovery.WindowStart);
    }

    [Fact]
    public void The_fault_window_starts_at_the_last_moment_the_node_was_nominal()
    {
        var (engine, clock) = Create();
        TrustTransition? transition = null;
        engine.FaultEntered += t => transition = t;

        RecordMany(engine, NodeA, TrustOutcome.Success, 4);
        var lastHealthy = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromMinutes(3));
        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 10);

        Assert.NotNull(transition);
        Assert.Equal(lastHealthy, transition.WindowStart);
        Assert.Equal(clock.GetUtcNow(), transition.WindowEnd);
    }

    [Fact]
    public void Tracking_a_new_node_never_raises_a_fault()
    {
        var (engine, _) = Create();
        var raised = 0;
        engine.FaultEntered += _ => raised++;

        engine.Track(NodeA);
        engine.Reevaluate();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void The_degraded_set_holds_one_entry_per_node_regardless_of_failure_count()
    {
        var (engine, _) = Create();

        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 500);
        Assert.Equal([NodeA], engine.DegradedNodes);

        RecordMany(engine, NodeB, TrustOutcome.RangeViolation, 500);
        Assert.Equal(2, engine.DegradedNodes.Count);
    }

    [Fact]
    public void A_node_leaves_the_degraded_set_once_it_is_nominal_again()
    {
        var (engine, _) = Create();

        engine.Track(NodeA);
        engine.Record(NodeA, TrustOutcome.RangeViolation);
        Assert.Contains(NodeA, engine.DegradedNodes);

        RecordMany(engine, NodeA, TrustOutcome.Success, 5);
        Assert.DoesNotContain(NodeA, engine.DegradedNodes);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_stop_ingestion_or_other_subscribers()
    {
        var (engine, _) = Create();
        var heard = 0;
        engine.FaultEntered += _ => throw new InvalidOperationException("broken subscriber");
        engine.FaultEntered += _ => heard++;

        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 10);

        Assert.Equal(1, heard);
        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);
    }
}
