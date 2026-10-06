using SmartX.Api.History;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Downsampling: a bounded number of buckets, with the extremes kept visible.
/// </summary>
public class TimelineBuilderTests
{
    private const string Mac = "AA:BB:CC:00:00:01";

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly SensorRegistration Thermometer = new(
        Mac, new DeploymentLocation("Plant Room", "zone-1", "N1"), SensorCategory.Environmental, "°C",
        new ValueRange(-50, 1000), TelemetryPayloadKind.Real);

    private static readonly SensorRegistration Valve = new(
        Mac, new DeploymentLocation("Plant Room", "zone-1", "N1"), SensorCategory.Actuator, "state",
        new ValueRange(0, 1), TelemetryPayloadKind.State, null, ActuatorKind.Valve);

    private static long _sequence;

    private static HistoryEntry Reading(DateTimeOffset at, double value) => new()
    {
        Sequence = ++_sequence,
        Timestamp = at,
        MacAddress = Mac,
        Kind = HistoryKind.Reading,
        Value = value,
        Unit = "°C",
    };

    private static HistoryEntry Event(DateTimeOffset at, HistoryKind kind, string summary) =>
        HistoryEntry.FromEvent(++_sequence, at, Mac, kind, new HistoryEventDetails(summary));

    /// <summary>One reading a second for a given number of seconds, all the same value.</summary>
    private static List<HistoryEntry> Steady(int seconds, double value)
    {
        List<HistoryEntry> entries = [];

        for (var second = 0; second < seconds; second++)
        {
            entries.Add(Reading(T0.AddSeconds(second), value));
        }

        return entries;
    }

    [Fact]
    public void A_single_spike_among_normal_readings_survives_downsampling_in_min_and_max()
    {
        // An hour of readings at 20 °C, once a second, with one reading of 900 °C halfway through.
        var entries = Steady(3600, 20);
        entries[1800] = Reading(T0.AddSeconds(1800), 900);

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddHours(1), maxPoints: 60);

        // 3,600 readings became 60 buckets of 60.
        Assert.Equal(60, timeline.Buckets.Count);
        Assert.Equal(3600, timeline.ReadingCount);

        var spiked = Assert.Single(timeline.Buckets, bucket => bucket.Max > 100);
        Assert.Equal(900, spiked.Max);
        Assert.Equal(20, spiked.Min);
        Assert.Equal(60, spiked.Count);

        // This is why the average alone is the wrong summary: 59 readings of 20 and one of 900 average to
        // about 34.7, which looks like a slightly warm minute. The spike is invisible in the average.
        Assert.InRange(spiked.Average, 34, 35);

