using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers how the engine turns silence into missed-heartbeat penalties.
/// </summary>
public class HeartbeatPenaltyTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void Silence_is_penalised_once_per_missed_interval_not_once_per_check()
    {
        // Three and a half minutes of silence against a one-minute heartbeat is three missed
        // intervals. Checking again immediately finds nothing new, however often it is asked.
        var (engine, clock) = Create();
        engine.Record(NodeA, TrustOutcome.Success);

        clock.Advance(TimeSpan.FromSeconds(210));

        Assert.Equal(3, engine.CheckHeartbeat(NodeA, OneMinute));
        Assert.Equal(0, engine.CheckHeartbeat(NodeA, OneMinute));
        Assert.Equal(0, engine.CheckHeartbeat(NodeA, OneMinute));

        clock.Advance(OneMinute);
        Assert.Equal(1, engine.CheckHeartbeat(NodeA, OneMinute));
    }

    [Fact]
    public void A_packet_restarts_the_heartbeat_clock()
    {
        var (engine, clock) = Create();
        engine.Record(NodeA, TrustOutcome.Success);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, engine.CheckHeartbeat(NodeA, OneMinute));

        engine.Record(NodeA, TrustOutcome.Success);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, engine.CheckHeartbeat(NodeA, OneMinute));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, engine.CheckHeartbeat(NodeA, OneMinute));
    }

    [Fact]
    public void A_node_that_registered_and_never_reported_is_still_caught()
    {
        // Measured from when tracking began. This is the silent disconnect in its purest form.
        var (engine, clock) = Create();
        engine.Track(NodeA);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(2, engine.CheckHeartbeat(NodeA, OneMinute));
        Assert.StartsWith("no packets for", Trust(engine, NodeA).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_healthy_node_that_goes_silent_falls_into_fault_and_raises_the_event_once()
    {
        var (engine, clock) = Create();
        var raised = new List<TrustTransition>();
        engine.FaultEntered += raised.Add;

        RecordMany(engine, NodeA, TrustOutcome.Success, 10);
        Assert.Equal(TrustBand.Nominal, Trust(engine, NodeA).Band);

        for (var minute = 0; minute < 10; minute++)
        {
            clock.Advance(OneMinute);
            engine.CheckHeartbeat(NodeA, OneMinute);
        }

        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);

        var transition = Assert.Single(raised);
        Assert.StartsWith("no packets for", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Decay_alone_never_clears_a_fault()
    {
        // s = 2, f = 4. Ageing scales both by the same factor, and the score is below 0.5 exactly
        // when s < f — so waiting alone cannot recover this node. Only new evidence can.
        var (engine, clock) = Create();
        var cleared = 0;
        engine.FaultCleared += _ => cleared++;

        RecordMany(engine, NodeA, TrustOutcome.Success, 2);
        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 2);
        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);

        clock.Advance(TimeSpan.FromHours(2));
        engine.Reevaluate();

        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);
        Assert.Equal(0, cleared);
    }
}
