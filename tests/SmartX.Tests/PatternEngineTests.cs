using SmartX.Api.Patterns;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>
/// The pattern engine: the brief's example learned literally, then what keeps it honest (the window, counting once, lift,
/// minimum support, decay) and what makes it general (two levels, and which one answers).
/// </summary>
public class PatternEngineTests
{
    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    [Fact]
    public void The_brief_example_is_learned_a_moisture_drop_followed_by_opening_the_pump()
    {
        var engine = Engine();

        // "If a user frequently searches for Pump A right after Moisture Sensor B drops below 20%": five times, half an hour apart.
        for (var round = 0; round < 5; round++)
        {
            Respond(engine, MoistB, PumpA, T0 + round * HalfHour);
        }

        var now = T0 + 4 * HalfHour + TimeSpan.FromMinutes(2);
        var found = engine.Associations(new PatternQuery(MacAddress: MoistB, Level: PatternLevel.Specific), now);

        var pattern = Assert.Single(found);
        Assert.Equal(TriggerKind.ThresholdCrossing, pattern.Trigger.Kind);
        Assert.Equal("Low", pattern.Trigger.Detail);
        Assert.Equal(MoistB, pattern.Trigger.Scope);
        Assert.Equal(PumpA, pattern.Action.Target);
        Assert.Equal(OperatorActionKind.Viewed, pattern.Action.Kind);
        Assert.True(pattern.Qualified);

        // Five pairings (a hair under, from a week-long half-life over two hours), every trigger answered, and the action
        // happening far more after the trigger than it does in a window picked at random.
        Assert.InRange(pattern.Support, 4.8, 5.0);
        Assert.InRange(pattern.Confidence, 0.99, 1.0);
        Assert.InRange(pattern.Baseline, 0.30, 0.38);
        Assert.InRange(pattern.Lift, 2.5, 3.5);
    }

    [Fact]
    public void An_action_after_the_window_does_not_pair_but_one_exactly_at_its_edge_does()
    {
        var late = Engine();
        late.RecordTrigger(Low(MoistB, T0));
        late.RecordAction(View(PumpA, T0 + TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1)));

        var edge = Engine();
        edge.RecordTrigger(Low(MoistB, T0));
        edge.RecordAction(View(PumpA, T0 + TimeSpan.FromMinutes(10)));

