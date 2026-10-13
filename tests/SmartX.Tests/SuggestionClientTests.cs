using Bunit;
using SmartX.Client.CommandStream;
using SmartX.Client.Components.CommandStream;
using SmartX.Client.Components.Trust;
using SmartX.Client.Realtime;
using SmartX.Client.Services;
using SmartX.Client.State;
using SmartX.Shared;
using static SmartX.Tests.ClientRealtimeKit;

namespace SmartX.Tests;

/// <summary>Builders for the suggestion, insight and investigation messages the client tests use.</summary>
public static class SuggestionKit
{
    public const string PumpA = "B8:27:EB:10:00:34";
    public const string MoistureB = "B8:27:EB:10:00:32";

    public static readonly DateTimeOffset T0 = ClientRealtimeKit.Start;

    public static Suggestion Make(
        string id,
        string what = "Turn on Pump A",
        bool command = true,
        double score = 0.8,
        int times = 7,
        int ofTimes = 8,
        int seeded = 5,
        double lift = 3.2,
        PatternLevel level = PatternLevel.Specific,
        double weight = 1,
        DateTimeOffset? triggeredAt = null,
        string mac = PumpA) =>
        new(
            id,
            what,
            "Moisture Sensor B dropped below 20 %",
            new SuggestionEvidence(
                times,
                ofTimes,
                seeded,
                (double)times / ofTimes,
                lift,
                $"Operators did this {times} of the last {ofTimes} times{(seeded > 0 ? $" ({seeded} from seeded history)" : string.Empty)}. {lift:0.#}× more likely than usual."),
            level,
            level == PatternLevel.Specific ? "This device's own pattern" : "Similar devices: a %RH sensor in Zone 1",
            score,
            weight,
            new SuggestedAction(
                command ? SuggestedActionKind.Command : SuggestedActionKind.OpenDevice,
                mac,
                "Pump A",
                command ? CommandAction.PumpOn : null,
                null,
                what),
            TriggerKind.ThresholdCrossing,
            MoistureB,
            "Moisture Sensor B",
            triggeredAt ?? T0,
            (triggeredAt ?? T0).AddMinutes(10),
            T0);

    public static SuggestionsUpdate Update(
        long sequence,
        IEnumerable<Suggestion>? suggestions = null,
        IEnumerable<Insight>? insights = null,
        IEnumerable<DismissedSuggestion>? dismissed = null) =>
        new(sequence, [.. suggestions ?? []], [.. insights ?? []], [.. dismissed ?? []], T0);

    public static Insight Anticipated(string mac = "B8:27:EB:10:00:03", string room = "Rack Row 3", int minutes = 6) =>
        new(
            $"anticipated:{mac}",
            InsightKind.Anticipated,
            mac,
            room,
            $"{room} temperature sensor: trust falling",
            $"Projected to reach Fault in about {minutes} minutes.",
            ["Trust 0.62, falling about 0.040 a minute.", "Reaches the Fault threshold (0.50) about 12:06.", "A straight line fits 18 samples over 3 minutes with R² 0.97."],
            0.5,
            TrustBand.Degrading,
            0.62,
            T0,
            T0.AddMinutes(minutes),
            0.97,
            null);

    public static Insight Current(string mac = "B8:27:EB:10:00:01", string room = "Rack Row 1", Guid? investigation = null) =>
        new(
            $"current:{mac}",
            InsightKind.Current,
            mac,
            room,
            $"{room} temperature sensor: in Fault, 2 alerts",
            "In Fault: 3 range violations. out-of-range alert.",
            ["In Fault: 3 range violations", "out-of-range alert"],
            1.0,
            TrustBand.Fault,
            0.31,
            T0,
            null,
            null,
            investigation);

    public static InvestigationDetails Investigation(
        Guid id,
        string mac = "B8:27:EB:10:00:01",
        string room = "Rack Row 1",
        InvestigationStatus status = InvestigationStatus.Open,
        Verdict? verdict = null,
        FaultCause? cause = null,
        SensitivityChange? change = null,
        PatternAnswer? precedent = null) =>
        new(
            id,
            mac,
            room,
            status,
            T0.AddMinutes(5),
            status == InvestigationStatus.Resolved ? T0.AddMinutes(6) : null,
            0.49,
            "3 range violations",
            TrustOutcome.RangeViolation,
            T0,
            T0.AddMinutes(5),
            [new AlertKey(mac, AlertKind.OutOfRange)],
            [],
            42,
            [],
            precedent ?? new PatternAnswer(new PatternTrigger(TriggerKind.TrustBand, "Fault", mac, room), null, []),
            verdict,
            cause,
            verdict is null ? null : T0.AddMinutes(6),
            change,
            []);
}

