using SmartX.Api.Patterns;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>
/// Backdated history: decay from event time, seeded counted apart from observed, and what an accepted suggestion teaches.
/// </summary>
public class PatternReplayTests
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>The brief's habit <paramref name="times"/> times, one a day, ending <paramref name="daysAgo"/> days before <see cref="PatternKit.T0"/>.</summary>
    private static List<HistoricEvent> Habit(int times, double daysAgo, EventSource source = EventSource.Seeded)
    {
        List<HistoricEvent> history = [];

        for (var index = 0; index < times; index++)
        {
            var at = T0 - TimeSpan.FromDays(daysAgo) - index * Day;

            history.Add(new HistoricEvent(new TriggerEvent(TriggerKind.ThresholdCrossing, MoistB, "Low", at, source), null));
            history.Add(new HistoricEvent(null, new OperatorAction(OperatorActionKind.Viewed, PumpA, string.Empty, at + TimeSpan.FromMinutes(1), source)));
        }

        return history;
    }

    private static PatternAssociation Specific(PatternEngine engine, DateTimeOffset now) =>
        Assert.Single(All(engine, now, new PatternQuery(MacAddress: MoistB, Level: PatternLevel.Specific)),
            association => association.Action.Target == PumpA);

    [Fact]
    public void A_replayed_history_makes_the_pattern_qualify_as_if_the_engine_had_been_watching()
    {
        var engine = Engine();

        engine.Replay(Habit(times: 8, daysAgo: 0.5));

        var found = Specific(engine, T0);

        Assert.True(found.Qualified);
        Assert.Equal(8, found.Times);
        Assert.Equal(8, found.TriggerTimes);
        Assert.True(found.Lift > 10, $"Lift was {found.Lift}.");
    }

    [Fact]
    public void Decay_runs_from_when_each_event_happened_and_not_from_when_it_was_replayed()
    {
        var engine = Engine();

        // Eight habits, the latest seven days before the question is asked: each has faded by exactly one half-life.
        engine.Replay(Habit(times: 1, daysAgo: 7));

        var found = Specific(engine, T0);

        Assert.Equal(0.5, found.Support, precision: 3);
        Assert.Equal(0.5, found.TriggerCount, precision: 3);
    }

    [Fact]
    public void A_habit_from_a_week_ago_is_worth_less_than_the_same_habit_from_yesterday()
    {
        var old = Engine();
        var fresh = Engine();

        old.Replay(Habit(times: 4, daysAgo: 14));
        fresh.Replay(Habit(times: 4, daysAgo: 1));

        Assert.True(Specific(old, T0).Support < Specific(fresh, T0).Support / 2);
    }

    [Fact]
    public void History_replayed_after_something_live_still_fades_from_its_own_time()
    {
        var engine = Engine();

        // Live first: one habit just now. Then the history from three days earlier is replayed behind it.
        engine.RecordTrigger(Low(MoistB, T0 - TimeSpan.FromMinutes(5)));
        engine.RecordAction(View(PumpA, T0 - TimeSpan.FromMinutes(4)));
        engine.Replay(Habit(times: 1, daysAgo: 3));

        var found = Specific(engine, T0);

        // One at full weight, and one that has faded for three days of a seven-day half-life.
        Assert.Equal(1 + Math.Pow(0.5, 3d / 7), found.Support, precision: 2);
        Assert.Equal(2, found.Times);
        Assert.Equal(1, found.SeededTimes);
    }

    [Fact]
    public void Seeded_and_observed_are_counted_separately()
    {
        var engine = Engine();

        engine.Replay(Habit(times: 5, daysAgo: 1));

        for (var index = 0; index < 2; index++)
        {
            var at = T0 - TimeSpan.FromHours(3 - index);

            engine.RecordTrigger(Low(MoistB, at));
            engine.RecordAction(View(PumpA, at + TimeSpan.FromMinutes(1)));
        }

        var found = Specific(engine, T0);

        Assert.Equal(7, found.Times);
        Assert.Equal(5, found.SeededTimes);
        Assert.Equal(7, found.TriggerTimes);
        Assert.Equal(5, found.SeededTriggerTimes);
    }

    [Fact]
    public void Nothing_replayed_is_ever_counted_as_observed()
    {
        var engine = Engine();

        engine.Replay(Habit(times: 6, daysAgo: 2));

        var found = Specific(engine, T0);

        Assert.Equal(found.Times, found.SeededTimes);
        Assert.Equal(found.TriggerTimes, found.SeededTriggerTimes);
    }

    [Fact]
    public void A_replayed_action_is_not_paired_with_a_live_trigger_that_came_after_it()
    {
        var engine = Engine();

        engine.RecordTrigger(Low(MoistB, T0));

        // Replayed after the live trigger, but it happened days before it.
        engine.Replay(
        [
            new HistoricEvent(null, new OperatorAction(OperatorActionKind.Viewed, PumpA, string.Empty, T0 - Day, EventSource.Seeded)),
        ]);

        Assert.Empty(All(engine, T0, new PatternQuery(MacAddress: MoistB, Level: PatternLevel.Specific)));
        Assert.Equal(1, engine.TriggersInWindow);
    }

    [Fact]
    public void A_live_action_is_still_paired_with_a_live_trigger_after_a_replay()
    {
        var engine = Engine();

        engine.RecordTrigger(Low(MoistB, T0));
        engine.Replay(Habit(times: 1, daysAgo: 3));
        engine.RecordAction(View(PumpA, T0 + TimeSpan.FromMinutes(1)));

        var found = Specific(engine, T0 + TimeSpan.FromMinutes(2));

        Assert.Equal(2, found.Times);
        Assert.Equal(1, found.SeededTimes);
    }

    [Fact]
    public void The_baseline_is_measured_over_the_history_that_was_replayed()
    {
        var engine = Engine();

        engine.Replay(Habit(times: 6, daysAgo: 1));

        // Six habits over six days: the pump was opened for ten minutes of nearly nine thousand, so it is a rare thing.
        Assert.True(Specific(engine, T0).Baseline < 0.05);
    }

    [Fact]
    public void Replaying_nothing_changes_nothing()
    {
        var engine = Engine();

        engine.Replay([]);

        Assert.Empty(All(engine, T0));
    }

    // ---- what an accepted suggestion teaches ------------------------------------------------------------------------

    private static PatternEngine WithHabit(Action<PatternOptions>? configure = null)
    {
        var engine = Engine(configure);

        engine.Replay(Habit(times: 4, daysAgo: 1));

        return engine;
    }

    private static void Respond(PatternEngine engine, bool fromSuggestion)
    {
        engine.RecordTrigger(Low(MoistB, T0));
        engine.RecordAction(new OperatorAction(OperatorActionKind.Viewed, PumpA, string.Empty, T0 + TimeSpan.FromMinutes(1), FromSuggestion: fromSuggestion));
    }

    [Fact]
    public void An_action_taken_by_accepting_a_suggestion_teaches_the_engine_nothing_by_default()
    {
        var own = WithHabit();
        var advised = WithHabit();

        Respond(own, fromSuggestion: false);
        Respond(advised, fromSuggestion: true);

        var before = Specific(WithHabit(), T0 + TimeSpan.FromMinutes(2));
        var afterOwn = Specific(own, T0 + TimeSpan.FromMinutes(2));
        var afterAdvised = Specific(advised, T0 + TimeSpan.FromMinutes(2));

        Assert.True(afterOwn.Support > before.Support + 0.9);
        Assert.Equal(before.Support, afterAdvised.Support, precision: 6);
        Assert.Equal(before.Times, afterAdvised.Times);
    }

    [Fact]
    public void Accepting_a_suggestion_does_not_raise_its_confidence_or_its_lift()
    {
        var engine = WithHabit();
        var before = Specific(engine, T0 + TimeSpan.FromMinutes(2));

        for (var round = 0; round < 5; round++)
        {
            var at = T0 + TimeSpan.FromHours(round);

            engine.RecordTrigger(Low(MoistB, at));
            engine.RecordAction(new OperatorAction(OperatorActionKind.Viewed, PumpA, string.Empty, at + TimeSpan.FromMinutes(1), FromSuggestion: true));
        }

        var after = Specific(engine, T0 + TimeSpan.FromHours(5));

        // The trigger happened five more times and the action, by the engine's own advice, was never counted as following
        // it: confidence can only have fallen, and the support the pattern rests on has not moved.
        Assert.True(after.Confidence <= before.Confidence);
        Assert.Equal(before.Times, after.Times);
    }

    [Fact]
    public void A_configured_weight_counts_an_accepted_suggestion_for_part_of_an_action_of_the_operators_own()
    {
        var engine = WithHabit(options => options.AcceptedSuggestionWeight = 0.5);
        var before = Specific(engine, T0 + TimeSpan.FromMinutes(2));

        Respond(engine, fromSuggestion: true);

        var after = Specific(engine, T0 + TimeSpan.FromMinutes(2));

        Assert.Equal(before.Support + 0.5, after.Support, precision: 2);
    }

    [Fact]
    public void An_accepted_suggestion_is_still_announced_so_the_suggestion_can_be_withdrawn()
    {
        var engine = WithHabit();
        List<OperatorAction> heard = [];
        engine.ActionRecorded += heard.Add;

        Respond(engine, fromSuggestion: true);

        Assert.Single(heard);
        Assert.True(heard[0].FromSuggestion);
    }

    // ---- what is announced ------------------------------------------------------------------------------------------

    [Fact]
    public void Observed_triggers_and_actions_are_announced_and_replayed_ones_are_not()
    {
        var engine = Engine();
        List<TriggerEvent> triggers = [];
        List<OperatorAction> actions = [];
        engine.TriggerRecorded += triggers.Add;
        engine.ActionRecorded += actions.Add;

        engine.Replay(Habit(times: 3, daysAgo: 1));

        Assert.Empty(triggers);
        Assert.Empty(actions);

        engine.RecordTrigger(Low(MoistB, T0));
        engine.RecordAction(View(PumpA, T0 + TimeSpan.FromMinutes(1)));

        Assert.Single(triggers);
        Assert.Single(actions);
    }

    [Fact]
    public void The_validation_rejects_an_accepted_suggestion_weight_outside_zero_to_one()
    {
        Assert.NotEmpty(PatternOptionsValidation.Problems(new PatternOptions { AcceptedSuggestionWeight = 1.5 }));
        Assert.NotEmpty(PatternOptionsValidation.Problems(new PatternOptions { AcceptedSuggestionWeight = -0.1 }));
        Assert.Empty(PatternOptionsValidation.Problems(new PatternOptions { AcceptedSuggestionWeight = 0 }));
    }
}