        Assert.Empty(All(late, T0 + TimeSpan.FromMinutes(11)));
        Assert.NotEmpty(All(edge, T0 + TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void The_same_action_repeated_after_one_trigger_is_one_response()
    {
        var engine = Engine();

        engine.RecordTrigger(Low(MoistB, T0));

        for (var minute = 1; minute <= 4; minute++)
        {
            engine.RecordAction(View(PumpA, T0 + TimeSpan.FromMinutes(minute)));
        }

        var pairing = Assert.Single(All(engine, T0 + TimeSpan.FromMinutes(5), new PatternQuery(Level: PatternLevel.Specific)));

        // Four opens of Pump A after one moisture drop is one response, not four.
        Assert.InRange(pairing.Support, 0.99, 1.0);
    }

    [Fact]
    public void A_second_trigger_gets_its_own_response_to_count()
    {
        var engine = Engine();

        Respond(engine, MoistB, PumpA, T0);
        Respond(engine, MoistB, PumpA, T0 + HalfHour);

        var pairing = Assert.Single(All(engine, T0 + HalfHour + TimeSpan.FromMinutes(2), new PatternQuery(Level: PatternLevel.Specific)));

        Assert.InRange(pairing.Support, 1.9, 2.0);
    }

    [Fact]
    public void An_action_that_happens_all_the_time_is_not_a_pattern_whatever_its_confidence()
    {
        var engine = Engine();
        List<(DateTimeOffset At, Action Do)> events = [];

        // The Busy valve's page is open all day: viewed every two minutes for three hours.
        for (var minute = 0; minute <= 180; minute += 2)
        {
            var at = T0 + TimeSpan.FromMinutes(minute);
            events.Add((at, () => engine.RecordAction(View(Busy, at))));
        }

        // Six moisture drops, each followed by Pump A, which is opened at no other time.
        for (var minute = 10; minute <= 160; minute += 30)
        {
            var at = T0 + TimeSpan.FromMinutes(minute);
            events.Add((at, () => engine.RecordTrigger(Low(MoistB, at))));
            events.Add((at + TimeSpan.FromMinutes(1), () => engine.RecordAction(View(PumpA, at + TimeSpan.FromMinutes(1)))));
        }

        Replay(engine, events);

        var now = T0 + TimeSpan.FromMinutes(181);
        var found = All(engine, now, new PatternQuery(Level: PatternLevel.Specific));
        var busy = found.Single(item => item.Action.Target == Busy);
        var pump = found.Single(item => item.Action.Target == PumpA);

        // The Busy valve follows every trigger, so its confidence is as high as the pump's, and it still tells us nothing.
        Assert.InRange(busy.Confidence, 0.99, 1.0);
        Assert.InRange(busy.Lift, 0.95, 1.1);
        Assert.False(busy.Qualified);

        Assert.True(pump.Qualified);
        Assert.True(pump.Lift > 3);

        // And only the pump is offered by default.
        var offered = engine.Associations(new PatternQuery(Level: PatternLevel.Specific), now);
        Assert.Equal(PumpA, Assert.Single(offered).Action.Target);
    }

    [Fact]
    public void A_coincidence_is_not_a_pattern_until_it_has_enough_support()
    {
        var engine = Engine();

        Respond(engine, MoistB, PumpA, T0);
        Respond(engine, MoistB, PumpA, T0 + HalfHour);

        var now = T0 + HalfHour + TimeSpan.FromMinutes(2);
        var pairing = Assert.Single(All(engine, now, new PatternQuery(Level: PatternLevel.Specific)));

        // High lift, full confidence, and still not enough: twice could be chance.
        Assert.True(pairing.Lift >= 1.5);
        Assert.InRange(pairing.Confidence, 0.99, 1.0);
        Assert.False(pairing.Qualified);
        Assert.Empty(engine.Associations(new PatternQuery(), now));

        Respond(engine, MoistB, PumpA, T0 + 2 * HalfHour);

        Assert.True(Assert.Single(engine.Associations(new PatternQuery(Level: PatternLevel.Specific), T0 + 2 * HalfHour + TimeSpan.FromMinutes(2))).Qualified);
    }

    [Fact]
    public void A_search_counts_less_than_opening_a_device()
    {
        var engine = Engine();

        // Four rounds, each with a search of Pump A: 4 x 0.4 = 1.6, short of three.
        for (var round = 0; round < 4; round++)
        {
            var at = T0 + round * HalfHour;
            engine.RecordTrigger(Low(MoistB, at));
            engine.RecordAction(new OperatorAction(OperatorActionKind.Searched, PumpA, string.Empty, at + TimeSpan.FromMinutes(1)));
        }

        var searched = Assert.Single(All(engine, T0 + 3 * HalfHour + TimeSpan.FromMinutes(2), new PatternQuery(Level: PatternLevel.Specific)));

        Assert.Equal(OperatorActionKind.Searched, searched.Action.Kind);
        Assert.InRange(searched.Support, 1.55, 1.61);
        Assert.False(searched.Qualified);
    }

    [Fact]
    public void Old_habits_fade_and_a_new_habit_overtakes_them()
    {
        // A day's half-life makes ten days of drift easy to show; the default is a week.
        var engine = Engine(options => options.HalfLife = TimeSpan.FromDays(1));

        for (var round = 0; round < 6; round++)
        {
            Respond(engine, MoistB, PumpA, T0 + TimeSpan.FromHours(round));
        }

        var later = T0 + TimeSpan.FromDays(10);

        for (var round = 0; round < 4; round++)
        {
            Respond(engine, MoistB, PumpB, later + TimeSpan.FromHours(round));
        }

        var now = later + TimeSpan.FromHours(3) + TimeSpan.FromMinutes(2);
        var found = All(engine, now, new PatternQuery(Level: PatternLevel.Specific));

        Assert.Equal(PumpB, found[0].Action.Target);
        Assert.True(found[0].Qualified);

        var old = found.Single(item => item.Action.Target == PumpA);

        // Six pairings ten half-lives ago are worth about six thousandths of one.
        Assert.True(old.Support < 0.05);
        Assert.False(old.Qualified);
    }

    [Fact]
    public void A_pattern_learned_on_one_sensor_answers_for_another_of_its_kind_in_the_same_zone_and_not_in_another()
    {
        var engine = Engine();

        for (var round = 0; round < 4; round++)
        {
            Respond(engine, MoistB, PumpA, T0 + round * HalfHour);
        }

        var now = T0 + 3 * HalfHour + TimeSpan.FromMinutes(5);

        // MoistC has never had an incident, and is in Zone 1 like MoistB (which is in a sub-zone of it).
        var sameZone = engine.Predict(TriggerKind.ThresholdCrossing, "Low", MoistC, now);

        Assert.Equal(PatternLevel.General, sameZone.AnsweredBy);
        var suggestion = Assert.Single(sameZone.Associations);
        Assert.Equal("Actuator/Pump", suggestion.Action.Target);
        Assert.Equal("pump", suggestion.Action.TargetLabel);
        Assert.Equal("%RH sensor in Zone 1", sameZone.Trigger.ScopeLabel);

        // MoistD is in Zone 4, which has no such history.
        var otherZone = engine.Predict(TriggerKind.ThresholdCrossing, "Low", MoistD, now);

        Assert.Null(otherZone.AnsweredBy);
        Assert.Empty(otherZone.Associations);
    }

    [Fact]
    public void Specific_beats_general_when_it_has_enough_support_and_general_answers_when_it_does_not()
    {
        var engine = Engine();

        // Zone 1's moisture sensors have teaching Pump A, three times, through MoistB.
        for (var round = 0; round < 3; round++)
        {
            Respond(engine, MoistB, PumpA, T0 + round * HalfHour);
        }

        // MoistC has its own habit: Pump B, three times.
        for (var round = 0; round < 3; round++)
        {
            Respond(engine, MoistC, PumpB, T0 + TimeSpan.FromHours(3) + round * HalfHour);
        }

        var now = T0 + TimeSpan.FromHours(5);

        var own = engine.Predict(TriggerKind.ThresholdCrossing, "Low", MoistC, now);

        Assert.Equal(PatternLevel.Specific, own.AnsweredBy);
        Assert.Equal(PumpB, own.Associations[0].Action.Target);

        // MoistE has no history, so it gets what is known about its kind: a pump in the zone.
        var none = engine.Predict(TriggerKind.ThresholdCrossing, "Low", MoistE, now);

        Assert.Equal(PatternLevel.General, none.AnsweredBy);
        Assert.Equal("Actuator/Pump", none.Associations[0].Action.Target);

        // MoistD has one coincidence of its own with Pump X, which is not enough, and Zone 4 has nothing general either.
        Respond(engine, MoistD, PumpX, T0 + TimeSpan.FromHours(5));
        var thin = engine.Predict(TriggerKind.ThresholdCrossing, "Low", MoistD, T0 + TimeSpan.FromHours(5) + TimeSpan.FromMinutes(2));

        Assert.Null(thin.AnsweredBy);
    }

    [Fact]
    public void An_action_in_another_zone_teaches_the_specific_level_and_not_the_general_one()
    {
        var engine = Engine();

        // MoistB is in Zone 1; Pump X is in Zone 4. Operators do open it after MoistB drops.
        for (var round = 0; round < 3; round++)
        {
            Respond(engine, MoistB, PumpX, T0 + round * HalfHour);
        }

        var now = T0 + 2 * HalfHour + TimeSpan.FromMinutes(2);

        Assert.NotEmpty(All(engine, now, new PatternQuery(Level: PatternLevel.Specific)));

        // "A pump in the same zone" is not what happened, so there is nothing general to learn.
        Assert.Empty(All(engine, now, new PatternQuery(Level: PatternLevel.General)));
    }

    [Fact]
    public void For_a_device_asks_each_kind_of_trigger_seen_and_says_which_level_answered()
    {
        var engine = Engine();

        for (var round = 0; round < 3; round++)
        {
            Respond(engine, MoistB, PumpA, T0 + round * HalfHour);
        }

        var now = T0 + 2 * HalfHour + TimeSpan.FromMinutes(5);

        var ofItself = engine.ForDevice(MoistB, now);
        var ofItsKind = engine.ForDevice(MoistC, now);
        var elsewhere = engine.ForDevice(MoistD, now);

        Assert.Equal(PatternLevel.Specific, Assert.Single(ofItself).AnsweredBy);
        Assert.Equal(PatternLevel.General, Assert.Single(ofItsKind).AnsweredBy);
        Assert.Empty(elsewhere);
    }

    [Fact]
    public void The_window_is_bounded_so_a_flood_of_triggers_cannot_make_an_action_slower()
    {
        var engine = Engine(options => options.MaxTriggersInWindow = 10);

        for (var index = 0; index < 25; index++)
        {
            engine.RecordTrigger(Low(MoistB, T0 + TimeSpan.FromSeconds(index)));
        }

        Assert.Equal(10, engine.TriggersInWindow);

        // An action long after the window has passed clears it.
        engine.RecordAction(View(PumpA, T0 + TimeSpan.FromHours(1)));

        Assert.Equal(0, engine.TriggersInWindow);
    }

    [Fact]
    public void Triggers_the_directory_does_not_know_are_still_learned_at_the_specific_level()
    {
        var engine = Engine();
        const string stranger = "B8:27:EB:99:99:99";

        for (var round = 0; round < 3; round++)
        {
            engine.RecordTrigger(Low(stranger, T0 + round * HalfHour));
            engine.RecordAction(View(PumpA, T0 + round * HalfHour + TimeSpan.FromMinutes(1)));
        }

        var found = All(engine, T0 + 2 * HalfHour + TimeSpan.FromMinutes(2));

        Assert.All(found, item => Assert.Equal(PatternLevel.Specific, item.Level));
        Assert.NotEmpty(found);
    }
}