/// <summary>The suggestions feed: the same snapshot-then-stream rules as the others, and the memory of what arrived live.</summary>
public class SuggestionFeedTests
{
    private static async Task<ClientRig> LiveRigAsync()
    {
        var rig = ClientRig.Create();
        rig.Connection.EnsureStarted();
        await rig.WaitForStateAsync(RealtimeState.Live);

        return rig;
    }

    [Fact]
    public async Task The_snapshot_is_what_is_held_and_nothing_in_it_has_just_arrived()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Suggestions = () => SuggestionKit.Update(4, [SuggestionKit.Make("a")], [SuggestionKit.Anticipated()]);

        await rig.Suggestions.StartAsync();

        Assert.True(rig.Suggestions.Current.IsLoaded);
        Assert.Equal(["a"], rig.Suggestions.Current.Suggestions.Select(suggestion => suggestion.Id));
        Assert.Single(rig.Suggestions.Current.Insights);
        Assert.Empty(rig.Suggestions.Current.Arrivals);
    }

    [Fact]
    public async Task A_push_replaces_what_is_held_and_marks_what_is_new()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Suggestions = () => SuggestionKit.Update(4, [SuggestionKit.Make("a")]);
        await rig.Suggestions.StartAsync();
        await ClientRig.UntilAsync(() => rig.Hub.HandlerCount(RealtimeMethods.SuggestionsUpdated) > 0, "a listener");

        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(5, [SuggestionKit.Make("b", "Open Pump A", command: false), SuggestionKit.Make("a")]));

        Assert.Equal(["b", "a"], rig.Suggestions.Current.Suggestions.Select(suggestion => suggestion.Id));
        Assert.Contains("b", rig.Suggestions.Current.Arrivals.Keys);
        Assert.DoesNotContain("a", rig.Suggestions.Current.Arrivals.Keys);
    }

    [Fact]
    public async Task What_has_gone_is_gone_and_is_forgotten_as_an_arrival()
    {
        await using var rig = await LiveRigAsync();
        await rig.Suggestions.StartAsync();
        await ClientRig.UntilAsync(() => rig.Hub.HandlerCount(RealtimeMethods.SuggestionsUpdated) > 0, "a listener");

        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(1, [SuggestionKit.Make("a")]));
        Assert.Contains("a", rig.Suggestions.Current.Arrivals.Keys);

        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2));

        Assert.Empty(rig.Suggestions.Current.Suggestions);
        Assert.Empty(rig.Suggestions.Current.Arrivals);
    }

    [Fact]
    public async Task An_arrival_stays_marked_while_it_is_still_there()
    {
        await using var rig = await LiveRigAsync();
        await rig.Suggestions.StartAsync();
        await ClientRig.UntilAsync(() => rig.Hub.HandlerCount(RealtimeMethods.SuggestionsUpdated) > 0, "a listener");

        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(1, [SuggestionKit.Make("a")]));
        var first = rig.Suggestions.Current.Arrivals["a"];

        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2, [SuggestionKit.Make("a"), SuggestionKit.Make("b", "Open Pump A", false)]));

        Assert.Equal(first, rig.Suggestions.Current.Arrivals["a"]);
        Assert.Contains("b", rig.Suggestions.Current.Arrivals.Keys);
    }

    [Fact]
    public async Task A_push_older_than_the_snapshot_that_arrives_while_it_is_in_flight_is_discarded()
    {
        await using var rig = await LiveRigAsync();

        // The server's state when the snapshot was taken: sequence 9, one suggestion.
        rig.Snapshots.Suggestions = () => SuggestionKit.Update(9, [SuggestionKit.Make("now")]);
        var release = rig.Snapshots.HoldNextSuggestions();

        var starting = rig.Suggestions.StartAsync();
        await ClientRig.UntilAsync(() => rig.Snapshots.SuggestionCalls == 1, "the snapshot request");

        // Two pushes while it is on its way: one from before it was taken (5) and one from after (11).
        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(5, [SuggestionKit.Make("old")]));
        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(11, [SuggestionKit.Make("newer")]));

        release();
        await starting;

        // The old one did not overwrite the snapshot, and the newer one went on top of it.
        Assert.Equal(["newer"], rig.Suggestions.Current.Suggestions.Select(suggestion => suggestion.Id));
    }

    [Fact]
    public async Task A_late_message_cannot_overwrite_a_newer_one()
    {
        await using var rig = await LiveRigAsync();
        await rig.Suggestions.StartAsync();
        await ClientRig.UntilAsync(() => rig.Hub.HandlerCount(RealtimeMethods.SuggestionsUpdated) > 0, "a listener");

        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(12, [SuggestionKit.Make("second")]));
        rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(11, [SuggestionKit.Make("first")]));

        Assert.Equal(["second"], rig.Suggestions.Current.Suggestions.Select(suggestion => suggestion.Id));
    }

    [Fact]
    public async Task The_feed_joins_the_suggestions_topic()
    {
        await using var rig = await LiveRigAsync();

        await rig.Suggestions.StartAsync();

        Assert.Contains(RealtimeTopics.Suggestions, rig.Hub.Subscribed);
    }
}

