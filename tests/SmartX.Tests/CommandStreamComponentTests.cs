using Bunit;
using SmartX.Client.CommandStream;
using SmartX.Client.Components.CommandStream;
using SmartX.Client.Components.Sensors;
using SmartX.Client.Realtime;
using SmartX.Shared;

namespace SmartX.Tests;

// Component tests for the Command Stream page, in bUnit. Each class is one behaviour the brief names. They run against the
// shipped feeds and services on a fake hub, a fake API and a fake clock, so what is asserted is what the component shows
// and what the API was asked, never what a component says it did.

/// <summary>Search: typing is debounced, and what was settled on is what survives leaving the page.</summary>
public class DeviceSearchComponentTests : StreamComponentTests
{
    private static DeviceSearchHit Hit(string mac, string room) =>
        new(mac, room, "N", "zone-1", SensorCategory.Actuator, ActuatorKind.Pump, TrustBand.Nominal, []);

    [Fact]
    public async Task Rapid_typing_is_one_request_sent_after_the_pause()
    {
        RenderPopoverHost();
        var cut = Render<DeviceSearchBar>();
        var box = cut.Find(".smartx-search input");

        box.Input("p");
        box.Input("pu");
        box.Input("pum");
        box.Input("pump");

        Assert.Empty(Api.Searched);

        // Just short of the pause: still nothing sent.
        Time.Advance(Options.SearchDebounce - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.Empty(Api.Searched);

        Time.Advance(TimeSpan.FromMilliseconds(1));
        await ClientRig.UntilAsync(() => Api.Searched.Count > 0, "the search to be sent");
        await Task.Delay(50);

        var sent = Assert.Single(Api.Searched);
        Assert.Equal("pump", sent.Q);
    }

    [Fact]
    public void The_box_shows_what_was_typed_at_once_even_though_the_request_waits()
    {
        RenderPopoverHost();
        var cut = Render<DeviceSearchBar>();

        cut.Find(".smartx-search input").Input("pump");

        Assert.Equal("pump", State.StreamFilters.Q);
        Assert.Contains("Text: pump", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(Api.Searched);
    }

    [Fact]
    public async Task The_result_count_says_what_the_filters_add_up_to()
    {
        Api.Search = filters => new DeviceSearchResponse(filters, 3, [Hit("B8:27:EB:10:00:01", "A"), Hit("B8:27:EB:10:00:02", "B"), Hit("B8:27:EB:10:00:03", "C")]);
        RenderPopoverHost();
        var cut = Render<DeviceSearchBar>();

        cut.Find(".smartx-search input").Input("pump");
        Time.Advance(Options.SearchDebounce);
        await ClientRig.UntilAsync(() => Search.Result is not null, "the result");

        cut.WaitForAssertion(() => Assert.Contains("3 devices match", cut.Find(".smartx-search__summary").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clearing_one_filter_leaves_the_others_and_searches_again()
    {
        State.SetStreamFilters(new DeviceSearchFilters(Q: "pump", Category: SensorCategory.Actuator));
        RenderPopoverHost();
        var cut = Render<DeviceSearchBar>();

        cut.Find("[aria-label='Clear filter Text: pump'] .mud-chip-close-button").Click();
        Time.Advance(Options.SearchDebounce);
        await ClientRig.UntilAsync(() => Api.Searched.Count > 0, "the search");

        Assert.Null(State.StreamFilters.Q);
        Assert.Equal(SensorCategory.Actuator, State.StreamFilters.Category);
    }

    [Fact]
    public async Task Leaving_the_page_and_coming_back_restores_the_filters_the_result_and_the_open_device()
    {
        const string mac = StreamKit.Pump;

        Api.Search = filters => new DeviceSearchResponse(filters, 1, [Hit(mac, "Coolant Pump")]);
        RenderPopoverHost();

        var first = Render<DevicePanel>();
        first.Find(".smartx-search input").Input("pump");
        Time.Advance(Options.SearchDebounce);
        await ClientRig.UntilAsync(() => Search.Result?.Filters.Q == "pump", "the settled search");

        State.SelectStreamDevice(mac);
        first.WaitForElement(".smartx-detail");
        await ClientRig.UntilAsync(() => Rig.Hub.Subscribed.Contains($"device:{mac}"), "the device topic to be joined");

        var requestsBefore = Search.RequestsSent;

        // Away: the page's components are destroyed, as navigation does. What lives in the scoped services stays.
        await DisposeComponentsAsync();
        await ClientRig.UntilAsync(() => !Rig.Hub.Subscribed.Contains($"device:{mac}"), "the device topic to be left");

        // Back.
        var second = Render<DevicePanel>();

        Assert.Equal("pump", second.Find(".smartx-search input").GetAttribute("value"));
        Assert.Contains("Text: pump", second.Markup, StringComparison.Ordinal);
        second.WaitForElement(".smartx-detail");
        Assert.Contains(mac, second.Find(".smartx-detail").TextContent, StringComparison.Ordinal);

        // The list came from what was kept, not from asking again.
        Assert.Equal(requestsBefore, Search.RequestsSent);
        await ClientRig.UntilAsync(() => Rig.Hub.Subscribed.Contains($"device:{mac}"), "the device topic to be joined again");
    }
}

/// <summary>The live stream: bounded, pausable with an ordered resume, and critical readings pinned until dealt with.</summary>
// A capacity of 20, so "bounded" is a few dozen readings and not five hundred. The pin lasts as long as it does in the app.
public class LiveStreamComponentTests() : StreamComponentTests(streamCapacity: 20)
{
    private async Task StartedAsync()
    {
        await Stream.StartAsync();
        await ListeningAsync(Rig, RealtimeMethods.ReadingsArrived);
        await ListeningAsync(Rig, RealtimeMethods.PriorityAlerted);
    }

    private static string[] Values(IEnumerable<StreamRow> rows) => [.. rows.Select(row => row.Reading.Value.ToString("0"))];

    [Fact]
    public async Task Pausing_holds_what_arrives_and_resuming_applies_it_in_the_order_it_arrived()
    {
        await StartedAsync();

        Rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, StreamKit.Batch(1, StreamKit.Reading(StreamKit.Meter, 1, 1)));
        await ClientRig.UntilAsync(() => Stream.RowCount == 1, "the first reading");

        Stream.SetPaused(true);

        Rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, StreamKit.Batch(2, StreamKit.Reading(StreamKit.Meter, 2, 2), StreamKit.Reading(StreamKit.Meter, 3, 3)));
        Rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, StreamKit.Batch(3, StreamKit.Reading(StreamKit.Meter, 4, 4)));
        await ClientRig.UntilAsync(() => Stream.PendingCount == 3, "the readings to be held");

        // Held: the visible stream has not moved.
        Assert.Equal(1, Stream.RowCount);
        Assert.Equal(["1"], Values(Stream.Rows(null)));

        Stream.SetPaused(false);

        // Newest first, and in exactly the order they arrived: 4 3 2, then the one from before the pause.
        Assert.Equal(["4", "3", "2", "1"], Values(Stream.Rows(null)));
        Assert.Equal(0, Stream.PendingCount);
    }

    [Fact]
    public async Task The_panel_counts_the_held_readings_and_the_button_says_resume()
    {
        await StartedAsync();
        var cut = Render<LiveStreamPanel>();
        await SettledAsync(cut);

        cut.Find("button[aria-pressed]").Click();
        Rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, StreamKit.Batch(1, StreamKit.Reading(StreamKit.Meter, 1, 1), StreamKit.Reading(StreamKit.Meter, 2, 2)));

        cut.WaitForAssertion(() => Assert.Contains("2 waiting", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Resume", cut.Find("button[aria-pressed]").TextContent, StringComparison.Ordinal);

        cut.Find("button[aria-pressed]").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("waiting", cut.Markup, StringComparison.Ordinal));
        Assert.False(State.StreamPaused);
        Assert.Equal(2, Stream.RowCount);
    }

    [Fact]
    public async Task The_stream_never_holds_more_than_its_capacity_and_keeps_the_newest()
    {
        await StartedAsync();

        // Fifty readings in one batch into a stream of twenty.
        var readings = Enumerable.Range(1, 50).Select(n => StreamKit.Reading(StreamKit.Meter, n, n)).ToArray();
        Rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, StreamKit.Batch(1, readings));
        await ClientRig.UntilAsync(() => Stream.Version > 0, "the batch");

        Assert.Equal(Options.StreamCapacity, Stream.RowCount);

        var rows = Stream.Rows(null);
        Assert.Equal("50", Values(rows)[0]);
        Assert.Equal("31", Values(rows)[^1]);
    }

    [Fact]
    public async Task A_long_pause_does_not_hoard_readings_and_says_how_many_were_pushed_out()
    {
        await StartedAsync();
        Stream.SetPaused(true);

        var readings = Enumerable.Range(1, 50).Select(n => StreamKit.Reading(StreamKit.Meter, n, n)).ToArray();
        Rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, StreamKit.Batch(1, readings));
        await ClientRig.UntilAsync(() => Stream.PendingCount > 0, "the held readings");

        Assert.Equal(Options.StreamCapacity, Stream.PendingCount);
        Assert.Equal(50 - Options.StreamCapacity, Stream.PendingDropped);

        Stream.SetPaused(false);

        // What resumes is the newest twenty, still in order.
        Assert.Equal("50", Values(Stream.Rows(null))[0]);
        Assert.Equal(0, Stream.PendingDropped);
    }

