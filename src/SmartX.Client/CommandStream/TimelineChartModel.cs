using System.Globalization;
using System.Text;
using MudBlazor;
using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>The part of the chart where the series is drawn, in view-box units.</summary>
public readonly record struct PlotArea(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;
}

/// <summary>A label at a position along an axis.</summary>
public sealed record ChartTick(double Position, string Label);

/// <summary>An event on the time axis: a command, an alert raised or cleared, or a change of trust band.</summary>
/// <param name="X">Where it sits along the axis.</param>
/// <param name="Kind">What sort of event.</param>
/// <param name="Shape">A shape that identifies the kind without its colour.</param>
/// <param name="Icon">An icon that identifies the kind, drawn inside the shape.</param>
/// <param name="Label">A short phrase.</param>
/// <param name="Detail">More, when there is more.</param>
/// <param name="At">When it happened.</param>
public sealed record ChartMarker(double X, HistoryKind Kind, string Shape, string Icon, string Label, string? Detail, DateTimeOffset At);

/// <summary>One bucket as drawn: where it is, what it holds, and the events that fall within it.</summary>
public sealed record ChartBucket(double X, double Width, TimelineBucket Bucket, IReadOnlyList<ChartMarker> Events);

/// <summary>Everything the chart component needs to draw, worked out and ready to place.</summary>
public sealed record ChartGeometry(
    int Width,
    int Height,
    PlotArea Plot,
    TimelineShape Shape,
    string Unit,
    IReadOnlyList<string> EnvelopeSegments,
    IReadOnlyList<string> AverageSegments,
    IReadOnlyList<ChartPoint> LonePoints,
    string StepPath,
    IReadOnlyList<ChartTick> YTicks,
    IReadOnlyList<ChartTick> XTicks,
    IReadOnlyList<ChartMarker> Markers,
    IReadOnlyList<ChartBucket> Buckets,
    double YMin,
    double YMax,
    DateTimeOffset From,
    DateTimeOffset To)
{
    /// <summary>Whether there is nothing to draw.</summary>
    public bool IsEmpty => Buckets.Count == 0 && StepPath.Length == 0;
}

/// <summary>A point in view-box units.</summary>
public readonly record struct ChartPoint(double X, double Y);

/// <summary>
/// Works out what a timeline chart draws, as numbers and strings, away from the component that draws it.
/// </summary>
/// <remarks>
/// <b>The envelope is the point.</b> Each bucket is a minimum, a maximum and an average. The minimum and maximum become a
/// filled band and the average a line on top of it, so a spike that the average hides is still the top of the band. The
/// band is drawn first and the line over it; nothing is drawn over the band that could cover the extreme.
/// <para>
/// <b>One axis only.</b> The band and the line share one y-axis. Two axes would let a series be stretched to look as
/// dramatic as the author liked.
/// </para>
/// <para>
/// <b>Gaps are gaps.</b> When there are no readings between two buckets, the line and the band are broken there and are
/// not joined across the silence. A node that stopped reporting for ten minutes must not look as if it reported a smooth
/// value for ten minutes.
/// </para>
/// <para>
/// <b>Live.</b> Readings pushed after the timeline was fetched extend it: they are put into new buckets of the same width
/// to the right of the last one, so the chart grows while the page is open without asking the server again.
/// </para>
/// </remarks>
public static class TimelineChartModel
{
    private const double Left = 46;
    private const double Top = 10;
    private const double Right = 10;
    private const double AxisSpace = 38;

    /// <summary>The icon and shape for each kind of event.</summary>
    public static (string Shape, string Icon) MarkerStyle(HistoryKind kind) => kind switch
    {
        HistoryKind.Command => ("diamond", Icons.Material.Filled.Terminal),
        HistoryKind.AlertRaised => ("triangle", Icons.Material.Filled.NotificationsActive),
        HistoryKind.AlertCleared => ("circle", Icons.Material.Filled.CheckCircle),
        HistoryKind.TrustTransition => ("square", Icons.Material.Filled.SwapVert),
        _ => ("circle", Icons.Material.Filled.Info),
    };

    /// <summary>A short name for a kind of event, for the legend and for screen readers.</summary>
    public static string KindLabel(HistoryKind kind) => kind switch
    {
        HistoryKind.Command => "Command",
        HistoryKind.AlertRaised => "Alert raised",
        HistoryKind.AlertCleared => "Alert cleared",
        HistoryKind.TrustTransition => "Trust change",
        _ => kind.ToString(),
    };