/// <summary>The words on the screen, as functions of what the server sent.</summary>
public class SuggestionPresentationTests
{
    [Theory]
    [InlineData(40, "Arrived 40 ms after the trigger.")]
    [InlineData(999, "Arrived 999 ms after the trigger.")]
    [InlineData(1500, "Arrived 1.5 s after the trigger.")]
    public void How_long_a_suggestion_took_is_said_in_milliseconds_and_then_seconds(int milliseconds, string expected)
    {
        var suggestion = SuggestionKit.Make("a");

        Assert.Equal(expected, SuggestionPresentation.Latency(suggestion, suggestion.TriggeredAt.AddMilliseconds(milliseconds)));
    }

    [Fact]
    public void A_negative_or_absurd_latency_is_not_shown_because_the_clocks_disagree()
    {
        var suggestion = SuggestionKit.Make("a");

        Assert.Null(SuggestionPresentation.Latency(suggestion, suggestion.TriggeredAt.AddSeconds(-1)));
        Assert.Null(SuggestionPresentation.Latency(suggestion, suggestion.TriggeredAt.AddMinutes(10)));
    }

    [Theory]
    [InlineData(0, "Projected to reach Fault about now.")]
    [InlineData(30, "Projected to reach Fault in under a minute.")]
    [InlineData(60, "Projected to reach Fault in about 1 minute.")]
    [InlineData(360, "Projected to reach Fault in about 6 minutes.")]
    public void The_countdown_is_counted_from_the_projected_moment(int secondsLeft, string expected)
    {
        var insight = SuggestionKit.Anticipated(minutes: 6);
        var now = insight.ProjectedFaultAt!.Value.AddSeconds(-secondsLeft);

        Assert.Equal(expected, SuggestionPresentation.Countdown(insight, now));
    }

    [Fact]
    public void The_countdown_of_a_current_insight_is_its_detail()
    {
        var insight = SuggestionKit.Current();

        Assert.Equal(insight.Detail, SuggestionPresentation.Countdown(insight, SuggestionKit.T0));
    }

    [Fact]
    public void An_arrival_is_new_until_the_highlight_runs_out()
    {
        var arrivals = new Dictionary<string, DateTimeOffset> { ["a"] = SuggestionKit.T0 };
        var highlight = TimeSpan.FromSeconds(8);

        Assert.True(SuggestionPresentation.IsNew(arrivals, "a", SuggestionKit.T0.AddSeconds(7), highlight));
        Assert.False(SuggestionPresentation.IsNew(arrivals, "a", SuggestionKit.T0.AddSeconds(8), highlight));
        Assert.False(SuggestionPresentation.IsNew(arrivals, "other", SuggestionKit.T0, highlight));
    }

