using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Patterns;
using SmartX.Api.Search;
using SmartX.Api.Storage;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>Ranking, explaining, withdrawing and feedback: what the suggestion service offers and why.</summary>
public class SuggestionServiceTests
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>An engine, a clock and a service that listens to the engine, the way the API wires them.</summary>
    internal sealed class Kit
    {
        public required PatternEngine Engine { get; init; }

        public required SuggestionService Service { get; init; }

        public required ManualTimeProvider Clock { get; init; }

        public int Changes { get; private set; }

        public static Kit Create(Action<SuggestionOptions>? suggestions = null, Action<PatternOptions>? patterns = null, FakeDirectory? directory = null)
        {
            var patternOptions = new PatternOptions();
            patterns?.Invoke(patternOptions);

            var suggestionOptions = new SuggestionOptions();
            suggestions?.Invoke(suggestionOptions);

            var clock = new ManualTimeProvider(T0);
            var fake = directory ?? Directory();
            var engine = new PatternEngine(Options.Create(patternOptions), fake);
            var service = new SuggestionService(
                engine,
                fake,
                Options.Create(suggestionOptions),
                Options.Create(patternOptions),
                clock,
                NullLogger<SuggestionService>.Instance,
                processInline: true);

            var kit = new Kit { Engine = engine, Service = service, Clock = clock };
            service.Changed += () => kit.Changes++;

            return kit;
        }

        public IReadOnlyList<Suggestion> Suggestions => Service.Current.Suggestions;

        /// <summary>A trigger that happens now, as the engine is told of it.</summary>
        public void Trigger(string mac, string detail = "Low", TriggerKind kind = TriggerKind.ThresholdCrossing) =>
            Engine.RecordTrigger(new TriggerEvent(kind, mac, detail, Clock.GetUtcNow()));

        public void View(string mac) => Engine.RecordAction(new OperatorAction(OperatorActionKind.Viewed, mac, string.Empty, Clock.GetUtcNow()));

        public void Send(string mac, CommandAction action) =>
            Engine.RecordAction(new OperatorAction(OperatorActionKind.Command, mac, action.ToString(), Clock.GetUtcNow()));

        public void Advance(TimeSpan by) => Clock.Advance(by);
    }

    /// <summary>The brief's habit as a seeded history: a drop, then the pump opened and turned on, <paramref name="times"/> times, a day apart.</summary>
    internal static List<HistoricEvent> Habit(string sensor, string device, int times, double firstDaysAgo = 1, bool alsoTurnOn = true, string detail = "Low")
    {
        List<HistoricEvent> history = [];

        for (var index = 0; index < times; index++)
        {
            var at = T0 - TimeSpan.FromDays(firstDaysAgo) - index * Day;

            history.Add(new HistoricEvent(new TriggerEvent(TriggerKind.ThresholdCrossing, sensor, detail, at, EventSource.Seeded), null));
            history.Add(new HistoricEvent(null, new OperatorAction(OperatorActionKind.Viewed, device, string.Empty, at + TimeSpan.FromMinutes(1), EventSource.Seeded)));

            if (alsoTurnOn)
            {
                history.Add(new HistoricEvent(null, new OperatorAction(OperatorActionKind.Command, device, nameof(CommandAction.PumpOn), at + TimeSpan.FromSeconds(70), EventSource.Seeded)));
            }
        }

        return history;
    }

    private static Kit Seeded(Action<SuggestionOptions>? suggestions = null, Action<PatternOptions>? patterns = null)
    {
        var kit = Kit.Create(suggestions, patterns);
        kit.Engine.Replay(Habit(MoistB, PumpA, times: 5));

        return kit;
    }

    // ---- the brief's example ----------------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_engine_with_seeded_history_suggests_the_pump_the_moment_the_sensor_drops()
    {
        var kit = Seeded();

        Assert.Empty(kit.Suggestions);

        kit.Trigger(MoistB);

        Assert.Contains(kit.Suggestions, suggestion => suggestion.What == "Turn on Pump A");
        Assert.Contains(kit.Suggestions, suggestion => suggestion.What == "Open Pump A");
        Assert.True(kit.Changes >= 1);
    }

    [Fact]
    public void Every_suggestion_says_what_why_and_the_evidence()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);

        var suggestion = Assert.Single(kit.Suggestions, found => found.What == "Turn on Pump A");

        Assert.Contains("Moisture B dropped below 20", suggestion.Why, StringComparison.Ordinal);
        Assert.Equal(T0, suggestion.TriggeredAt);
        Assert.Equal(T0 + TimeSpan.FromMinutes(10), suggestion.ExpiresAt);
        Assert.Equal(PatternLevel.Specific, suggestion.Level);
        Assert.Equal("This device's own pattern", suggestion.LevelNote);
        Assert.Equal(SuggestedActionKind.Command, suggestion.Action.Kind);
        Assert.Equal(CommandAction.PumpOn, suggestion.Action.Command);
        Assert.Equal(PumpA, suggestion.Action.MacAddress);
        Assert.Equal("Turn on Pump A", suggestion.Action.Label);
        Assert.InRange(suggestion.Score, 0, 1);
    }

    [Fact]
    public void The_evidence_is_counted_and_says_how_much_of_it_is_seeded()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);

        var suggestion = Assert.Single(kit.Suggestions, found => found.What == "Turn on Pump A");

        Assert.Equal(5, suggestion.Evidence.Times);
        Assert.Equal(5, suggestion.Evidence.SeededTimes);

        // The occurrence that is happening now has not been answered yet, so it is not one of the times it was or was not.
        Assert.Equal(5, suggestion.Evidence.OfTimes);
        Assert.Contains("Operators did this 5 of the last 5 times (5 from seeded history).", suggestion.Evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("more likely than usual", suggestion.Evidence.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Observed_evidence_is_counted_apart_from_the_seeded()
    {
        var kit = Seeded();

        // One real occasion, hours ago, observed: the pump was opened after the sensor dropped.
        kit.Advance(TimeSpan.FromHours(2));
        kit.Trigger(MoistB);
        kit.Advance(TimeSpan.FromMinutes(1));
        kit.View(PumpA);
        kit.Advance(TimeSpan.FromMinutes(11));

        // And now again.
        kit.Trigger(MoistB);

        var suggestion = Assert.Single(kit.Suggestions, found => found.What == "Open Pump A");

        Assert.Equal(6, suggestion.Evidence.Times);
        Assert.Equal(5, suggestion.Evidence.SeededTimes);
        Assert.Equal(6, suggestion.Evidence.OfTimes);
        Assert.Contains("6 of the last 6 times (5 from seeded history)", suggestion.Evidence.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void With_nothing_seeded_the_evidence_does_not_mention_seeding()
    {
        var kit = Kit.Create();

        // Three observed habits, then a fourth drop.
        for (var round = 0; round < 3; round++)
        {
            kit.Trigger(MoistB);
            kit.Advance(TimeSpan.FromMinutes(1));
            kit.View(PumpA);
            kit.Advance(TimeSpan.FromMinutes(40));
        }

        kit.Trigger(MoistB);

        var suggestion = Assert.Single(kit.Suggestions, found => found.What == "Open Pump A");

        Assert.Equal(0, suggestion.Evidence.SeededTimes);
        Assert.DoesNotContain("seeded", suggestion.Evidence.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_is_suggested_for_a_trigger_that_has_no_pattern()
    {
        var kit = Seeded();

        kit.Trigger(PumpB, detail: "High");
        kit.Trigger(MoistD);

        Assert.Empty(kit.Suggestions);
    }

    // ---- ranking ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pattern_with_higher_lift_ranks_above_one_with_less()
    {
        var kit = Kit.Create();
        var rare = Habit(MoistB, PumpA, times: 5, alsoTurnOn: false);

        // Pump B follows just as reliably, but it is opened all the time anyway, so the trigger tells you less about it.
        var common = Habit(MoistB, PumpB, times: 5, alsoTurnOn: false);

        for (var minute = 0; minute < 24 * 60; minute += 7)
        {
            common.Add(new HistoricEvent(null, new OperatorAction(OperatorActionKind.Viewed, PumpB, string.Empty, T0 - TimeSpan.FromMinutes(minute) - TimeSpan.FromHours(12), EventSource.Seeded)));
        }

        kit.Engine.Replay([.. rare, .. common]);
        kit.Trigger(MoistB);

        var open = kit.Suggestions.Where(found => found.What.StartsWith("Open", StringComparison.Ordinal)).ToList();

        Assert.Equal("Open Pump A", open[0].What);
        Assert.True(kit.Suggestions.Count(found => found.What == "Open Pump B") == 0 || open.IndexOf(open.Single(found => found.What == "Open Pump B")) > 0);
    }

    [Fact]
    public void A_more_severe_trigger_ranks_above_a_less_severe_one_with_the_same_pattern()
    {
        var kit = Kit.Create();

        // The same habit after a threshold crossing on one sensor and after a node falling into Fault on another.
        kit.Engine.Replay(Habit(MoistB, PumpA, times: 5, alsoTurnOn: false));

        kit.Engine.Replay(
            Habit(MoistC, PumpB, times: 5, firstDaysAgo: 1.5, alsoTurnOn: false)
                .Select(item => item.Trigger is { } trigger
                    ? new HistoricEvent(trigger with { Kind = TriggerKind.TrustBand, Detail = nameof(TrustBand.Fault) }, null)
                    : item)
                .ToList());

        kit.Trigger(MoistB);
        kit.Trigger(MoistC, nameof(TrustBand.Fault), TriggerKind.TrustBand);

        Assert.Equal("Open Pump B", kit.Suggestions[0].What);
        Assert.True(kit.Suggestions[0].Score > kit.Suggestions.Single(found => found.What == "Open Pump A").Score);
    }

    [Fact]
    public void A_fresher_trigger_ranks_above_an_older_one_of_the_same_kind()
    {
        var kit = Kit.Create();
        kit.Engine.Replay([.. Habit(MoistB, PumpA, times: 5, alsoTurnOn: false), .. Habit(MoistC, PumpB, times: 5, firstDaysAgo: 1.5, alsoTurnOn: false)]);

        kit.Trigger(MoistB);
        kit.Advance(TimeSpan.FromMinutes(6));
        kit.Trigger(MoistC);

        var a = kit.Suggestions.Single(found => found.What == "Open Pump A");
        var b = kit.Suggestions.Single(found => found.What == "Open Pump B");

        Assert.True(b.Score > a.Score);
    }

    [Fact]
    public void Only_the_top_k_are_kept()
    {
        var kit = Kit.Create(options => options.MaxSuggestions = 2);

        kit.Engine.Replay([.. Habit(MoistB, PumpA, times: 5), .. Habit(MoistB, PumpB, times: 5, alsoTurnOn: false)]);
        kit.Trigger(MoistB);

        // Four candidates: open and turn on Pump A, open Pump B ... held to two.
        Assert.Equal(2, kit.Suggestions.Count);
        Assert.True(kit.Suggestions[0].Score >= kit.Suggestions[1].Score);
    }

    [Fact]
    public void The_two_kept_are_the_two_best_of_all_the_candidates()
    {
        var everything = Kit.Create(options => options.MaxSuggestions = 10);
        var top = Kit.Create(options => options.MaxSuggestions = 2);

        foreach (var kit in new[] { everything, top })
        {
            kit.Engine.Replay([.. Habit(MoistB, PumpA, times: 5), .. Habit(MoistB, PumpB, times: 5, alsoTurnOn: false)]);
            kit.Trigger(MoistB);
        }

        Assert.Equal(
            everything.Suggestions.Take(2).Select(found => found.What),
            top.Suggestions.Select(found => found.What));
    }

    [Fact]
    public void The_same_action_offered_by_two_triggers_is_one_suggestion()
    {
        var kit = Kit.Create();
        kit.Engine.Replay([.. Habit(MoistB, PumpA, times: 5, alsoTurnOn: false), .. Habit(MoistC, PumpA, times: 5, alsoTurnOn: false)]);

        kit.Trigger(MoistB);
        kit.Trigger(MoistC);

        Assert.Single(kit.Suggestions, found => found.What == "Open Pump A");
    }

    [Fact]
    public void The_ranking_is_the_same_whichever_order_things_were_learned_in()
    {
        var one = Kit.Create();
        var two = Kit.Create();

        one.Engine.Replay([.. Habit(MoistB, PumpA, times: 5), .. Habit(MoistB, PumpB, times: 5, alsoTurnOn: false)]);
        two.Engine.Replay([.. Habit(MoistB, PumpB, times: 5, alsoTurnOn: false), .. Habit(MoistB, PumpA, times: 5)]);
        one.Trigger(MoistB);
        two.Trigger(MoistB);

        Assert.Equal(one.Suggestions.Select(found => found.What), two.Suggestions.Select(found => found.What));
    }

    [Fact]
    public void Weights_are_normalised_so_the_score_stays_between_zero_and_one()
    {
        var kit = Seeded(options =>
        {
            options.LiftWeight = 5;
            options.ConfidenceWeight = 5;
            options.SeverityWeight = 5;
            options.RecencyWeight = 5;
        });

        kit.Trigger(MoistB);

        Assert.All(kit.Suggestions, found => Assert.InRange(found.Score, 0, 1));
    }

    // ---- what is done is not suggested ------------------------------------------------------------------------------

    [Fact]
    public void A_suggestion_disappears_once_its_action_has_been_taken()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);

        Assert.Contains(kit.Suggestions, found => found.What == "Open Pump A");
        var before = kit.Changes;

        kit.Advance(TimeSpan.FromSeconds(20));
        kit.View(PumpA);

        Assert.DoesNotContain(kit.Suggestions, found => found.What == "Open Pump A");
        Assert.Contains(kit.Suggestions, found => found.What == "Turn on Pump A");
        Assert.True(kit.Changes > before);
    }

    [Fact]
    public void Sending_the_command_removes_the_command_suggestion_and_not_the_other()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);

        kit.Send(PumpA, CommandAction.PumpOn);

        Assert.DoesNotContain(kit.Suggestions, found => found.What == "Turn on Pump A");
        Assert.Contains(kit.Suggestions, found => found.What == "Open Pump A");
    }

    [Fact]
    public void An_action_from_before_the_trigger_does_not_remove_it()
    {
        var kit = Seeded();

        kit.Advance(TimeSpan.FromMinutes(1));
        kit.Engine.RecordAction(new OperatorAction(OperatorActionKind.Viewed, PumpA, string.Empty, T0 - TimeSpan.FromSeconds(30)));
        kit.Trigger(MoistB);

        Assert.Contains(kit.Suggestions, found => found.What == "Open Pump A");
    }

    [Fact]
    public void A_search_does_not_count_as_having_opened_the_device()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);

        kit.Engine.RecordAction(new OperatorAction(OperatorActionKind.Searched, PumpA, string.Empty, T0));

        Assert.Contains(kit.Suggestions, found => found.What == "Open Pump A");
    }

    [Fact]
    public void A_fresh_trigger_brings_the_suggestion_back_after_it_was_done()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        kit.View(PumpA);
        Assert.DoesNotContain(kit.Suggestions, found => found.What == "Open Pump A");

        kit.Advance(TimeSpan.FromMinutes(1));
        kit.Trigger(MoistB);

        Assert.Contains(kit.Suggestions, found => found.What == "Open Pump A");
    }

    // ---- ageing out -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_suggestion_ends_when_its_trigger_leaves_the_window()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var before = kit.Changes;

        kit.Advance(TimeSpan.FromMinutes(11));
        kit.Service.Sweep();

        Assert.Empty(kit.Suggestions);
        Assert.True(kit.Changes > before);
    }

    [Fact]
    public void A_score_drifting_as_a_trigger_ages_does_not_announce_a_change()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var before = kit.Changes;
        var score = kit.Suggestions[0].Score;

        kit.Advance(TimeSpan.FromMinutes(2));
        kit.Service.Sweep();

        Assert.Equal(before, kit.Changes);
        Assert.True(kit.Suggestions[0].Score < score);
    }

    // ---- the general level ------------------------------------------------------------------------------------------

    [Fact]
    public void A_device_with_no_history_of_its_own_gets_what_similar_devices_in_its_zone_got()
    {
        var kit = Kit.Create();

        // Zone 1's moisture sensors have led to a pump being opened, several times, on two different sensors.
        kit.Engine.Replay([.. Habit(MoistB, PumpA, times: 3, alsoTurnOn: false), .. Habit(MoistC, PumpA, times: 3, firstDaysAgo: 4.5, alsoTurnOn: false)]);

        // Moisture E has never had an incident of its own.
        kit.Trigger(MoistE);

        var suggestion = kit.Suggestions.First();

        Assert.Equal(PatternLevel.General, suggestion.Level);
        Assert.StartsWith("Similar devices:", suggestion.LevelNote, StringComparison.Ordinal);
        Assert.Contains("Zone 1", suggestion.LevelNote, StringComparison.Ordinal);
        Assert.Contains("similar devices", suggestion.Evidence.Summary, StringComparison.Ordinal);

        // A pattern about "a pump in Zone 1" is turned into pumps that can be clicked: both of them, in address order.
        Assert.Contains(kit.Suggestions, found => found.What == "Open Pump A");
        Assert.Contains(kit.Suggestions, found => found.What == "Open Pump B");
    }

    [Fact]
    public void Opening_any_pump_in_the_zone_satisfies_a_pattern_about_pumps_in_the_zone()
    {
        var kit = Kit.Create();
        kit.Engine.Replay([.. Habit(MoistB, PumpA, times: 3, alsoTurnOn: false), .. Habit(MoistC, PumpA, times: 3, firstDaysAgo: 4.5, alsoTurnOn: false)]);

        kit.Trigger(MoistE);
        kit.View(PumpB);

        Assert.DoesNotContain(kit.Suggestions, found => found.Level == PatternLevel.General);
    }

    [Fact]
    public void How_many_devices_a_general_pattern_becomes_is_bounded()
    {
        var kit = Kit.Create(options => options.MaxDevicesPerGeneralSuggestion = 1);
        kit.Engine.Replay([.. Habit(MoistB, PumpA, times: 3, alsoTurnOn: false), .. Habit(MoistC, PumpA, times: 3, firstDaysAgo: 4.5, alsoTurnOn: false)]);

        kit.Trigger(MoistE);

        Assert.Single(kit.Suggestions);
    }

    // ---- "Not useful" -----------------------------------------------------------------------------------------------

    private static Suggestion Open(Kit kit) => kit.Suggestions.Single(found => found.What == "Open Pump A");

    [Fact]
    public void Marking_a_suggestion_not_useful_removes_it_and_lowers_the_weight_of_its_pattern()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var id = Open(kit).Id;

        Assert.Equal(SuggestionOutcome.Done, kit.Service.Dismiss(id));

        Assert.DoesNotContain(kit.Suggestions, found => found.Id == id);
        Assert.Contains(kit.Service.Current.Dismissed, found => found.Suggestion.Id == id && found.WeightAfter == 0.85);

        // The next time the same pattern fires, it is weighted less.
        kit.Advance(TimeSpan.FromMinutes(11));
        kit.Trigger(MoistB);

        Assert.Equal(0.85, Open(kit).Weight, precision: 6);
    }

    [Fact]
    public void A_lowered_pattern_scores_lower_than_it_did()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var first = Open(kit).Score;
        kit.Service.Dismiss(Open(kit).Id);

        kit.Advance(TimeSpan.FromMinutes(11));
        kit.Trigger(MoistB);

        Assert.True(Open(kit).Score < first);
    }

    [Fact]
    public void Dismissing_a_suggestion_does_not_dismiss_the_others()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);

        kit.Service.Dismiss(Open(kit).Id);

        Assert.Contains(kit.Suggestions, found => found.What == "Turn on Pump A");
        Assert.Equal(1.0, kit.Suggestions.Single(found => found.What == "Turn on Pump A").Weight);
    }

    [Fact]
    public void The_weight_is_lowered_in_steps_and_never_below_the_floor()
    {
        var kit = Seeded();

        for (var round = 0; round < 12; round++)
        {
            kit.Trigger(MoistB);
            kit.Service.Dismiss(Open(kit).Id);
            kit.Advance(TimeSpan.FromMinutes(11));
        }

        kit.Trigger(MoistB);

        Assert.Equal(0.5, Open(kit).Weight, precision: 6);
        Assert.True(Open(kit).Score > 0);
    }

    [Fact]
    public void Taking_it_back_gives_the_weight_back_exactly()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var id = Open(kit).Id;
        var score = Open(kit).Score;

        kit.Service.Dismiss(id);

        Assert.Equal(SuggestionOutcome.Done, kit.Service.Restore(id));

        Assert.Contains(kit.Suggestions, found => found.Id == id);
        Assert.Equal(1.0, Open(kit).Weight);
        Assert.Equal(score, Open(kit).Score, precision: 6);
        Assert.Empty(kit.Service.Current.Dismissed);
    }

    [Fact]
    public void Taking_back_a_dismissal_that_hit_the_floor_gives_back_exactly_what_was_taken_and_no_more()
    {
        var kit = Seeded(options => options.NotUsefulFloor = 0.8);

        // The first takes 0.15, the second only 0.05, because the floor stops it: 0.2 in all.
        kit.Trigger(MoistB);
        var id = Open(kit).Id;
        kit.Service.Dismiss(id);
        kit.Advance(TimeSpan.FromMinutes(11));
        kit.Trigger(MoistB);
        kit.Service.Dismiss(Open(kit).Id);

        var record = Assert.Single(kit.Service.Current.Dismissed, found => found.Suggestion.What == "Open Pump A");

        Assert.Equal(1.0, record.WeightBefore);
        Assert.Equal(0.8, record.WeightAfter, precision: 6);

        kit.Service.Restore(id);
        kit.Advance(TimeSpan.FromMinutes(11));
        kit.Trigger(MoistB);

        // Back to exactly what it was, and not above it.
        Assert.Equal(1.0, Open(kit).Weight, precision: 6);
    }

    [Fact]
    public void Dismissing_or_restoring_something_that_is_not_held_says_so()
    {
        var kit = Seeded();

        Assert.Equal(SuggestionOutcome.NotFound, kit.Service.Dismiss("nope"));
        Assert.Equal(SuggestionOutcome.NotFound, kit.Service.Restore("nope"));
    }

    [Fact]
    public void The_dismissed_list_is_bounded()
    {
        var kit = Kit.Create(options => options.MaxDismissedRetained = 2);
        kit.Engine.Replay([.. Habit(MoistB, PumpA, times: 5), .. Habit(MoistB, PumpB, times: 5, alsoTurnOn: false)]);
        kit.Trigger(MoistB);

        // Four different suggestions, each dismissed: the list keeps the last two.
        foreach (var id in kit.Suggestions.Select(found => found.Id).ToList())
        {
            kit.Service.Dismiss(id);
        }

        Assert.Equal(2, kit.Service.Current.Dismissed.Count);
    }

    [Fact]
    public void Dismissing_the_same_suggestion_on_two_occurrences_is_one_entry_and_taking_it_back_gives_back_both()
    {
        var kit = Seeded();

        for (var round = 0; round < 2; round++)
        {
            kit.Trigger(MoistB);
            kit.Service.Dismiss(Open(kit).Id);
            kit.Advance(TimeSpan.FromMinutes(11));
        }

        var record = Assert.Single(kit.Service.Current.Dismissed, found => found.Suggestion.What == "Open Pump A");

        Assert.Equal(1.0, record.WeightBefore);
        Assert.Equal(0.7225, record.WeightAfter, precision: 6);

        kit.Service.Restore(record.Suggestion.Id);
        kit.Trigger(MoistB);

        Assert.Equal(1.0, Open(kit).Weight);
    }

    [Fact]
    public void A_trigger_that_fires_again_before_anything_is_done_is_the_same_suggestion_with_the_later_time()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var first = Open(kit);

        kit.Advance(TimeSpan.FromMinutes(2));
        kit.Trigger(MoistB);

        var second = Open(kit);

        // The same id, so a client does not see a new suggestion, and the time it is about has moved on.
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(T0 + TimeSpan.FromMinutes(2), second.TriggeredAt);
    }

    [Fact]
    public void A_trigger_that_only_moves_a_suggestions_time_is_announced_at_most_once_a_second()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var announced = kit.Changes;

        // A sensor hovering about its level fires again and again. Nothing about the suggestion has changed but when.
        for (var round = 0; round < 20; round++)
        {
            kit.Advance(TimeSpan.FromMilliseconds(40));
            kit.Trigger(MoistB);
        }

        Assert.Equal(announced, kit.Changes);

        // The state still moved on, and it is announced once a second has passed.
        kit.Advance(TimeSpan.FromSeconds(1));
        kit.Trigger(MoistB);

        Assert.Equal(announced + 1, kit.Changes);
        Assert.Equal(kit.Clock.GetUtcNow(), Open(kit).TriggeredAt);
    }

    [Fact]
    public void A_held_back_time_change_is_announced_by_the_next_sweep_even_if_nothing_else_happens()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        kit.Advance(TimeSpan.FromMilliseconds(100));
        kit.Trigger(MoistB);
        var announced = kit.Changes;

        kit.Advance(TimeSpan.FromSeconds(2));
        kit.Service.Sweep();

        Assert.Equal(announced + 1, kit.Changes);
    }

    [Fact]
    public void A_dismissed_suggestion_stays_hidden_until_its_trigger_fires_again()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var id = Open(kit).Id;

        kit.Service.Dismiss(id);
        kit.Advance(TimeSpan.FromMinutes(1));
        kit.Service.Sweep();

        Assert.DoesNotContain(kit.Suggestions, found => found.Id == id);

        kit.Trigger(MoistB);

        Assert.Contains(kit.Suggestions, found => found.Id == id);
    }

    [Fact]
    public void Dismissing_announces_a_change()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var before = kit.Changes;

        kit.Service.Dismiss(Open(kit).Id);

        Assert.Equal(before + 1, kit.Changes);
    }

    // ---- accepting --------------------------------------------------------------------------------------------------

    [Fact]
    public void Accepting_a_suggestion_is_used_once_by_the_action_it_announced()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);

        Assert.True(kit.Service.Accept(Open(kit).Id));

        Assert.True(kit.Service.TakeAcceptance(OperatorActionKind.Viewed, PumpA, string.Empty));
        Assert.False(kit.Service.TakeAcceptance(OperatorActionKind.Viewed, PumpA, string.Empty));
    }

    [Fact]
    public void Accepting_registers_the_command_the_suggestion_would_send()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);

        kit.Service.Accept(kit.Suggestions.Single(found => found.What == "Turn on Pump A").Id);

        Assert.False(kit.Service.TakeAcceptance(OperatorActionKind.Viewed, PumpA, string.Empty));
        Assert.True(kit.Service.TakeAcceptance(OperatorActionKind.Command, PumpA, nameof(CommandAction.PumpOn)));
    }

    [Fact]
    public void An_acceptance_that_is_never_followed_by_its_action_lapses()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        kit.Service.Accept(Open(kit).Id);

        kit.Advance(TimeSpan.FromSeconds(31));

        Assert.False(kit.Service.TakeAcceptance(OperatorActionKind.Viewed, PumpA, string.Empty));
    }

    [Fact]
    public void Accepting_something_that_is_not_held_registers_nothing()
    {
        var kit = Seeded();

        Assert.False(kit.Service.Accept("nope"));
        Assert.False(kit.Service.TakeAcceptance(OperatorActionKind.Viewed, PumpA, string.Empty));
    }

    [Fact]
    public void Accepting_a_suggestion_and_doing_it_does_not_make_the_pattern_more_confident_and_still_removes_the_suggestion()
    {
        var kit = Seeded();
        kit.Trigger(MoistB);
        var before = Assert.Single(All(kit.Engine, T0, new PatternQuery(MacAddress: MoistB, Level: PatternLevel.Specific)), found => found.Action.Target == PumpA && found.Action.Kind == OperatorActionKind.Viewed);

        kit.Service.Accept(Open(kit).Id);

        // What the API does: the registration is consumed by the action that follows, which is flagged accordingly.
        var fromSuggestion = kit.Service.TakeAcceptance(OperatorActionKind.Viewed, PumpA, string.Empty);
        kit.Engine.RecordAction(new OperatorAction(OperatorActionKind.Viewed, PumpA, string.Empty, T0 + TimeSpan.FromSeconds(30), FromSuggestion: fromSuggestion));

        var after = Assert.Single(All(kit.Engine, T0 + TimeSpan.FromSeconds(30), new PatternQuery(MacAddress: MoistB, Level: PatternLevel.Specific)), found => found.Action.Target == PumpA && found.Action.Kind == OperatorActionKind.Viewed);

        Assert.True(fromSuggestion);
        Assert.Equal(before.Times, after.Times);
        Assert.Equal(before.Support, after.Support, precision: 3);
        Assert.DoesNotContain(kit.Suggestions, found => found.What == "Open Pump A");
    }

    // ---- bounds and wording -----------------------------------------------------------------------------------------

    [Fact]
    public void The_live_triggers_are_bounded()
    {
        var kit = Seeded(options => options.MaxLiveTriggers = 3);

        for (var index = 0; index < 20; index++)
        {
            kit.Trigger(MoistE);
            kit.Advance(TimeSpan.FromSeconds(1));
        }

        kit.Trigger(MoistB);

        Assert.Contains(kit.Suggestions, found => found.What == "Open Pump A");
    }

    [Theory]
    [InlineData(TriggerKind.TrustBand, "Fault", "Moisture B's trust fell into Fault")]
    [InlineData(TriggerKind.AlertRaised, "OutOfRange", "Moisture B raised a out-of-range alert")]
    [InlineData(TriggerKind.CriticalReading, "Pressure", "Moisture B reported a critical reading (Pressure)")]
    public void Each_kind_of_trigger_is_described_in_words(TriggerKind kind, string detail, string expected)
    {
        var kit = Kit.Create();
        kit.Engine.Replay(
            Habit(MoistB, PumpA, times: 5, alsoTurnOn: false)
                .Select(item => item.Trigger is { } trigger ? new HistoricEvent(trigger with { Kind = kind, Detail = detail }, null) : item)
                .ToList());

        kit.Trigger(MoistB, detail, kind);

        Assert.Equal(expected, Open(kit).Why);
    }

    [Fact]
    public void A_high_crossing_says_rose_above_and_gives_the_level()
    {
        var kit = Kit.Create();
        kit.Engine.Replay(Habit(MoistB, PumpA, times: 5, alsoTurnOn: false, detail: "High"));

        kit.Trigger(MoistB, "High");

        Assert.Contains("rose above 80", Open(kit).Why, StringComparison.Ordinal);
    }

    // ---- the locks it must not hold together ------------------------------------------------------------------------

    [Fact]
    public async Task An_event_raised_from_inside_the_device_indexs_lock_cannot_deadlock_with_ranking()
    {
        // The alert set raises its changes inside the lock it shares with the device index, and an alert is a trigger, so the engine
        // can be told something while that lock is held. Ranking asks the index about devices while it holds its own lock. Done on the
        // thread that raised the event, the two would wait for each other for ever. Here one thread raises triggers from inside the
        // index's lock while another ranks, many times over; a deadlock is a test that does not finish.
        var index = new DeviceIndex();

        foreach (var device in DemoDevices.All)
        {
            Assert.True(device.Registration.TryCreateRegistration(out var registration, out _));
            index.Register(registration);
        }

        var directory = new DeviceDirectory(index, Options.Create(new PatternOptions())) { Catalogue = new SmartX.Shared.ZoneCatalogue(DemoZoneHierarchy.Zones) };
        var clock = new ManualTimeProvider(T0);
        var engine = new PatternEngine(Options.Create(new PatternOptions()), directory);
        var service = new SuggestionService(
            engine, directory, Options.Create(new SuggestionOptions()), Options.Create(new PatternOptions()), clock, NullLogger<SuggestionService>.Instance);

        engine.Replay(SeededHistory.Build(T0, new SeededHistoryOptions()));
        await service.StartAsync(CancellationToken.None);

        var freezer = DemoDevices.Mac(0x18);

        var raising = Task.Run(() =>
        {
            for (var round = 0; round < 3_000; round++)
            {
                lock (index.Gate)
                {
                    engine.RecordTrigger(new TriggerEvent(TriggerKind.ThresholdCrossing, freezer, "High", T0.AddMilliseconds(round)));
                }
            }
        });

        var ranking = Task.Run(() =>
        {
            for (var round = 0; round < 3_000; round++)
            {
                service.Sweep();
            }
        });

        await Task.WhenAll(raising, ranking).WaitAsync(TimeSpan.FromSeconds(30));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Events_are_worked_through_off_the_callers_thread_when_the_service_is_running()
    {
        var directory = Directory();
        var clock = new ManualTimeProvider(T0);
        var engine = new PatternEngine(Options.Create(new PatternOptions()), directory);
        var service = new SuggestionService(
            engine, directory, Options.Create(new SuggestionOptions()), Options.Create(new PatternOptions()), clock, NullLogger<SuggestionService>.Instance);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => changed.TrySetResult();

        engine.Replay(Habit(MoistB, PumpA, times: 5));
        await service.StartAsync(CancellationToken.None);

        engine.RecordTrigger(Low(MoistB, T0));

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(service.Current.Suggestions, found => found.What == "Open Pump A");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_options_are_validated()
    {
        Assert.Empty(SuggestionOptionsValidation.Problems(new SuggestionOptions()));
        Assert.NotEmpty(SuggestionOptionsValidation.Problems(new SuggestionOptions { MaxSuggestions = 0 }));
        Assert.NotEmpty(SuggestionOptionsValidation.Problems(new SuggestionOptions { NotUsefulFloor = 0 }));
        Assert.NotEmpty(SuggestionOptionsValidation.Problems(new SuggestionOptions { NotUsefulFactor = 1 }));
        Assert.NotEmpty(SuggestionOptionsValidation.Problems(new SuggestionOptions { LiftWeight = 0, ConfidenceWeight = 0, SeverityWeight = 0, RecencyWeight = 0 }));
        Assert.NotEmpty(SuggestionOptionsValidation.Problems(new SuggestionOptions { Severity = new SeverityOptions { Fault = 1.5 } }));
    }
}
