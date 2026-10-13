using Bunit;
using SmartX.Client.Components.CommandStream;
using SmartX.Client.Components.Trust;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// What the accessibility pass changed, held in place: live regions that exist before their content does, critical readings
/// announced assertively, and an accessible name that contains what the control shows.
/// </summary>
/// <remarks>
/// None of these waits on a clock. They render a component and read the markup, which is all a screen reader gets.
/// </remarks>
public class AccessibilityTests : StreamComponentTests
{
    private static DeviceSearchHit Hit(string mac, string room) =>
        new(mac, room, "N", "zone-1", SensorCategory.Actuator, ActuatorKind.Pump, TrustBand.Nominal, []);

    [Fact]
    public async Task The_suggestions_live_region_is_in_the_page_before_the_first_suggestion()
    {
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(1);
        await PreloadFeedsAsync();

        var cut = Render<SuggestedActionsPanel>();
        await SettledAsync(cut);

        // A region that is created together with its first item is not announced, so the wrapper has to be there while empty.
        Assert.Single(cut.FindAll("[aria-live=polite][aria-relevant=additions]"));
        Assert.Empty(cut.FindAll(".smartx-suggestion"));
    }

    [Fact]
    public async Task A_suggestion_arrives_inside_the_polite_region_and_the_list_is_not_a_second_one()
    {
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(1, [SuggestionKit.Make("a", "Turn on Pump A")]);
        await PreloadFeedsAsync();

        var cut = Render<SuggestedActionsPanel>();
        await SettledAsync(cut);

        var region = cut.Find("[aria-live=polite]");

        Assert.NotEmpty(region.QuerySelectorAll(".smartx-suggestion"));
        Assert.Single(cut.FindAll("[aria-live]"));
    }

    [Fact]
    public async Task The_trust_panel_strip_has_its_live_region_even_with_nothing_to_suggest()
    {
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(1);
        await PreloadFeedsAsync();

        var cut = Render<SuggestionStrip>();
        await SettledAsync(cut);

        Assert.Single(cut.FindAll("[aria-live=polite][aria-relevant=additions]"));
    }

    [Fact]
    public async Task Pinned_critical_readings_are_announced_assertively_and_the_firehose_is_not_announced_at_all()
    {
        await PreloadFeedsAsync();

        var cut = Render<LiveStreamPanel>();
        await SettledAsync(cut);

        Assert.Single(cut.FindAll("[aria-live=assertive][aria-relevant=additions]"));

        // Every reading read aloud at the pace they arrive would make the page unusable, so the log itself stays quiet.
        foreach (var log in cut.FindAll("[role=log]"))
        {
            Assert.Equal("off", log.GetAttribute("aria-live"));
        }
    }

    [Fact]
    public void A_trust_row_is_named_by_the_text_it_shows_in_the_order_it_shows_it()
    {
        var node = new NodeTrustSnapshot(
            "B8:27:EB:10:00:20", 0.1, TrustBand.Fault, "no packet for 3 minutes",
            DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, 0, 4, []);

        var cut = Render<TrustNodeRow>(parameters => parameters.Add(p => p.Node, node));
        var name = cut.Find("button").GetAttribute("aria-label");

        // WCAG 2.5.3: someone who speaks the visible text to a voice-control tool has to hit this button.
        Assert.Equal("Fault B8:27:EB:10:00:20 no packet for 3 minutes 0.10", name);
    }

    [Fact]
    public async Task The_device_list_puts_the_list_item_around_the_button_and_not_on_it()
    {
        Api.Search = filters => new DeviceSearchResponse(filters, 1, [Hit(StreamKit.Pump, "Coolant Pump")]);
        await PreloadFeedsAsync();
        RenderPopoverHost();

        var cut = Render<DevicePanel>();
        var button = cut.WaitForElement(".smartx-device-row");

        // A button's role cannot be a listitem and still carry aria-pressed, so the item is the wrapper.
        Assert.Null(button.GetAttribute("role"));
        Assert.Equal("listitem", button.ParentElement!.GetAttribute("role"));
    }
}