    [Fact]
    public void The_highlight_ends_when_the_latest_one_does()
    {
        var arrivals = new Dictionary<string, DateTimeOffset> { ["a"] = SuggestionKit.T0, ["b"] = SuggestionKit.T0.AddSeconds(3) };

        Assert.Equal(TimeSpan.FromSeconds(9), SuggestionPresentation.HighlightEnds(arrivals, SuggestionKit.T0.AddSeconds(2), TimeSpan.FromSeconds(8)));
        Assert.Null(SuggestionPresentation.HighlightEnds(arrivals, SuggestionKit.T0.AddSeconds(30), TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void A_verdict_is_put_into_words()
    {
        var id = Guid.NewGuid();

        Assert.Equal("No verdict yet", SuggestionPresentation.Verdict(SuggestionKit.Investigation(id)));
        Assert.Equal("Confirmed as a real fault", SuggestionPresentation.Verdict(SuggestionKit.Investigation(id, verdict: Verdict.Confirm)));
        Assert.Equal("Dismissed as a false alarm", SuggestionPresentation.Verdict(SuggestionKit.Investigation(id, verdict: Verdict.Dismiss)));
        Assert.Equal("Relabelled: the cause was power", SuggestionPresentation.Verdict(SuggestionKit.Investigation(id, verdict: Verdict.Relabel, cause: FaultCause.Power)));
    }

    [Fact]
    public void The_sensitivity_note_says_what_a_dismissal_did_and_that_it_can_be_given_back()
    {
        var investigation = SuggestionKit.Investigation(Guid.NewGuid(), verdict: Verdict.Dismiss, change: new SensitivityChange(TrustOutcome.RangeViolation, 1, 0.85));

        var note = SuggestionPresentation.SensitivityNote(investigation);

        Assert.Contains("0.85", note, StringComparison.Ordinal);
        Assert.Contains("readings outside the registered range", note, StringComparison.Ordinal);
        Assert.Contains("gives it back", note, StringComparison.Ordinal);
        Assert.Null(SuggestionPresentation.SensitivityNote(SuggestionKit.Investigation(Guid.NewGuid())));
    }

    [Fact]
    public void The_precedent_is_what_operators_did_last_time_and_empty_when_nothing_is_known()
    {
        var none = new PatternAnswer(new PatternTrigger(TriggerKind.TrustBand, "Fault", "x", "x"), null, []);

        Assert.Null(SuggestionPresentation.Precedent(none));

        var known = new PatternAnswer(
            new PatternTrigger(TriggerKind.TrustBand, "Fault", "x", "Rack Row 1"),
            PatternLevel.Specific,
            [new PatternAssociation(
                new PatternTrigger(TriggerKind.TrustBand, "Fault", "x", "Rack Row 1"),
                new PatternAction(OperatorActionKind.Viewed, "y", "Rack Row 2", string.Empty),
                PatternLevel.Specific, 3, 0.75, 4, 4, 0.1, true)]);

        Assert.Equal("Last time this node faulted, operators opened Rack Row 2, 75% of the time.", SuggestionPresentation.Precedent(known)?.Replace(' ', ' '));
    }

    [Fact]
    public void The_decay_window_is_said_as_a_span_of_time()
    {
        var window = SuggestionPresentation.Window(SuggestionKit.Investigation(Guid.NewGuid()));

        Assert.Contains("(5 min)", window, StringComparison.Ordinal);
    }
}

/// <summary>Suggested Actions: what each card says, what one click does, and what an arrival looks like.</summary>
public class SuggestedActionsPanelTests : StreamComponentTests
{
    private async Task<IRenderedComponent<SuggestedActionsPanel>> RenderPanelAsync(params Suggestion[] suggestions)
    {
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(1, suggestions);
        await PreloadFeedsAsync();

        var cut = Render<SuggestedActionsPanel>();
        await SettledAsync(cut);

        return cut;
    }

    [Fact]
    public async Task An_empty_panel_says_what_it_is_waiting_for()
    {
        var cut = await RenderPanelAsync();

        Assert.Contains("Nothing to suggest right now", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Waiting for a trigger", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_card_says_what_why_and_the_evidence_and_where_it_came_from()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Make("a"));
        var card = cut.Find(".smartx-suggestion");

        Assert.Contains("Turn on Pump A", card.QuerySelector(".smartx-suggestion__what")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Moisture Sensor B dropped below 20 % at", card.QuerySelector(".smartx-suggestion__why")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Operators did this 7 of the last 8 times (5 from seeded history). 3.2× more likely than usual.", card.QuerySelector(".smartx-suggestion__evidence")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("This device's own pattern", card.QuerySelector(".smartx-suggestion__level")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pattern_learned_from_similar_devices_says_so()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Make("a", level: PatternLevel.General));

        Assert.Contains("Similar devices: a %RH sensor in Zone 1", cut.Find(".smartx-suggestion__level").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cards_are_in_the_order_the_server_ranked_them()
    {
        var cut = await RenderPanelAsync(
            SuggestionKit.Make("a", "Turn on Pump A"),
            SuggestionKit.Make("b", "Open Pump A", command: false),
            SuggestionKit.Make("c", "Open Drain Valve", command: false));

        var order = cut.FindAll(".smartx-suggestion__what").Select(element => element.TextContent.Trim()).ToList();

        Assert.Equal(["Turn on Pump A", "Open Pump A", "Open Drain Valve"], order);
    }

    [Fact]
    public async Task One_click_on_a_command_sends_it_through_the_normal_path_with_the_suggestions_id()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Make("abc", "Turn on Pump A"));

        cut.Find(".smartx-suggestion__actions button").Click();

        await ClientRig.UntilAsync(() => Api.CommandsIssued.Count == 1, "the command");

        Assert.Equal((SuggestionKit.PumpA, CommandAction.PumpOn, (double?)null), Api.CommandsIssued[0]);
        Assert.Equal(("abc", SuggestionKit.PumpA), (Api.SuggestedCommands[0].SuggestionId, Api.SuggestedCommands[0].Mac));
        cut.WaitForAssertion(() => Assert.Contains("Sent: Turn on Pump A", cut.Find("[role=status]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_click_on_an_open_suggestion_opens_the_device_and_reports_it_as_following_the_suggestion()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Make("xyz", "Open Pump A", command: false));

        cut.Find(".smartx-suggestion__actions button").Click();

        await ClientRig.UntilAsync(() => Api.SuggestedSelections.Count == 1, "the selection");

        Assert.Equal(SuggestionKit.PumpA, State.StreamDevice);
        Assert.Equal((SuggestionKit.PumpA, "xyz"), Api.SuggestedSelections[0]);
        Assert.Empty(Api.CommandsIssued);
    }

    [Fact]
    public async Task A_refused_command_says_why_and_is_not_reported_as_sent()
    {
        Api.IssueProblem = new ApiProblem(400, "A pump cannot be told to open a valve.");
        var cut = await RenderPanelAsync(SuggestionKit.Make("abc"));

        cut.Find(".smartx-suggestion__actions button").Click();

        cut.WaitForAssertion(() =>
        {
            var message = cut.Find("[role=status]").TextContent;

            Assert.Contains("was not sent", message, StringComparison.Ordinal);
            Assert.Contains("A pump cannot be told to open a valve.", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Not_useful_asks_the_api_to_dismiss_that_suggestion_only()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Make("a"), SuggestionKit.Make("b", "Open Pump A", command: false));

        cut.FindAll("button").Single(button => button.GetAttribute("aria-label") == "Not useful: Open Pump A").Click();

        await ClientRig.UntilAsync(() => Api.Dismissals.Count == 1, "the dismissal");

        Assert.Equal(["b"], Api.Dismissals);
    }

    [Fact]
    public async Task A_dismissed_suggestion_can_be_taken_back_and_says_what_its_weight_is()
    {
        var suggestion = SuggestionKit.Make("a");
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(
            1, [], null, [new DismissedSuggestion(suggestion with { Weight = 0.85 }, SuggestionKit.T0, 1, 0.85)]);
        await PreloadFeedsAsync();

        var cut = Render<SuggestedActionsPanel>();
        await SettledAsync(cut);

        Assert.Contains("Marked not useful (1)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("weight now 85", cut.Find(".smartx-suggestions__dismissed").TextContent, StringComparison.Ordinal);

        cut.FindAll("button").Single(button => button.TextContent.Contains("Take back", StringComparison.Ordinal)).Click();

        await ClientRig.UntilAsync(() => Api.Restorations.Count == 1, "the restoration");

        Assert.Equal(["a"], Api.Restorations);
    }

    [Fact]
    public async Task A_lowered_weight_is_shown_on_the_card()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Make("a", weight: 0.72));

        Assert.Contains("weight 72", cut.Find(".smartx-suggestion__weight").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_suggestion_appears_without_a_reload_and_says_New_in_words()
    {
        var cut = await RenderPanelAsync();

        Assert.Empty(cut.FindAll(".smartx-suggestion"));

        Rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2, [SuggestionKit.Make("a", triggeredAt: Time.GetUtcNow())]));

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".smartx-suggestion");

            Assert.Contains("smartx-suggestion--new", card.ClassName, StringComparison.Ordinal);
            Assert.Contains("New", card.QuerySelector(".smartx-suggestion__new")!.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task How_long_the_suggestion_took_to_arrive_is_said_on_a_new_card()
    {
        var cut = await RenderPanelAsync();

        Rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2, [SuggestionKit.Make("a", triggeredAt: Time.GetUtcNow() - TimeSpan.FromMilliseconds(75))]));

        cut.WaitForAssertion(() => Assert.Contains("Arrived 75 ms after the trigger.", cut.Find(".smartx-suggestion__latency").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_highlight_goes_by_itself_and_the_card_stays()
    {
        var cut = await RenderPanelAsync();

        Rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2, [SuggestionKit.Make("a", triggeredAt: Time.GetUtcNow())]));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".smartx-suggestion--new")));
        await SettledAsync(cut);

        Time.Advance(Options.ArrivalHighlight + TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".smartx-suggestion--new"));
            Assert.Single(cut.FindAll(".smartx-suggestion"));
        });
    }

    [Fact]
    public async Task A_suggestion_that_is_acted_on_disappears_when_the_server_says_so()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Make("a"), SuggestionKit.Make("b", "Open Pump A", command: false));

        Rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2, [SuggestionKit.Make("a")]));