    [Fact]
    public async Task A_critical_reading_is_pinned_and_acknowledging_it_unpins_it()
    {
        await StartedAsync();
        var cut = Render<LiveStreamPanel>();

        Rig.Hub.Deliver(RealtimeMethods.PriorityAlerted, StreamKit.Critical(1, value: 250));

        var pinned = cut.WaitForElement("ul[aria-label='Critical readings'] li");
        Assert.Contains("power spike", pinned.TextContent, StringComparison.Ordinal);
        Assert.Contains("250", pinned.TextContent, StringComparison.Ordinal);
        Assert.Contains("critical lane", pinned.TextContent, StringComparison.Ordinal);

        await SettledAsync(cut);
        cut.Find("ul[aria-label='Critical readings'] li button").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("ul[aria-label='Critical readings'] li")));
        Assert.Empty(Stream.Pinned);
    }

    [Fact]
    public async Task A_pinned_reading_unpins_itself_when_its_time_runs_out()
    {
        await StartedAsync();
        var cut = Render<LiveStreamPanel>();

        Rig.Hub.Deliver(RealtimeMethods.PriorityAlerted, StreamKit.Critical(1));
        cut.WaitForElement("ul[aria-label='Critical readings'] li");

        Time.Advance(Options.PinDuration - TimeSpan.FromSeconds(2));
        Assert.Single(Stream.Pinned);

        Time.Advance(TimeSpan.FromSeconds(3));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("ul[aria-label='Critical readings'] li")));
    }

    [Fact]
    public async Task A_critical_reading_pins_even_while_the_stream_is_paused()
    {
        await StartedAsync();
        Stream.SetPaused(true);

        Rig.Hub.Deliver(RealtimeMethods.PriorityAlerted, StreamKit.Critical(1));
        await ClientRig.UntilAsync(() => Stream.Pinned.Count == 1, "the pin");

        Assert.True(Stream.Paused);
    }

    [Fact]
    public async Task The_same_reading_arriving_in_its_batch_is_marked_critical_in_the_list()
    {
        await StartedAsync();

        Rig.Hub.Deliver(RealtimeMethods.PriorityAlerted, StreamKit.Critical(1, StreamKit.Meter, 250, secondsAfterStart: 5));
        await ClientRig.UntilAsync(() => Stream.Pinned.Count == 1, "the pin");

        Rig.Hub.Deliver(
            RealtimeMethods.ReadingsArrived,
            StreamKit.Batch(1, StreamKit.Reading(StreamKit.Meter, 250, 5), StreamKit.Reading(StreamKit.Meter, 20, 6)));
        await ClientRig.UntilAsync(() => Stream.RowCount == 2, "the batch");

        var rows = Stream.Rows(null);

        Assert.False(rows[0].Critical);
        Assert.True(rows[1].Critical);
        Assert.Equal("power spike", rows[1].Reason);
    }

    [Fact]
    public async Task The_stream_is_filtered_by_the_devices_the_search_matched()
    {
        await StartedAsync();

        Rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, StreamKit.Batch(1, StreamKit.Reading(StreamKit.Meter, 1, 1), StreamKit.Reading(StreamKit.Pump, 2, 2)));
        await ClientRig.UntilAsync(() => Stream.RowCount == 2, "the batch");

        var rows = Stream.Rows(new HashSet<string> { StreamKit.Pump });

        Assert.Equal(StreamKit.Pump, Assert.Single(rows).Reading.MacAddress);
    }

    [Fact]
    public async Task The_panel_does_not_redraw_unless_something_it_shows_changed()
    {
        await StartedAsync();
        var cut = Render<LiveStreamPanel>();

        cut.WaitForState(() => cut.RenderCount >= 1);
        var before = cut.RenderCount;

        // A parent re-rendering with the same parameters is not a reason to redraw the list.
        cut.Render();

        Assert.Equal(before, cut.RenderCount);

        Rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, StreamKit.Batch(1, StreamKit.Reading(StreamKit.Meter, 1, 1)));

        cut.WaitForAssertion(() => Assert.True(cut.RenderCount > before));
    }
}