    /// <summary>Works out the chart for a timeline and the live readings pushed since it was fetched.</summary>
    /// <param name="timeline">What the server returned.</param>
    /// <param name="live">Readings pushed after the timeline was fetched, oldest first. They extend it to the right.</param>
    /// <param name="width">The chart's width in view-box units.</param>
    /// <param name="height">The chart's height in view-box units.</param>
    public static ChartGeometry Build(SensorTimeline timeline, IReadOnlyList<ReadingPush> live, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(live);

        var plot = new PlotArea(Left, Top, width - Right, height - AxisSpace);
        var newer = live.Where(reading => reading.Timestamp >= timeline.To).OrderBy(reading => reading.Timestamp).ToList();
        var end = newer.Count > 0 ? newer[^1].Timestamp : timeline.To;
        var span = Math.Max(1, (end - timeline.From).TotalMilliseconds);

        double XOf(DateTimeOffset at) => plot.Left + (at - timeline.From).TotalMilliseconds / span * plot.Width;

        var markers = timeline.Markers
            .Where(marker => marker.Timestamp >= timeline.From && marker.Timestamp <= end)
            .Select(marker =>
            {
                var (shape, icon) = MarkerStyle(marker.Kind);

                return new ChartMarker(XOf(marker.Timestamp), marker.Kind, shape, icon, marker.Label, marker.Detail, marker.Timestamp);
            })
            .ToList();

        return timeline.Shape == TimelineShape.Transitions
            ? BuildSteps(timeline, newer, end, width, height, plot, markers, XOf)
            : BuildBuckets(timeline, newer, end, width, height, plot, markers, XOf);
    }

    private static ChartGeometry BuildBuckets(
        SensorTimeline timeline,
        List<ReadingPush> newer,
        DateTimeOffset end,
        int width,
        int height,
        PlotArea plot,
        List<ChartMarker> markers,
        Func<DateTimeOffset, double> xOf)
    {
        var buckets = ExtendBuckets(timeline, newer);

        if (buckets.Count == 0)
        {
            return new ChartGeometry(width, height, plot, timeline.Shape, timeline.Unit, [], [], [], string.Empty, [], XTicks(timeline.From, end, plot, xOf), markers, [], 0, 0, timeline.From, end);
        }

        var minimum = buckets.Min(bucket => bucket.Min);
        var maximum = buckets.Max(bucket => bucket.Max);
        var (low, high) = Domain(minimum, maximum);

        double YOf(double value) => plot.Bottom - (value - low) / (high - low) * plot.Height;

        var drawn = buckets
            .Select(bucket =>
            {
                var x = xOf(bucket.Start);

                return new ChartBucket(x, Math.Max(2, xOf(bucket.End) - x), bucket, [.. markers.Where(marker => marker.At >= bucket.Start && marker.At < bucket.End)]);
            })
            .ToList();

        // Broken wherever there are no readings for a stretch, so silence is not drawn as a smooth value.
        var widest = buckets.Max(bucket => (bucket.End - bucket.Start).TotalMilliseconds);
        List<List<ChartBucket>> runs = [];

        foreach (var bucket in drawn)
        {
            if (runs.Count > 0 && (bucket.Bucket.Start - runs[^1][^1].Bucket.End).TotalMilliseconds <= widest * 2)
            {
                runs[^1].Add(bucket);
            }
            else
            {
                runs.Add([bucket]);
            }
        }

        List<string> envelopes = [];
        List<string> averages = [];
        List<ChartPoint> lone = [];

        foreach (var run in runs)
        {
            if (run.Count == 1)
            {
                var only = run[0];
                lone.Add(new ChartPoint(only.X + only.Width / 2, YOf(only.Bucket.Average)));

                // A single bucket still has an extent. Drawn as a thin band so its spike is still seen.
                envelopes.Add(Points(
                    (only.X, YOf(only.Bucket.Max)),
                    (only.X + only.Width, YOf(only.Bucket.Max)),
                    (only.X + only.Width, YOf(only.Bucket.Min)),
                    (only.X, YOf(only.Bucket.Min))));

                continue;
            }

            var upper = run.Select(bucket => (bucket.X + bucket.Width / 2, YOf(bucket.Bucket.Max)));
            var lower = run.AsEnumerable().Reverse().Select(bucket => (bucket.X + bucket.Width / 2, YOf(bucket.Bucket.Min)));

            envelopes.Add(Points([.. upper, .. lower]));
            averages.Add(Points([.. run.Select(bucket => (bucket.X + bucket.Width / 2, YOf(bucket.Bucket.Average)))]));
        }

        return new ChartGeometry(
            width, height, plot, timeline.Shape, timeline.Unit,
            envelopes, averages, lone, string.Empty,
            YTicks(low, high, plot, YOf), XTicks(timeline.From, end, plot, xOf), markers, drawn, low, high, timeline.From, end);
    }

