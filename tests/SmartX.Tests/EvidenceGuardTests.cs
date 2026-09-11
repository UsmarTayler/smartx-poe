using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the guard that stops evidence which has decayed to nothing from passing as a recovery.
/// </summary>
public class EvidenceGuardTests
{
    [Fact]
    public void Without_the_guard_vanishing_evidence_rounds_to_exactly_one_half()
    {
        // The failure being guarded, shown directly. Two failures to one success, all sixty half-lives
        // old: failures still outweigh successes, yet the score is exactly the prior, which the
        // thresholds would classify as degrading rather than fault.
        var decay = Math.Pow(0.5, 60);

        Assert.Equal(0.5, TrustFormula.Score(1 * decay, 2 * decay));
        Assert.True(3 * decay < TrustFormula.MinimumEvidence);
    }

    [Fact]
    public void A_fault_whose_evidence_decays_away_becomes_unproven_without_raising_cleared()
    {
        var (engine, clock) = Create();
        var cleared = 0;
        engine.FaultCleared += _ => cleared++;

        engine.Record(NodeA, TrustOutcome.Success);
        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 2);
        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);

        // Sixty half-lives, well past the point the raw score rounds to 0.5.
        clock.Advance(TimeSpan.FromHours(10));
        engine.Reevaluate();

        Assert.Equal(TrustBand.Unproven, Trust(engine, NodeA).Band);
        Assert.Equal(0, cleared);
        Assert.Empty(engine.DegradedNodes);
    }

    [Fact]
    public void A_node_whose_evidence_vanished_is_announced_again_if_it_faults_again()
    {
        // Decaying to unproven ends the fault episode, so a later failure is a fresh crossing.
        var (engine, clock) = Create();
        var entered = 0;
        engine.FaultEntered += _ => entered++;

        engine.Record(NodeA, TrustOutcome.SchemaFailure);
        clock.Advance(TimeSpan.FromHours(10));
        engine.Reevaluate();

        engine.Record(NodeA, TrustOutcome.SchemaFailure);

        Assert.Equal(2, entered);
        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);
    }

    [Fact]
    public void A_nominal_node_whose_evidence_decays_away_becomes_unproven_quietly()
    {
        var (engine, clock) = Create();
        var events = 0;
        engine.FaultEntered += _ => events++;
        engine.FaultCleared += _ => events++;

        RecordMany(engine, NodeA, TrustOutcome.Success, 5);

        clock.Advance(TimeSpan.FromHours(10));
        engine.Reevaluate();

        Assert.Equal(TrustBand.Unproven, Trust(engine, NodeA).Band);
        Assert.Equal(0, events);
    }

    [Fact]
    public void Old_but_still_meaningful_evidence_keeps_its_band()
    {
        // Twenty half-lives on, one type failure weighs about 2e-6: tiny, but three orders of
        // magnitude above the guard, and still correctly a fault because failures outweigh successes.
        var (engine, clock) = Create();
        engine.Record(NodeA, TrustOutcome.SchemaFailure);

        clock.Advance(TimeSpan.FromMinutes(200));
        engine.Reevaluate();

        Assert.Equal(TrustBand.Fault, Trust(engine, NodeA).Band);
    }
}