/// <summary>A command's state follows it from Pending to Acknowledged, with an icon and a word at every step.</summary>
public class CommandControlsComponentTests : StreamComponentTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private IRenderedComponent<CommandControls> RenderFor(ActuatorKind kind, string mac = StreamKit.Valve) =>
        Render<CommandControls>(parameters => parameters.Add(controls => controls.Sensor, StreamKit.Actuator(mac, "Dock Valve", kind)));

    private static Command Now(CommandState state, string? failure = null) =>
        StreamKit.Cmd(StreamKit.Valve, ActuatorKind.Valve, CommandAction.ValveClose, state, Id, failure: failure);

    private static string Chip(string label) => $"[aria-label='Command {label}']";

    [Fact]
    public void A_valve_is_offered_open_and_close_and_nothing_else()
    {
        var cut = RenderFor(ActuatorKind.Valve);

        var labels = cut.FindAll(".smartx-commands__buttons button").Select(button => button.TextContent.Trim()).ToList();

        Assert.Equal(["Open valve", "Close valve"], labels);
    }

    [Fact]
    public void A_dimmer_is_offered_a_level_control_and_no_on_off_buttons()
    {
        var cut = RenderFor(ActuatorKind.Dimmer);

        Assert.Empty(cut.FindAll(".smartx-commands__buttons"));
        Assert.Contains("Set level", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_state_chip_follows_the_command_from_pending_through_delivered_to_acknowledged()
    {
        Api.Issue = (_, _, _) => Now(CommandState.Pending);
        await PreloadFeedsAsync();
        var cut = RenderFor(ActuatorKind.Valve);
        await SettledAsync(cut);

        cut.FindAll(".smartx-commands__buttons button").Single(button => button.TextContent.Contains("Close valve", StringComparison.Ordinal)).Click();

        // Shown at once, before the feed has said anything.
        cut.WaitForElement(Chip("Pending"));
        Assert.Equal((StreamKit.Valve, CommandAction.ValveClose, (double?)null), Assert.Single(Api.CommandsIssued));

        Rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(1, Now(CommandState.Delivered), CommandEvent.Delivered));
        cut.WaitForElement(Chip("Delivered"));
        Assert.Empty(cut.FindAll(Chip("Pending")));

        Rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(2, Now(CommandState.Acknowledged), CommandEvent.Acknowledged));
        cut.WaitForElement(Chip("Acknowledged"));
        Assert.Empty(cut.FindAll(Chip("Delivered")));

        // One command, one row: the chip moved, it did not add a line.
        Assert.Single(cut.FindAll(".smartx-commands__recent li"));
    }

    [Fact]
    public async Task A_command_that_times_out_says_so_in_words_and_gives_the_reason()
    {
        Api.Issue = (_, _, _) => Now(CommandState.Pending);
        await PreloadFeedsAsync();
        var cut = RenderFor(ActuatorKind.Valve);
        await SettledAsync(cut);

        cut.FindAll(".smartx-commands__buttons button")[0].Click();
        cut.WaitForElement(Chip("Pending"));

        Rig.Hub.Deliver(
            RealtimeMethods.CommandChanged,
            new CommandUpdate(1, Now(CommandState.TimedOut, "Nobody collected it within 30 s."), CommandEvent.TimedOut));

        var chip = cut.WaitForElement(Chip("Timed out"));
        Assert.Contains("Timed out", chip.TextContent, StringComparison.Ordinal);
        Assert.Contains("Nobody collected it", cut.Find(".smartx-commands__recent").TextContent, StringComparison.Ordinal);
    }
}

