using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the unproven band: nodes with no evidence for the engine to judge.
/// </summary>
public class UnprovenBandTests
{
    private const string NodeD = "AA:BB:CC:00:00:04";

    [Fact]
    public void A_tracked_node_with_no_outcomes_is_unproven_at_the_prior_score()
    {
        var (engine, _) = Create();

        engine.Track(NodeA);

        var trust = Trust(engine, NodeA);
        Assert.Equal(TrustBand.Unproven, trust.Band);
        Assert.Equal(0.5, trust.Score);
        Assert.Equal("no telemetry yet", trust.Reason);
    }

    [Fact]
    public void An_unproven_node_is_not_in_the_degraded_set()
    {
        // The degraded set is what alerting reads. A node that has not failed does not belong in it.
        var (engine, _) = Create();

        engine.Track(NodeA);

        Assert.Empty(engine.DegradedNodes);
    }

    [Fact]
    public void An_unproven_node_raises_no_events_however_often_it_is_reevaluated()
    {
        var (engine, clock) = Create();
        var events = 0;
        engine.FaultEntered += _ => events++;
        engine.FaultCleared += _ => events++;

        engine.Track(NodeA);

        for (var hour = 0; hour < 10; hour++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            engine.Reevaluate();
        }

        Assert.Equal(0, events);
        Assert.Equal(TrustBand.Unproven, Trust(engine, NodeA).Band);
    }

    [Fact]
    public void The_first_outcome_ends_the_unproven_state()
    {
        // One good packet is evidence, and 2/3 is a degrading score: seen working once, not yet trusted.
        var (engine, _) = Create();
        engine.Track(NodeA);

        engine.Record(NodeA, TrustOutcome.Success);

        Assert.Equal(TrustBand.Degrading, Trust(engine, NodeA).Band);
    }

    [Fact]
    public void A_first_outcome_bad_enough_for_fault_raises_the_event_from_unproven()
    {
        // Leaving unproven straight into fault is a real crossing on real evidence, so it is raised.
        var (engine, _) = Create();
        var raised = new List<TrustTransition>();
        engine.FaultEntered += raised.Add;
        engine.Track(NodeA);

        engine.Record(NodeA, TrustOutcome.SchemaFailure);

        var transition = Assert.Single(raised);
        Assert.Equal(TrustBand.Unproven, transition.PreviousBand);
        Assert.Equal(TrustBand.Fault, transition.Band);
    }

    [Fact]
    public void Worst_first_places_unproven_between_degrading_and_nominal()
    {
        // The unproven node's 0.5 is below the degrading node's 0.67, so a plain score sort would
        // rank it the more urgent of the two. Band severity decides first.
        var (engine, _) = Create();

        engine.Record(NodeA, TrustOutcome.SchemaFailure);        // 0.25, fault
        engine.Record(NodeB, TrustOutcome.Success);              // 0.67, degrading
        engine.Track(NodeC);                                     // 0.5, unproven
        RecordMany(engine, NodeD, TrustOutcome.Success, 3);      // 0.8, nominal

        Assert.Equal([NodeA, NodeB, NodeC, NodeD], engine.Snapshot().Select(node => node.MacAddress));
    }

    [Fact]
    public void Unproven_nodes_are_counted_but_kept_out_of_mesh_confidence()
    {
        var (engine, _) = Create();

        engine.Track(NodeA);
        engine.Track(NodeB);
        RecordMany(engine, NodeC, TrustOutcome.Success, 3);

        var summary = engine.Summary();

        Assert.Equal(3, summary.NodeCount);
        Assert.Equal(2, summary.Unproven);
        Assert.Equal(1, summary.Nominal);
        Assert.Equal(0.8, summary.MeshConfidence!.Value, 12);
        Assert.Equal(0.8, summary.LowestScore!.Value, 12);
        Assert.Equal(NodeC, summary.WorstNode);
    }

    [Fact]
    public void A_mesh_with_only_unproven_nodes_reports_no_confidence_figure()
    {
        // Null, not 0.5: there is no measurement to report.
        var (engine, _) = Create();

        engine.Track(NodeA);

        var summary = engine.Summary();

        Assert.Null(summary.MeshConfidence);
        Assert.Null(summary.LowestScore);
        Assert.Null(summary.WorstNode);
        Assert.Equal(1, summary.Unproven);
    }

    [Fact]
    public void Severity_orders_fault_then_degrading_then_unproven_then_nominal()
    {
        Assert.True(TrustFormula.Severity(TrustBand.Fault) < TrustFormula.Severity(TrustBand.Degrading));
        Assert.True(TrustFormula.Severity(TrustBand.Degrading) < TrustFormula.Severity(TrustBand.Unproven));
        Assert.True(TrustFormula.Severity(TrustBand.Unproven) < TrustFormula.Severity(TrustBand.Nominal));
    }
}