        // Every other bucket is flat, so the spike is the only thing the chart would draw.
        Assert.All(timeline.Buckets.Where(bucket => bucket != spiked), bucket =>
        {
            Assert.Equal(20, bucket.Max);
            Assert.Equal(20, bucket.Min);
        });
    }

    [Fact]
    public void A_dip_survives_the_same_way_a_spike_does()
    {
        var entries = Steady(600, 50);
        entries[300] = Reading(T0.AddSeconds(300), -40);

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddSeconds(600), maxPoints: 10);

        Assert.Equal(-40, timeline.Buckets.Min(bucket => bucket.Min));
    }

    [Theory]
    [InlineData(3600, 1)]
    [InlineData(3600, 7)]
    [InlineData(3600, 60)]
    [InlineData(3600, 200)]
    [InlineData(3600, 3599)]
    [InlineData(3600, 10_000)]
    [InlineData(1000, 333)]
    public void A_timeline_never_has_more_buckets_than_it_was_asked_for(int seconds, int maxPoints)
    {
        var entries = Steady(seconds, 1);

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddSeconds(seconds), maxPoints);

        Assert.InRange(timeline.Buckets.Count, 1, maxPoints);
    }

    [Fact]
    public void Every_reading_is_counted_in_exactly_one_bucket()
    {
        var entries = Steady(1000, 5);

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddSeconds(1000), maxPoints: 37);

        Assert.Equal(1000, timeline.Buckets.Sum(bucket => bucket.Count));
        Assert.Equal(1000, timeline.ReadingCount);
    }

    [Fact]
    public void Buckets_are_in_time_order_and_do_not_overlap()
    {
        var entries = Steady(500, 5);

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddSeconds(500), maxPoints: 20);

        for (var i = 1; i < timeline.Buckets.Count; i++)
        {
            Assert.True(timeline.Buckets[i].Start >= timeline.Buckets[i - 1].End);
        }

        Assert.True(timeline.Buckets[^1].End <= T0.AddSeconds(500));
    }

    [Fact]
    public void A_gap_in_the_data_is_a_gap_in_the_buckets_and_not_a_run_of_zeros()
    {
        // Readings in the first and last ten seconds of an hour, nothing between.
        var entries = Steady(10, 20);
        entries.AddRange(Steady(10, 20).Select((_, second) => Reading(T0.AddSeconds(3590 + second), 20)));

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddHours(1), maxPoints: 60);

        Assert.Equal(2, timeline.Buckets.Count);
        Assert.Equal(20, timeline.ReadingCount);
    }

    [Fact]
    public void The_average_is_the_mean_of_the_bucket()
    {
        List<HistoryEntry> entries = [Reading(T0, 10), Reading(T0.AddSeconds(1), 20), Reading(T0.AddSeconds(2), 60)];

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddSeconds(10), maxPoints: 1);

        var bucket = Assert.Single(timeline.Buckets);
        Assert.Equal(30, bucket.Average);
        Assert.Equal(10, bucket.Min);
        Assert.Equal(60, bucket.Max);
    }

    [Fact]
    public void A_range_with_no_readings_is_an_empty_timeline_and_not_an_error()
    {
        var timeline = TimelineBuilder.Build(Thermometer, [], T0, T0.AddHours(1), maxPoints: 100);

        Assert.Empty(timeline.Buckets);
        Assert.Equal(0, timeline.ReadingCount);
        Assert.Equal(TimelineShape.Buckets, timeline.Shape);
    }

    [Fact]
    public void The_bucket_width_is_reported_so_a_chart_can_draw_it()
    {
        var timeline = TimelineBuilder.Build(Thermometer, Steady(10, 1), T0, T0.AddSeconds(3600), maxPoints: 60);

        Assert.Equal(60, timeline.BucketSeconds);
    }

    [Fact]
    public void A_state_sensor_returns_its_transitions_and_no_buckets()
    {
        // Shut for three readings, open for three, shut again.
        List<HistoryEntry> entries =
        [
            Reading(T0, 0), Reading(T0.AddSeconds(5), 0), Reading(T0.AddSeconds(10), 0),
            Reading(T0.AddSeconds(15), 1), Reading(T0.AddSeconds(20), 1), Reading(T0.AddSeconds(25), 1),
            Reading(T0.AddSeconds(30), 0),
        ];

        var timeline = TimelineBuilder.Build(Valve, entries, T0, T0.AddMinutes(1), maxPoints: 10);

        Assert.Equal(TimelineShape.Transitions, timeline.Shape);
        Assert.Empty(timeline.Buckets);
        Assert.Equal(7, timeline.ReadingCount);

        // The state at the start, and each change. Seven readings, three facts.
        Assert.Equal(
            [(T0, false), (T0.AddSeconds(15), true), (T0.AddSeconds(30), false)],
            timeline.Transitions.Select(transition => (transition.Timestamp, transition.State)));
        Assert.Equal(0, timeline.BucketSeconds);
    }

    [Fact]
    public void A_state_that_never_changes_is_one_transition()
    {
        var timeline = TimelineBuilder.Build(Valve, Steady(1000, 1), T0, T0.AddSeconds(1000), maxPoints: 10);

        var only = Assert.Single(timeline.Transitions);
        Assert.True(only.State);
    }

    [Fact]
    public void Commands_alerts_and_trust_transitions_come_back_as_markers_in_time_order()
    {
        var entries = Steady(60, 20);
        entries.Add(Event(T0.AddSeconds(10), HistoryKind.Command, "Issued: ValveClose"));
        entries.Add(Event(T0.AddSeconds(20), HistoryKind.AlertRaised, "OutOfRange raised"));
        entries.Add(Event(T0.AddSeconds(30), HistoryKind.TrustTransition, "Trust moved from Nominal to Degrading"));
        entries.Add(Event(T0.AddSeconds(40), HistoryKind.AlertCleared, "OutOfRange cleared"));
        entries = [.. entries.OrderBy(entry => entry.Timestamp).ThenBy(entry => entry.Sequence)];

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddMinutes(1), maxPoints: 10);

        Assert.Equal(
            [HistoryKind.Command, HistoryKind.AlertRaised, HistoryKind.TrustTransition, HistoryKind.AlertCleared],
            timeline.Markers.Select(marker => marker.Kind));
        Assert.Equal("Issued: ValveClose", timeline.Markers[0].Label);

        // Markers are not readings: they are drawn on the line, and do not move it.
        Assert.Equal(60, timeline.ReadingCount);
    }

    [Fact]
    public void A_search_is_not_a_marker_on_a_sensors_timeline()
    {
        List<HistoryEntry> entries = [Event(T0, HistoryKind.Search, "Searched all devices")];

        Assert.Empty(TimelineBuilder.Build(Thermometer, entries, T0, T0.AddMinutes(1), maxPoints: 10).Markers);
    }

    [Fact]
    public void Markers_appear_on_a_state_timeline_too()
    {
        List<HistoryEntry> entries = [Reading(T0, 0), Event(T0.AddSeconds(1), HistoryKind.Command, "Issued: ValveOpen"), Reading(T0.AddSeconds(2), 1)];

        var timeline = TimelineBuilder.Build(Valve, entries, T0, T0.AddMinutes(1), maxPoints: 10);

        Assert.Single(timeline.Markers);
        Assert.Equal(2, timeline.Transitions.Count);
    }

    [Fact]
    public void Asking_for_no_points_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineBuilder.Build(Thermometer, [], T0, T0.AddHours(1), maxPoints: 0));
    }

    [Fact]
    public void A_large_range_is_handled_in_one_pass()
    {
        // Fifty thousand readings down to a hundred buckets. The point of the feature, stated as a test.
        List<HistoryEntry> entries = [];

        for (var i = 0; i < 50_000; i++)
        {
            entries.Add(Reading(T0.AddSeconds(i), i == 25_000 ? 900 : 20));
        }

        var timeline = TimelineBuilder.Build(Thermometer, entries, T0, T0.AddSeconds(50_000), maxPoints: 100);

        Assert.Equal(100, timeline.Buckets.Count);
        Assert.Equal(900, timeline.Buckets.Max(bucket => bucket.Max));
        Assert.Equal(50_000, timeline.Buckets.Sum(bucket => bucket.Count));
    }
}
