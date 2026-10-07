using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>An operator's dismissal of a false alarm makes one node less sensitive to one failure, within a bound, and can be undone.</summary>
public class TrustSensitivityTests
{
    private const string Node = "B8:27:EB:40:00:01";

    private static (TrustEngine Engine, FakeTimeProvider Clock) Engine()
    {
        var clock = new FakeTimeProvider(T0);

        return (new TrustEngine(Options.Create(new TrustOptions()), clock, NullLogger<TrustEngine>.Instance), clock);
    }

    private static void Fault(TrustEngine engine, FakeTimeProvider clock)
    {
        for (var index = 0; index < 6; index++)
        {
            engine.Record(Node, TrustOutcome.Success);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        for (var index = 0; index < 9; index++)
        {
            engine.Record(Node, TrustOutcome.RangeViolation);
            clock.Advance(TimeSpan.FromSeconds(5));
        }
    }

    private static double Score(TrustEngine engine)
    {
        Assert.True(engine.TryGet(Node, out var snapshot));

        return snapshot.Score;
    }

    [Fact]
    public void The_failure_that_weighs_most_is_the_one_named_as_the_cause()
    {
        var (engine, clock) = Engine();
        Fault(engine, clock);

        Assert.Equal(TrustOutcome.RangeViolation, engine.DominantFailure(Node));
        Assert.Null(engine.DominantFailure("B8:27:EB:99:99:99"));
    }

    [Fact]
    public void A_dismissal_scales_that_failure_down_for_that_node_alone_and_raises_its_score()
    {
        var (engine, clock) = Engine();
        Fault(engine, clock);
        engine.Record("B8:27:EB:40:00:02", TrustOutcome.RangeViolation);

        var before = Score(engine);
        var change = engine.AdjustSensitivity(Node, TrustOutcome.RangeViolation, 0.5, 0.5);

        Assert.Equal(new SensitivityChange(TrustOutcome.RangeViolation, 1, 0.5), change);
        Assert.True(Score(engine) > before);

        // Other kinds of failure on the same node are untouched, and so is every other node.
        var scales = engine.Sensitivity(Node, 0.5);
        Assert.Equal(0.5, scales.Single(item => item.Outcome == TrustOutcome.RangeViolation).Scale);
        Assert.Equal(1, scales.Single(item => item.Outcome == TrustOutcome.SchemaFailure).Scale);
        Assert.All(engine.Sensitivity("B8:27:EB:40:00:02", 0.5), item => Assert.Equal(1, item.Scale));
    }

    [Fact]
    public void However_many_times_it_is_dismissed_the_scale_never_goes_below_the_floor()
    {
        var (engine, clock) = Engine();
        Fault(engine, clock);

        for (var index = 0; index < 50; index++)
        {
            engine.AdjustSensitivity(Node, TrustOutcome.RangeViolation, 0.85, 0.5);
        }

        var scale = engine.Sensitivity(Node, 0.5).Single(item => item.Outcome == TrustOutcome.RangeViolation).Scale;

        Assert.Equal(0.5, scale);

        // Detection is dulled and not off: a node with enough genuine failures is still in fault at the floor.
        for (var index = 0; index < 30; index++)
        {
            engine.Record(Node, TrustOutcome.RangeViolation);
        }

        Assert.True(engine.TryGet(Node, out var snapshot));
        Assert.Equal(TrustBand.Fault, snapshot.Band);
    }

    [Fact]
    public void Giving_the_sensitivity_back_restores_it_and_never_goes_past_untouched()
    {
        var (engine, clock) = Engine();
        Fault(engine, clock);

        var change = engine.AdjustSensitivity(Node, TrustOutcome.RangeViolation, 0.85, 0.5)!;
        var restored = engine.RestoreSensitivity(Node, TrustOutcome.RangeViolation, change.Before - change.After);

        Assert.Equal(1, restored?.After);
        Assert.Null(engine.RestoreSensitivity(Node, TrustOutcome.RangeViolation, 5));
    }

    [Fact]
    public void Sensitivity_to_success_cannot_be_adjusted_and_an_unknown_node_is_ignored()
    {
        var (engine, _) = Engine();

        Assert.Throws<ArgumentException>(() => engine.AdjustSensitivity(Node, TrustOutcome.Success, 0.5, 0.5));
        Assert.Null(engine.AdjustSensitivity("B8:27:EB:99:99:99", TrustOutcome.RangeViolation, 0.5, 0.5));
    }

    [Fact]
    public void A_node_never_adjusted_scores_exactly_as_it_did_before_this_feature_existed()
    {
        var (engine, clock) = Engine();
        var (control, controlClock) = Engine();

        Fault(engine, clock);
        Fault(control, controlClock);

        Assert.Equal(Score(control), Score(engine));
    }
}