    private static ChartGeometry BuildSteps(
        SensorTimeline timeline,
        List<ReadingPush> newer,
        DateTimeOffset end,
        int width,
        int height,
        PlotArea plot,
        List<ChartMarker> markers,
        Func<DateTimeOffset, double> xOf)
    {
        List<(DateTimeOffset At, bool State)> steps = [.. timeline.Transitions.Select(transition => (transition.Timestamp, transition.State))];

        // Live readings only matter where the state changes. The same state again is the same step.
        foreach (var reading in newer)
        {
            var state = reading.Value != 0;

            if (steps.Count == 0 || steps[^1].State != state)
            {
                steps.Add((reading.Timestamp, state));
            }
        }

        double YOf(bool state) => state ? plot.Top + plot.Height * 0.15 : plot.Bottom - plot.Height * 0.15;

        var path = new StringBuilder();

        for (var index = 0; index < steps.Count; index++)
        {
            var (at, state) = steps[index];
            var x = Number(xOf(at));

            if (index == 0)
            {
                path.Append('M').Append(x).Append(' ').Append(Number(YOf(state)));
            }
            else
            {
                // A step: along at the old level to the moment of change, then straight up or down.
                path.Append(" H").Append(x).Append(" V").Append(Number(YOf(state)));
            }
        }

        if (steps.Count > 0)
        {
            path.Append(" H").Append(Number(xOf(end)));
        }

        List<ChartTick> yTicks = [new(YOf(true), "engaged"), new(YOf(false), "released")];

        return new ChartGeometry(
            width, height, plot, timeline.Shape, timeline.Unit,
            [], [], [], path.ToString(), yTicks, XTicks(timeline.From, end, plot, xOf), markers,
            [.. steps.Select((step, index) =>
            {
                var next = index + 1 < steps.Count ? steps[index + 1].At : end;
                var x = xOf(step.At);

                // Each step is a hit target for the tooltip, spanning until the next one.
                var bucket = new TimelineBucket(step.At, next, step.State ? 1 : 0, step.State ? 1 : 0, step.State ? 1 : 0, 1);

                return new ChartBucket(x, Math.Max(2, xOf(next) - x), bucket, [.. markers.Where(marker => marker.At >= step.At && marker.At < next)]);
            })],
            0, 1, timeline.From, end);
    }

    /// <summary>The server's buckets, followed by buckets of the same width holding the readings pushed since.</summary>
    private static List<TimelineBucket> ExtendBuckets(SensorTimeline timeline, List<ReadingPush> newer)
    {
        List<TimelineBucket> buckets = [.. timeline.Buckets];

        if (newer.Count == 0)
        {
            return buckets;
        }

        var width = TimeSpan.FromSeconds(Math.Max(timeline.BucketSeconds, 1));
        SortedDictionary<long, (double Min, double Max, double Sum, int Count)> extra = [];

        foreach (var reading in newer)
        {
            var index = (long)((reading.Timestamp - timeline.To).Ticks / width.Ticks);

            extra[index] = extra.TryGetValue(index, out var open)
                ? (Math.Min(open.Min, reading.Value), Math.Max(open.Max, reading.Value), open.Sum + reading.Value, open.Count + 1)
                : (reading.Value, reading.Value, reading.Value, 1);
        }

        foreach (var (index, bucket) in extra)
        {
            var start = timeline.To + TimeSpan.FromTicks(index * width.Ticks);

            buckets.Add(new TimelineBucket(start, start + width, bucket.Min, bucket.Max, bucket.Sum / bucket.Count, bucket.Count));
        }

        return buckets;
    }

    /// <summary>The y-domain: the data's range with a margin, never zero height.</summary>
    private static (double Low, double High) Domain(double minimum, double maximum)
    {
        if (maximum - minimum < 1e-9)
        {
            var pad = Math.Max(1, Math.Abs(maximum) * 0.05);

            return (minimum - pad, maximum + pad);
        }

        var margin = (maximum - minimum) * 0.08;

        return (minimum - margin, maximum + margin);
    }

    private static List<ChartTick> YTicks(double low, double high, PlotArea plot, Func<double, double> yOf)
    {
        var step = NiceStep((high - low) / 4);
        List<ChartTick> ticks = [];

        for (var value = Math.Ceiling(low / step) * step; value <= high + step * 1e-9; value += step)
        {
            ticks.Add(new ChartTick(yOf(value), ReadingFormatter.Number(Math.Round(value, 6))));
        }

        return ticks;
    }

    private static List<ChartTick> XTicks(DateTimeOffset from, DateTimeOffset to, PlotArea plot, Func<DateTimeOffset, double> xOf)
    {
        List<ChartTick> ticks = [];
        const int count = 4;

        for (var i = 0; i <= count; i++)
        {
            var at = from + (to - from) * i / count;

            ticks.Add(new ChartTick(xOf(at), ReadingFormatter.Time(at)));
        }

        return ticks;
    }

    /// <summary>A tick step of 1, 2 or 5 times a power of ten, so the labels are round numbers.</summary>
    public static double NiceStep(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough))
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(rough));
        var fraction = rough / Math.Pow(10, exponent);
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;

        return nice * Math.Pow(10, exponent);
    }

    private static string Points(params (double X, double Y)[] points) =>
        string.Join(' ', points.Select(point => $"{Number(point.X)},{Number(point.Y)}"));

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