/// <summary>The undo stack says what cannot be undone before anyone presses anything, and shows results as the API gave them.</summary>
public class UndoPanelComponentTests : StreamComponentTests
{
    private static UndoStackEntry Entry(params Command[] commands) =>
        new(Guid.NewGuid(), StreamKit.T0, "ValveClose on a valve", null, commands, CommandRules.StatusOf(commands));

    private static IRenderedComponent<UndoPanel> Open(UndoPanelComponentTests test) => test.Render<UndoPanel>();

    [Fact]
    public void An_entry_that_cannot_be_undone_gives_the_servers_reason_before_anything_is_pressed()
    {
        var timedOut = StreamKit.Cmd(StreamKit.Valve, ActuatorKind.Valve, CommandAction.ValveClose, CommandState.TimedOut);
        Api.Stack = () => [Entry(timedOut)];

        var cut = Open(this);

        cut.WaitForAssertion(() => Assert.Contains("Cannot be undone.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(CommandRules.WhyNotUndoable(timedOut)!, cut.Markup, StringComparison.Ordinal);

        // The button says what pressing it really does, and nothing has been sent.
        Assert.Contains("Clear last", cut.Find("button").TextContent, StringComparison.Ordinal);
        Assert.Equal(0, Api.UndoCalls);

    }

    [Fact]
    public void An_entry_that_can_be_undone_offers_undo_and_gives_no_warning()
    {
        var acknowledged = StreamKit.Cmd(StreamKit.Valve, ActuatorKind.Valve, CommandAction.ValveClose, CommandState.Acknowledged);
        Api.Stack = () => [Entry(acknowledged)];

        var cut = Open(this);

        cut.WaitForAssertion(() => Assert.Contains("Undo last", cut.Find("button").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Cannot be undone.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_is_shown_exactly_as_the_api_worded_it()
    {
        var timedOut = StreamKit.Cmd(StreamKit.Valve, ActuatorKind.Valve, CommandAction.ValveClose, CommandState.TimedOut);
        const string reason = "The command timed out, so the gateway does not know whether the device applied it.";
        Api.Stack = () => [Entry(timedOut)];
        Api.Undo = () => new UndoResult(
            UndoOutcome.Refused,
            "Nothing was undone: the entry could not be reverted.",
            null,
            [new UndoItem(timedOut.Id, StreamKit.Valve, UndoItemResult.Refused, null, reason)]);

        await PreloadFeedsAsync();
        var cut = Open(this);
        cut.WaitForAssertion(() => Assert.False(cut.Find("button").HasAttribute("disabled")));
        await SettledAsync(cut);

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Refused", cut.Find(".smartx-undo-result").TextContent, StringComparison.Ordinal));
        var result = cut.Find(".smartx-undo-result").TextContent;

        Assert.Contains("Nothing was undone: the entry could not be reverted.", result, StringComparison.Ordinal);
        Assert.Contains(reason, result, StringComparison.Ordinal);
        Assert.Equal(1, Api.UndoCalls);
    }

    [Fact]
    public async Task A_partial_result_names_what_was_restored_and_what_was_refused_and_why()
    {
        var done = StreamKit.Cmd(StreamKit.Valve, ActuatorKind.Valve, CommandAction.ValveClose, CommandState.Acknowledged);
        var silent = StreamKit.Cmd("B8:27:EB:10:00:2B", ActuatorKind.Valve, CommandAction.ValveClose, CommandState.TimedOut);
        Api.Stack = () => [Entry(done, silent)];
        Api.Undo = () => new UndoResult(
            UndoOutcome.PartiallyReverted,
            "1 of 2 devices is being restored.",
            null,
            [
                new UndoItem(done.Id, StreamKit.Valve, UndoItemResult.RestoreIssued, Guid.NewGuid(), null),
                new UndoItem(silent.Id, "B8:27:EB:10:00:2B", UndoItemResult.Refused, null, "It timed out, so its real state is unknown."),
            ]);

        await PreloadFeedsAsync();
        var cut = Open(this);
        cut.WaitForAssertion(() => Assert.False(cut.Find("button").HasAttribute("disabled")));
        await SettledAsync(cut);

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Partly reverted", cut.Find(".smartx-undo-result").TextContent, StringComparison.Ordinal));
        var items = cut.FindAll(".smartx-undo-items li").Select(item => item.TextContent).ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains("restore command issued", items[0], StringComparison.Ordinal);
        Assert.Contains("refused", items[1], StringComparison.Ordinal);
        Assert.Contains("its real state is unknown", items[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_zone_wide_entry_is_summed_up_and_the_part_that_cannot_be_undone_is_called_out()
    {
        var composite = Guid.NewGuid();
        var ok = StreamKit.Cmd(StreamKit.Valve, ActuatorKind.Valve, CommandAction.ValveClose, CommandState.Acknowledged, composite: composite);
        var bad = StreamKit.Cmd("B8:27:EB:10:00:2B", ActuatorKind.Valve, CommandAction.ValveClose, CommandState.TimedOut, composite: composite);
        Api.Stack = () => [new UndoStackEntry(Guid.NewGuid(), StreamKit.T0, "ValveClose on 2 devices", composite, [ok, bad], CompositeStatus.PartiallyApplied)];

        var cut = Open(this);

        cut.WaitForAssertion(() => Assert.Contains("Partly applied", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Part of this cannot be undone.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("2 devices", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_stack_says_there_is_nothing_to_undo_and_the_button_is_off()
    {
        var cut = Open(this);

        cut.WaitForAssertion(() => Assert.Contains("Nothing to undo", cut.Markup, StringComparison.Ordinal));
        Assert.True(cut.Find("button").HasAttribute("disabled"));
    }
}

/// <summary>A zone-wide action shows exactly which devices it will reach, before it sends anything.</summary>
public class ZoneActionComponentTests : StreamComponentTests
{
    private static readonly Guid Composite = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FirstId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecondId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string SecondMac = "B8:27:EB:10:00:2B";

    public ZoneActionComponentTests()
    {
        Api.Preview = (zone, action, level) => new ZonePreview(
            zone,
            "Cold Store",
            action,
            level,
            [
                new ZoneTarget(StreamKit.Valve, "Freezer Glycol Valve", "zone-f", ActuatorKind.Valve, 1),
                new ZoneTarget(SecondMac, "Chiller Valve A", "zone-g", ActuatorKind.Valve, 1),
            ]);

        Api.IssueZone = (zone, action, level) => new CompositeCommandResult(
            Composite,
            zone,
            action,
            [Child(FirstId, StreamKit.Valve, CommandState.Pending), Child(SecondId, SecondMac, CommandState.Pending)],
            CompositeStatus.InProgress);
    }

    private static Command Child(Guid id, string mac, CommandState state) =>
        StreamKit.Cmd(mac, ActuatorKind.Valve, CommandAction.ValveClose, state, id, composite: Composite);

    private async Task<IRenderedComponent<ZoneActionPanel>> ChooseZoneAsync()
    {
        await PreloadFeedsAsync();
        RenderPopoverHost();
        var cut = Render<ZoneActionPanel>();

        // Choosing from the picker is the picker calling back with the zone's id; the select's popup is MudBlazor's own.
        var picker = cut.FindComponent<ZonePicker>();
        await cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync("zone-3"));
        await SettledAsync(cut);

        return cut;
    }

    private static void Press(IRenderedComponent<ZoneActionPanel> cut, string text) =>
        cut.FindAll("button").Single(button => button.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    [Fact]
    public async Task The_confirmation_lists_every_device_and_nothing_has_been_sent_yet()
    {
        var cut = await ChooseZoneAsync();

        Press(cut, "Apply to zone");

        var dialog = cut.WaitForElement(".smartx-modal [role=alertdialog]");

        Assert.Contains("Send close valve to 2 devices?", dialog.TextContent, StringComparison.Ordinal);
        Assert.Contains("Freezer Glycol Valve", dialog.TextContent, StringComparison.Ordinal);
        Assert.Contains("Chiller Valve A", dialog.TextContent, StringComparison.Ordinal);
        Assert.Contains("Nothing has been sent yet", dialog.TextContent, StringComparison.Ordinal);

        // The preview was asked for; the action was not issued.
        Assert.Single(Api.ZonePreviews);
        Assert.Empty(Api.ZoneIssues);
    }

    [Fact]
    public async Task Cancelling_the_confirmation_sends_nothing()
    {
        var cut = await ChooseZoneAsync();
        Press(cut, "Apply to zone");
        cut.WaitForElement(".smartx-modal");

        Press(cut, "Cancel");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".smartx-modal")));
        Assert.Empty(Api.ZoneIssues);
    }

    [Fact]
    public async Task Confirming_sends_one_zone_action_and_shows_each_child_then_the_overall_result()
    {
        var cut = await ChooseZoneAsync();
        await ListeningAsync(Rig, RealtimeMethods.CommandChanged);

        Press(cut, "Apply to zone");
        cut.WaitForElement(".smartx-modal");
        Press(cut, "Send to 2 devices");

        var issued = Assert.Single(Api.ZoneIssues);
        Assert.Equal(("zone-3", CommandAction.ValveClose), issued);

        // Both children, each with its own state, and the whole still in progress.
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".smartx-zone-action__children li").Count));
        Assert.Contains("In progress", cut.Find(".smartx-zone-action__result").TextContent, StringComparison.Ordinal);

        Rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(1, Child(FirstId, StreamKit.Valve, CommandState.Acknowledged), CommandEvent.Acknowledged));
        Rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(2, Child(SecondId, SecondMac, CommandState.Acknowledged), CommandEvent.Acknowledged));

        cut.WaitForAssertion(() => Assert.Contains("Applied", cut.Find(".smartx-zone-action__result .smartx-status").TextContent, StringComparison.Ordinal));
        Assert.Equal(2, cut.FindAll(".smartx-zone-action__children [aria-label='Command Acknowledged']").Count);
    }

    [Fact]
    public async Task One_child_timing_out_makes_the_whole_partly_applied()
    {
        var cut = await ChooseZoneAsync();
        await ListeningAsync(Rig, RealtimeMethods.CommandChanged);

        Press(cut, "Apply to zone");
        cut.WaitForElement(".smartx-modal");
        Press(cut, "Send to 2 devices");
        cut.WaitForElement(".smartx-zone-action__result");

        Rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(1, Child(FirstId, StreamKit.Valve, CommandState.Acknowledged), CommandEvent.Acknowledged));
        Rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(2, Child(SecondId, SecondMac, CommandState.TimedOut), CommandEvent.TimedOut));

        cut.WaitForAssertion(() => Assert.Contains("Partly applied", cut.Find(".smartx-zone-action__result .smartx-status").TextContent, StringComparison.Ordinal));
        Assert.Single(cut.FindAll(".smartx-zone-action__children [aria-label='Command Timed out']"));
    }

    [Fact]
    public void Without_a_zone_chosen_the_action_cannot_be_started()
    {
        RenderPopoverHost();
        var cut = Render<ZoneActionPanel>();

        var button = cut.FindAll("button").Single(b => b.TextContent.Contains("Apply to zone", StringComparison.Ordinal));

        Assert.True(button.HasAttribute("disabled"));
        Assert.Empty(Api.ZonePreviews);
    }
}

/// <summary>The timeline chart draws the envelope, the average line and the event markers, and can be read as a table.</summary>
public class TimelineChartComponentTests : StreamComponentTests
{
    private IRenderedComponent<TimelineChart> Draw(SensorTimeline? timeline = null) =>
        Render<TimelineChart>(parameters => parameters
            .Add(chart => chart.Timeline, timeline ?? StreamKit.Spike())
            .Add(chart => chart.Width, 480)
            .Add(chart => chart.Title, "Main Incomer readings"));

    [Fact]
    public void It_draws_the_min_to_max_envelope_and_the_average_as_a_two_pixel_line()
    {
        var cut = Draw();

        Assert.NotEmpty(cut.FindAll("polygon.smartx-chart__envelope"));

        var average = cut.Find("polyline.smartx-chart__average");
        Assert.Equal("2", average.GetAttribute("stroke-width"));

        // The band is first in the drawing and the line after it, so nothing covers the extreme.
        var markup = cut.Find("svg.smartx-chart__svg").InnerHtml;
        Assert.True(markup.IndexOf("smartx-chart__envelope", StringComparison.Ordinal) < markup.IndexOf("smartx-chart__average", StringComparison.Ordinal));
    }

    [Fact]
    public void It_has_one_y_axis_with_labels_in_text_that_is_not_the_series_colour()
    {
        var cut = Draw();

        Assert.NotEmpty(cut.FindAll("text.smartx-chart__axis"));
        Assert.NotEmpty(cut.FindAll("line.smartx-chart__grid"));
        Assert.DoesNotContain("stroke=", cut.Find("text.smartx-chart__axis").OuterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_kind_of_event_is_a_marker_with_its_own_shape_an_icon_and_a_name()
    {
        var cut = Draw();

        var markers = cut.FindAll("g.smartx-chart__marker");

        Assert.Equal(4, markers.Count);
        Assert.Equal(4, markers.Select(marker => marker.GetAttribute("data-kind")).Distinct().Count());

        // Shape: a different class per kind, so they can be told apart with the colour taken away.
        Assert.Equal(4, markers.Select(marker => marker.ClassList.Single(c => c.StartsWith("smartx-chart__marker--", StringComparison.Ordinal))).Distinct().Count());

        // Icon and name: every marker carries both.
        Assert.All(markers, marker =>
        {
            Assert.NotNull(marker.QuerySelector("g.smartx-chart__icon svg, g.smartx-chart__icon path"));
            Assert.False(string.IsNullOrWhiteSpace(marker.GetAttribute("aria-label")));
        });

        // At least 8 px across (a radius of 8 for the circle; the others are drawn to the same extent), with a wider hit area.
        Assert.NotNull(cut.Find("g.smartx-chart__marker--circle circle.smartx-chart__shape").GetAttribute("r"));
        Assert.All(markers, marker => Assert.Equal("13", marker.QuerySelector("circle[fill=transparent]")!.GetAttribute("r")));
    }

    [Fact]
    public void A_legend_names_each_kind_of_event()
    {
        var cut = Draw();

        var legend = cut.Find("ul[aria-label='Event key']").TextContent;

        Assert.Contains("Command", legend, StringComparison.Ordinal);
        Assert.Contains("Alert raised", legend, StringComparison.Ordinal);
        Assert.Contains("Alert cleared", legend, StringComparison.Ordinal);
        Assert.Contains("Trust change", legend, StringComparison.Ordinal);
    }

    [Fact]
    public void Hovering_a_bucket_shows_a_crosshair_and_its_minimum_maximum_average_and_count()
    {
        var cut = Draw();

        // The sixth bucket is the spike.
        cut.FindAll("rect.smartx-chart__hit")[5].MouseEnter();

        var tip = cut.WaitForElement(".smartx-chart__tooltip").TextContent;

        Assert.Contains("Minimum 18", tip, StringComparison.Ordinal);
        Assert.Contains("maximum 900", tip, StringComparison.Ordinal);
        Assert.Contains("average 34", tip, StringComparison.Ordinal);
        Assert.Contains("60 readings", tip, StringComparison.Ordinal);
        Assert.Contains("OutOfRange raised", tip, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("line.smartx-chart__crosshair"));
    }

    [Fact]
    public void The_hit_areas_are_wider_than_the_marks()
    {
        var cut = Draw();

        var hits = cut.FindAll("rect.smartx-chart__hit");

        Assert.Equal(10, hits.Count);
        Assert.All(hits, hit => Assert.True(double.Parse(hit.GetAttribute("width")!, System.Globalization.CultureInfo.InvariantCulture) >= 2));
    }

    [Fact]
    public void Arrow_keys_step_through_the_buckets_so_the_tooltip_is_reachable_without_a_mouse()
    {
        var cut = Draw();

        cut.Find(".smartx-chart").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });

        Assert.Contains("Minimum", cut.WaitForElement(".smartx-chart__tooltip").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_data_can_be_read_as_a_table()
    {
        var cut = Draw();

        cut.FindAll(".smartx-chart__toolbar button").Single().Click();

        var tables = cut.FindAll("table.smartx-chart__table");

        Assert.Equal(2, tables.Count);
        Assert.Equal(10, tables[0].QuerySelectorAll("tbody tr").Length);
        Assert.Equal(4, tables[1].QuerySelectorAll("tbody tr").Length);
        Assert.Contains("900", tables[0].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("svg.smartx-chart__svg"));
    }

    [Fact]
    public void A_state_sensor_is_drawn_as_a_step_line()
    {
        var timeline = new SensorTimeline(
            StreamKit.Valve,
            "state",
            TimelineShape.Transitions,
            StreamKit.T0,
            StreamKit.T0.AddMinutes(10),
            0,
            2,
            [],
            [new StateTransition(StreamKit.T0, true), new StateTransition(StreamKit.T0.AddMinutes(4), false)],
            []);

        var cut = Draw(timeline);

        Assert.NotEmpty(cut.Find("path.smartx-chart__step").GetAttribute("d")!);
        Assert.Empty(cut.FindAll("polyline.smartx-chart__average"));
    }

    [Fact]
    public void With_nothing_in_the_range_it_says_so_in_words()
    {
        var cut = Draw(StreamKit.EmptyTimeline(StreamKit.Meter));

        Assert.Contains("No readings in this range yet.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("svg.smartx-chart__svg"));
    }
}

/// <summary>The device detail asks for a timeline sized to its width, and chooses a range that survives navigation.</summary>
public class DeviceDetailComponentTests : StreamComponentTests
{
    [Fact]
    public async Task Opening_a_device_loads_its_timeline_and_joins_its_topic_and_closing_leaves_it()
    {
        Api.Timeline = _ => StreamKit.Spike();
        RenderPopoverHost();

        var cut = Render<DeviceDetail>(parameters => parameters.Add(detail => detail.Mac, StreamKit.Meter));

        await ClientRig.UntilAsync(() => Rig.Hub.Subscribed.Contains($"device:{StreamKit.Meter}"), "the device topic to be joined");
        cut.WaitForElement("g.smartx-chart__marker");
        Assert.Equal(1, Api.TimelineCalls);

        await DisposeComponentsAsync();

        await ClientRig.UntilAsync(() => !Rig.Hub.Subscribed.Contains($"device:{StreamKit.Meter}"), "the device topic to be left");
    }

    [Fact]
    public async Task Choosing_another_range_asks_for_that_range_and_is_remembered()
    {
        Api.Timeline = _ => StreamKit.Spike();
        await PreloadFeedsAsync();
        RenderPopoverHost();
        var cut = Render<DeviceDetail>(parameters => parameters.Add(detail => detail.Mac, StreamKit.Meter));
        cut.WaitForElement("g.smartx-chart__marker");
        await ClientRig.UntilAsync(() => Rig.Snapshots.DeviceCalls > 0, "the device snapshot");
        await SettledAsync(cut);
        var before = Api.TimelineCalls;

        cut.FindAll("[aria-label='Timeline range'] button").Single(button => button.TextContent.Contains("6 h", StringComparison.Ordinal)).Click();

        await ClientRig.UntilAsync(() => Api.TimelineCalls > before, "the timeline to be asked for again");
        Assert.Equal(SmartX.Client.State.TimelineRange.SixHours, State.TimelineRange);
    }
}