        cut.WaitForAssertion(() => Assert.Equal(["Turn on Pump A"], cut.FindAll(".smartx-suggestion__what").Select(element => element.TextContent.Trim())));
    }

    [Fact]
    public async Task Every_button_has_a_name_that_says_what_it_does_and_for_which_suggestion()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Make("a"));

        var names = cut.FindAll(".smartx-suggestion__actions button").Select(button => button.GetAttribute("aria-label")).ToList();

        Assert.Contains(names, name => name!.StartsWith("Turn on Pump A, suggested because", StringComparison.Ordinal));
        Assert.Contains("Not useful: Turn on Pump A", names);
    }
}

/// <summary>Automated Insights: anticipated first, with a countdown that moves, then what is wrong now.</summary>
public class InsightsPanelTests : StreamComponentTests
{
    private async Task<IRenderedComponent<InsightsPanel>> RenderPanelAsync(params Insight[] insights)
    {
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(1, null, insights);
        await PreloadFeedsAsync();

        var cut = Render<InsightsPanel>();
        await SettledAsync(cut);

        return cut;
    }

    [Fact]
    public async Task An_empty_panel_says_what_it_is_waiting_for()
    {
        var cut = await RenderPanelAsync();

        Assert.Contains("Nothing needs attention", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Waiting for a node's trust to start falling", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anticipated_fault_says_so_with_a_word_and_a_projected_time()
    {
        // The page's clock is at the insight's own start, so six minutes remain.
        var cut = await RenderPanelAsync(SuggestionKit.Anticipated());
        var row = cut.Find(".smartx-insight");

        Assert.Contains("Anticipated", row.QuerySelector(".smartx-status")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Rack Row 3 temperature sensor: trust falling", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Projected to reach Fault in about 6 minutes.", row.QuerySelector(".smartx-insight__detail")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_working_behind_a_projection_is_one_click_away()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Anticipated());

        var working = cut.Find(".smartx-insight__working");

        Assert.Contains("R² 0.97", working.TextContent, StringComparison.Ordinal);
        Assert.Contains("How this was worked out", working.QuerySelector("summary")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_countdown_moves_as_time_passes_without_any_message_from_the_server()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Anticipated());

        Time.Advance(TimeSpan.FromMinutes(3));

        cut.WaitForAssertion(() => Assert.Contains("in about 3 minutes", cut.Find(".smartx-insight__detail").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anticipated_comes_before_current_and_each_says_which_it_is()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Anticipated(), SuggestionKit.Current());

        var words = cut.FindAll(".smartx-insight .smartx-status").Select(element => element.TextContent.Trim()).ToList();

        Assert.Equal(["Anticipated", "Now"], words);
    }

    [Fact]
    public async Task A_current_problem_lists_its_reasons_and_links_to_its_investigation()
    {
        var investigation = Guid.NewGuid();
        var cut = await RenderPanelAsync(SuggestionKit.Current(investigation: investigation));

        Assert.Contains("In Fault: 3 range violations", cut.Find(".smartx-insight__reasons").TextContent, StringComparison.Ordinal);
        Assert.Equal($"#investigation-{investigation}", cut.Find(".smartx-insight__link").GetAttribute("href"));
    }

    [Fact]
    public async Task Open_opens_the_device_and_reports_the_click()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Current());

        cut.FindAll("button").Single(button => button.TextContent.Contains("Open Rack Row 1", StringComparison.Ordinal)).Click();

        await ClientRig.UntilAsync(() => Api.Selections.Count == 1, "the selection");

        Assert.Equal("B8:27:EB:10:00:01", State.StreamDevice);
        Assert.Equal(["B8:27:EB:10:00:01"], Api.Selections);
    }

    [Fact]
    public async Task A_new_insight_appears_by_push_and_says_New()
    {
        var cut = await RenderPanelAsync();

        Rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2, null, [SuggestionKit.Anticipated()]));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("smartx-insight--new", cut.Find(".smartx-insight").ClassName, StringComparison.Ordinal);
            Assert.Contains("New", cut.Find(".smartx-suggestion__new").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_withdrawn_insight_leaves_the_list()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Anticipated());

        Rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2));

        cut.WaitForAssertion(() => Assert.Contains("Nothing needs attention", cut.Markup, StringComparison.Ordinal));
    }
}

/// <summary>Investigations: what is shown, the three verdicts, the cause picker, and what opening one does to the timeline.</summary>
public class InvestigationCardTests : StreamComponentTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private async Task<IRenderedComponent<InvestigationsPanel>> RenderPanelAsync(params InvestigationDetails[] open)
    {
        Api.OpenInvestigations = () => open;
        await PreloadFeedsAsync();

        var cut = Render<InvestigationsPanel>();
        await SettledAsync(cut);

        return cut;
    }

    [Fact]
    public async Task With_nothing_open_the_panel_says_what_opens_one()
    {
        var cut = await RenderPanelAsync();

        Assert.Contains("No investigations are open", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("falls into Fault", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_open_investigation_shows_the_node_the_window_the_cause_and_the_precedent()
    {
        var precedent = new PatternAnswer(
            new PatternTrigger(TriggerKind.TrustBand, "Fault", "x", "Rack Row 1"),
            PatternLevel.Specific,
            [new PatternAssociation(
                new PatternTrigger(TriggerKind.TrustBand, "Fault", "x", "Rack Row 1"),
                new PatternAction(OperatorActionKind.Viewed, "y", "Rack Row 2", string.Empty),
                PatternLevel.Specific, 3, 0.75, 4, 4, 0.1, true)]);

        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id, precedent: precedent));
        var card = cut.Find(".smartx-investigation");

        Assert.Contains("Rack Row 1", card.QuerySelector(".smartx-investigation__head")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Open", card.QuerySelector(".smartx-status")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("(5 min)", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("readings outside the registered range", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("Out of range", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("operators opened Rack Row 2", card.QuerySelector(".smartx-investigation__precedent")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_sends_a_confirm_verdict_for_that_investigation()
    {
        Api.Verdict = (id, request) => ApiResult<InvestigationDetails>.Success(SuggestionKit.Investigation(id, status: InvestigationStatus.Resolved, verdict: request.Verdict));
        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm").Click();

        await ClientRig.UntilAsync(() => Api.Verdicts.Count == 1, "the verdict");

        Assert.Equal((Id, new VerdictRequest(Verdict.Confirm, null)), Api.Verdicts[0]);
        cut.WaitForAssertion(() => Assert.Contains("Confirmed as a real fault", cut.Find(".smartx-investigation__verdict").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dismiss_sends_a_dismiss_verdict_and_says_what_it_did_to_detection()
    {
        Api.Verdict = (id, request) => ApiResult<InvestigationDetails>.Success(SuggestionKit.Investigation(
            id, status: InvestigationStatus.Resolved, verdict: request.Verdict, change: new SensitivityChange(TrustOutcome.RangeViolation, 1, 0.85)));
        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Dismiss").Click();

        await ClientRig.UntilAsync(() => Api.Verdicts.Count == 1, "the verdict");

        Assert.Equal(Verdict.Dismiss, Api.Verdicts[0].Request.Verdict);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Dismissed as a false alarm", cut.Find(".smartx-investigation__verdict").TextContent, StringComparison.Ordinal);
            Assert.Contains("0.85", cut.Find(".smartx-investigation__note").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Relabel_asks_for_a_cause_and_will_not_send_without_one()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id));

        Assert.Empty(cut.FindAll(".smartx-investigation__relabel"));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Relabel").Click();

        var apply = cut.FindAll("button").Single(button => button.TextContent.Contains("Apply relabel", StringComparison.Ordinal));

        Assert.True(apply.HasAttribute("disabled"));
        Assert.Empty(Api.Verdicts);
    }

    [Fact]
    public async Task Relabel_with_a_cause_sends_that_cause()
    {
        Api.Verdict = (id, request) => ApiResult<InvestigationDetails>.Success(
            SuggestionKit.Investigation(id, status: InvestigationStatus.Resolved, verdict: request.Verdict, cause: request.Cause));
        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Relabel").Click();
        cut.Find(".smartx-investigation__relabel select").Change("Power");
        cut.FindAll("button").Single(button => button.TextContent.Contains("Apply relabel", StringComparison.Ordinal)).Click();

        await ClientRig.UntilAsync(() => Api.Verdicts.Count == 1, "the verdict");

        Assert.Equal((Id, new VerdictRequest(Verdict.Relabel, FaultCause.Power)), Api.Verdicts[0]);
        cut.WaitForAssertion(() => Assert.Contains("Relabelled: the cause was power", cut.Find(".smartx-investigation__verdict").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_cause_is_offered()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Relabel").Click();

        var offered = cut.FindAll(".smartx-investigation__relabel option").Select(option => option.GetAttribute("value")).Where(value => !string.IsNullOrEmpty(value)).ToList();

        Assert.Equal(["Hardware", "Network", "Power", "Environmental"], offered);
    }

    [Fact]
    public async Task A_verdict_that_fails_says_why_and_keeps_the_card()
    {
        Api.Verdict = (id, request) => ApiResult<InvestigationDetails>.Failure(new ApiProblem(404, "No investigation has that id."));
        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm").Click();

        cut.WaitForAssertion(() => Assert.Contains("No investigation has that id.", cut.Find(".smartx-investigation [role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Single(cut.FindAll(".smartx-investigation"));
    }

    [Fact]
    public async Task An_investigation_the_operator_resolved_stays_listed_as_resolved_so_the_verdict_can_be_changed()
    {
        Api.Verdict = (id, request) => ApiResult<InvestigationDetails>.Success(
            SuggestionKit.Investigation(id, status: InvestigationStatus.Resolved, verdict: request.Verdict));
        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Resolved by you this session", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Resolved", cut.Find(".smartx-investigation .smartx-status").TextContent, StringComparison.Ordinal);
            Assert.Equal("true", cut.FindAll("button").Single(button => button.TextContent.Trim() == "Confirm").GetAttribute("aria-pressed"));
        });
    }

    [Fact]
    public async Task Show_decay_window_opens_the_device_and_points_its_timeline_at_the_window()
    {
        var cut = await RenderPanelAsync(SuggestionKit.Investigation(Id));

        cut.FindAll("button").Single(button => button.TextContent.Contains("Show decay window", StringComparison.Ordinal)).Click();

        Assert.Equal("B8:27:EB:10:00:01", State.StreamDevice);

        var focus = State.TimelineFocus ?? throw new InvalidOperationException("No focus.");

        // The window, with a minute either side.
        Assert.Equal(SuggestionKit.T0.AddMinutes(-1), focus.From);
        Assert.Equal(SuggestionKit.T0.AddMinutes(6), focus.To);
        Assert.Contains("decay window", focus.Label, StringComparison.Ordinal);
        await ClientRig.UntilAsync(() => Api.Selections.Count == 1, "the selection");
    }

    [Fact]
    public async Task A_new_investigation_appears_when_the_insights_say_one_opened()
    {
        var opened = new List<InvestigationDetails>();
        Api.OpenInvestigations = () => opened;
        await PreloadFeedsAsync();

        var cut = Render<InvestigationsPanel>();
        await SettledAsync(cut);
        Assert.Contains("No investigations are open", cut.Markup, StringComparison.Ordinal);

        opened.Add(SuggestionKit.Investigation(Id));
        Rig.Hub.Deliver(RealtimeMethods.SuggestionsUpdated, SuggestionKit.Update(2, null, [SuggestionKit.Current(investigation: Id)]));

        cut.WaitForAssertion(() => Assert.Contains("Rack Row 1", cut.Find(".smartx-investigation__head").TextContent, StringComparison.Ordinal));
    }
}

/// <summary>The device's timeline can be pointed at a stretch of the past, and says so.</summary>
public class TimelineFocusTests
{
    [Fact]
    public void Focusing_a_timeline_opens_the_device_and_choosing_a_range_lets_go_of_it()
    {
        var state = new AppState();
        var changes = 0;
        state.Changed += () => changes++;

        state.FocusTimeline(new TimelineFocus("A", SuggestionKit.T0, SuggestionKit.T0.AddMinutes(5), "the window"));

        Assert.Equal("A", state.StreamDevice);
        Assert.NotNull(state.TimelineFocus);

        state.SetTimelineRange(TimelineRange.OneHour);

        Assert.Null(state.TimelineFocus);
        Assert.True(changes >= 2);
    }

    [Fact]
    public void Choosing_the_range_already_chosen_changes_nothing_when_there_was_no_focus()
    {
        var state = new AppState();
        var changes = 0;
        state.Changed += () => changes++;

        state.SetTimelineRange(state.TimelineRange);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Opening_another_device_or_closing_this_one_ends_the_focus()
    {
        var state = new AppState();

        state.FocusTimeline(new TimelineFocus("A", SuggestionKit.T0, SuggestionKit.T0.AddMinutes(5), "the window"));
        state.SelectStreamDevice("B");
        Assert.Null(state.TimelineFocus);

        state.FocusTimeline(new TimelineFocus("A", SuggestionKit.T0, SuggestionKit.T0.AddMinutes(5), "the window"));
        state.SelectStreamDevice(null);
        Assert.Null(state.TimelineFocus);
    }

    [Fact]
    public void Clearing_a_focus_that_is_not_there_does_nothing()
    {
        var state = new AppState();
        var changes = 0;
        state.Changed += () => changes++;

        state.ClearTimelineFocus();

        Assert.Equal(0, changes);
    }
}

/// <summary>The compact strip on the trust page.</summary>
public class SuggestionStripTests : StreamComponentTests
{
    private async Task<IRenderedComponent<SuggestionStrip>> RenderStripAsync(params Suggestion[] suggestions)
    {
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(1, suggestions);
        await PreloadFeedsAsync();

        var cut = Render<SuggestionStrip>();
        await SettledAsync(cut);

        return cut;
    }

    [Fact]
    public async Task With_nothing_to_suggest_it_takes_no_room()
    {
        var cut = await RenderStripAsync();

        Assert.Empty(cut.FindAll(".smartx-suggestion"));
        Assert.DoesNotContain("Suggested Actions", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_shows_the_best_three_with_what_and_why()
    {
        var cut = await RenderStripAsync(
            SuggestionKit.Make("a", "Turn on Pump A"),
            SuggestionKit.Make("b", "Open Pump A", command: false),
            SuggestionKit.Make("c", "Open Drain Valve", command: false),
            SuggestionKit.Make("d", "Open Pump B", command: false));

        Assert.Equal(3, cut.FindAll(".smartx-suggestion").Count);
        Assert.Contains("Moisture Sensor B dropped below 20 %", cut.Find(".smartx-suggestion__why").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Open Pump B", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_offers_the_one_click_and_leaves_not_useful_for_the_command_stream()
    {
        var cut = await RenderStripAsync(SuggestionKit.Make("a"));

        Assert.Single(cut.FindAll(".smartx-suggestion__actions button"));

        cut.Find(".smartx-suggestion__actions button").Click();

        await ClientRig.UntilAsync(() => Api.SuggestedCommands.Count == 1, "the command");

        Assert.Equal("a", Api.SuggestedCommands[0].SuggestionId);
    }

    [Fact]
    public async Task It_links_to_the_command_stream_for_the_evidence()
    {
        var cut = await RenderStripAsync(SuggestionKit.Make("a"));

        Assert.Equal("command-stream", cut.Find(".smartx-insight__link").GetAttribute("href"));
    }
}
