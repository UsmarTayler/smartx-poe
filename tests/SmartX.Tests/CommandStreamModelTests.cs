using SmartX.Client.CommandStream;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// The Command Stream page's logic that has no markup: lane rates, alert order, which commands a device is offered, how wide
/// a chart is, and what the timeline chart draws. Tested as plain code because none of it needs a renderer.
/// </summary>
public class PipelineRatesTests
{
    private static PipelineStats Stats(long standard, long critical, DateTimeOffset at, double standardWait = 0.4, double criticalWait = 0.1) =>
        new(
            [new LaneStats("standard", 3, standard, standardWait, 2), new LaneStats("critical", 0, critical, criticalWait, 1)],
            10_000,
            0,
            at);

    [Fact]
    public void The_first_sample_has_no_rate_because_there_is_nothing_to_compare_it_with()
    {
        var views = new PipelineRates().Update(Stats(100, 0, StreamKit.T0));

        Assert.All(views, view => Assert.Null(view.ProcessedPerSecond));
    }

    [Fact]
    public void A_rate_is_the_difference_between_two_samples_over_the_time_between_them()
    {
        var rates = new PipelineRates();
        rates.Update(Stats(100, 0, StreamKit.T0));

        var views = rates.Update(Stats(160, 4, StreamKit.T0.AddSeconds(2)));

        Assert.Equal(30, views.Single(view => view.Lane == "standard").ProcessedPerSecond);
        Assert.Equal(2, views.Single(view => view.Lane == "critical").ProcessedPerSecond);
    }

    [Fact]
    public void A_total_that_goes_down_means_the_api_restarted_and_is_not_shown_as_a_negative_rate()
    {
        var rates = new PipelineRates();
        rates.Update(Stats(5_000, 10, StreamKit.T0));

        var views = rates.Update(Stats(12, 0, StreamKit.T0.AddSeconds(2)));

        Assert.All(views, view => Assert.Null(view.ProcessedPerSecond));
    }

    [Fact]
    public void The_advantage_is_the_measured_ratio_of_the_two_average_waits()
    {
        var views = new PipelineRates().Update(Stats(10, 1, StreamKit.T0, standardWait: 0.4, criticalWait: 0.1));

        Assert.Equal(4, PipelineRates.Advantage(views));
    }

    [Fact]
    public void There_is_no_advantage_to_state_until_both_lanes_have_a_wait_to_compare()
    {
        var views = new PipelineRates().Update(Stats(10, 0, StreamKit.T0, criticalWait: 0));

        Assert.Null(PipelineRates.Advantage(views));
    }
}

public class AlertOrderingTests
{
    [Fact]
    public void The_most_severe_kind_comes_first_whatever_its_age()
    {
        var older = StreamKit.Alert("B8:27:EB:10:00:01", AlertKind.Fault) with { RaisedAt = StreamKit.T0 };
        var newer = StreamKit.Alert("B8:27:EB:10:00:02", AlertKind.OutOfRange) with { RaisedAt = StreamKit.T0.AddMinutes(5) };

        var ordered = AlertOrdering.Order([newer, older]);

        Assert.Equal([AlertKind.Fault, AlertKind.OutOfRange], ordered.Select(alert => alert.Key.Kind));
    }

    [Fact]
    public void Within_one_severity_the_newest_comes_first()
    {
        var first = StreamKit.Alert("B8:27:EB:10:00:01", AlertKind.Fault) with { RaisedAt = StreamKit.T0 };
        var second = StreamKit.Alert("B8:27:EB:10:00:02", AlertKind.Fault) with { RaisedAt = StreamKit.T0.AddMinutes(1) };

        var ordered = AlertOrdering.Order([first, second]);

        Assert.Equal(["B8:27:EB:10:00:02", "B8:27:EB:10:00:01"], ordered.Select(alert => alert.Key.Mac));
    }

    [Fact]
    public void Every_kind_of_alert_has_a_severity_and_they_are_not_all_the_same()
    {
        var severities = Enum.GetValues<AlertKind>().Select(AlertOrdering.Severity).ToList();

        Assert.True(severities.Distinct().Count() > 1);
    }
}

public class CommandChoicesTests
{
    [Fact]
    public void Every_choice_offered_for_a_kind_is_an_action_that_kind_accepts()
    {
        foreach (var kind in Enum.GetValues<ActuatorKind>())
        {
            Assert.All(CommandChoices.For(kind), choice => Assert.Equal(kind, CommandActionRules.KindFor(choice.Action)));
        }
    }

    [Fact]
    public void A_pump_is_never_offered_a_valve_action()
    {
        var actions = CommandChoices.For(ActuatorKind.Pump).Select(choice => choice.Action).ToList();

        Assert.Equal([CommandAction.PumpOn, CommandAction.PumpOff], actions);
    }

    [Fact]
    public void A_dimmer_is_offered_a_level_and_nothing_else()
    {
        Assert.Equal([CommandAction.SetLevel], CommandChoices.For(ActuatorKind.Dimmer).Select(choice => choice.Action));
    }

    [Fact]
    public void The_zone_menu_offers_every_action_once()
    {
        var actions = CommandChoices.ForZone.Select(choice => choice.Action).ToList();

        Assert.Equal(actions.Count, actions.Distinct().Count());
        Assert.Equal(Enum.GetValues<CommandAction>().Length, actions.Count);
    }

    [Fact]
    public void A_command_is_described_in_the_words_on_its_button()
    {
        var command = StreamKit.Cmd(StreamKit.Valve, ActuatorKind.Valve, CommandAction.ValveClose, CommandState.Pending);

        Assert.Equal("Close valve", CommandChoices.Describe(command));
    }
}

public class ChartSizesTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(768)]
    [InlineData(1280)]
    [InlineData(3840)]
    public void A_chart_is_never_narrower_or_wider_than_the_drawing_can_use(int window)
    {
        Assert.InRange(ChartSizes.WidthFor(window), 260, 720);
    }

    [Fact]
    public void A_wider_chart_asks_for_more_points_up_to_a_ceiling()
    {
        Assert.True(ChartSizes.MaxPointsFor(720) > ChartSizes.MaxPointsFor(300));
        Assert.Equal(240, ChartSizes.MaxPointsFor(5_000));
        Assert.Equal(40, ChartSizes.MaxPointsFor(10));
    }
}

public class TimelineChartModelTests
{
    [Fact]
    public void A_spike_is_the_top_of_the_envelope_even_though_the_average_hides_it()
    {
        var geometry = TimelineChartModel.Build(StreamKit.Spike(), [], 480, 230);

        // The 900 is in one bucket whose average is 34. The y-axis has to reach the maximum, not the average.
        Assert.True(geometry.YMax >= 900, $"The axis tops out at {geometry.YMax}, below the spike.");
        Assert.Single(geometry.EnvelopeSegments);
        Assert.Single(geometry.AverageSegments);
    }

    [Fact]
    public void A_silence_breaks_the_band_and_the_line_and_is_not_joined_across()
    {
        var timeline = StreamKit.Spike();
        // Three empty minutes between buckets of one minute: longer than the model's tolerance, so a real silence.
        var withGap = timeline with { Buckets = [.. timeline.Buckets.Where((_, index) => index is not (4 or 5 or 6))] };

        var geometry = TimelineChartModel.Build(withGap, [], 480, 230);

        Assert.Equal(2, geometry.EnvelopeSegments.Count);
        Assert.Equal(2, geometry.AverageSegments.Count);
    }

    [Fact]
    public void One_or_two_missing_buckets_are_sparse_data_and_are_still_joined()
    {
        var timeline = StreamKit.Spike();
        var sparse = timeline with { Buckets = [.. timeline.Buckets.Where((_, index) => index is not (4 or 5))] };

        var geometry = TimelineChartModel.Build(sparse, [], 480, 230);

        Assert.Single(geometry.EnvelopeSegments);
    }

    [Fact]
    public void Each_kind_of_event_has_its_own_shape_and_its_own_icon()
    {
        var geometry = TimelineChartModel.Build(StreamKit.Spike(), [], 480, 230);

        Assert.Equal(4, geometry.Markers.Count);
        Assert.Equal(4, geometry.Markers.Select(marker => marker.Shape).Distinct().Count());
        Assert.Equal(4, geometry.Markers.Select(marker => marker.Icon).Distinct().Count());
    }

    [Fact]
    public void Every_marker_sits_inside_the_plot()
    {
        var geometry = TimelineChartModel.Build(StreamKit.Spike(), [], 480, 230);

        Assert.All(geometry.Markers, marker => Assert.InRange(marker.X, geometry.Plot.Left, geometry.Plot.Right));
    }

    [Fact]
    public void Readings_pushed_after_the_fetch_extend_the_chart_to_the_right()
    {
        var timeline = StreamKit.Spike();
        var live = new[] { StreamKit.Reading(StreamKit.Meter, 21, 11 * 60), StreamKit.Reading(StreamKit.Meter, 23, 12 * 60) };

        var before = TimelineChartModel.Build(timeline, [], 480, 230);
        var after = TimelineChartModel.Build(timeline, live, 480, 230);

        Assert.True(after.Buckets.Count > before.Buckets.Count);
        Assert.True(after.To > before.To);
    }

    [Fact]
    public void A_state_sensor_is_a_step_line_and_not_an_average_of_zero_and_one()
    {
        var timeline = new SensorTimeline(
            StreamKit.Valve,
            "state",
            TimelineShape.Transitions,
            StreamKit.T0,
            StreamKit.T0.AddMinutes(10),
            0,
            3,
            [],
            [
                new StateTransition(StreamKit.T0, true),
                new StateTransition(StreamKit.T0.AddMinutes(3), false),
                new StateTransition(StreamKit.T0.AddMinutes(6), true),
            ],
            []);

        var geometry = TimelineChartModel.Build(timeline, [], 480, 230);

        Assert.False(geometry.IsEmpty);
        Assert.NotEmpty(geometry.StepPath);
        Assert.Empty(geometry.AverageSegments);
    }

    [Fact]
    public void A_timeline_with_nothing_in_it_is_empty_and_says_so()
    {
        Assert.True(TimelineChartModel.Build(StreamKit.EmptyTimeline(StreamKit.Meter), [], 480, 230).IsEmpty);
    }
}
