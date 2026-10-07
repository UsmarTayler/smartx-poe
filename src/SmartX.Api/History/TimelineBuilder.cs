using SmartX.Shared;

namespace SmartX.Api.History;

/// <summary>
/// Turns one node's history over a range into something a chart can draw: a bounded number of buckets for a
/// numeric sensor, the state changes for a state sensor, and markers for what happened along the way.
/// </summary>
/// <remarks>
/// <b>Why downsample at all.</b> A day of readings from one sensor is tens of thousands of points, and a chart
/// is a few hundred pixels wide. Sending every point makes the response large and the chart slow, to draw detail
/// no screen can show. The brief's reason for the sorted history is "so the dashboard can render timeline graphs
/// efficiently"; this is where that is cashed in.
/// <para>
/// <b>Why every bucket carries min and max.</b> Averaging is the obvious way to downsample and the wrong one for
/// a monitoring dashboard. One reading of 900 among fifty-nine of 20 averages to about 34, a figure nobody
/// would look at twice, and the spike is gone. The dashboard exists to show the spike. Min and max cost two
/// numbers per bucket and keep every extreme visible however far the data is squeezed; the average is kept for
/// the shape of the line.
/// </para>
/// <para>
/// <b>One pass, no sort.</b> The entries arrive in time order from the store, so a reading's bucket is never
/// earlier than the previous reading's. That means one running bucket is enough: when a reading falls in a later
/// bucket the current one is finished and a new one starts. There is no array of buckets to allocate up front and
/// no dictionary to look them up in. The number of buckets can never exceed the limit, because the bucket width
/// is the range divided by the limit, rounded up.
/// </para>
/// </remarks>
internal static class TimelineBuilder
{
    /// <summary>Builds the timeline for one sensor from the entries the store returned for the range.</summary>
    /// <param name="sensor">The sensor, which decides whether this is a numeric or a state timeline.</param>
    /// <param name="entries">The node's entries in <c>[from, to)</c>, oldest first, of every kind.</param>
    /// <param name="from">The start of the range, inclusive.</param>
    /// <param name="to">The end of the range, exclusive.</param>
    /// <param name="maxPoints">The most buckets to return. At least one.</param>
    public static SensorTimeline Build(
        SensorRegistration sensor,
        IReadOnlyList<HistoryEntry> entries,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);

        var isState = sensor.PayloadKind == TelemetryPayloadKind.State;
        var span = (to - from).Ticks;

        // Rounded up so that span / width never exceeds maxPoints. Rounding down would allow one bucket too many.
        var width = isState ? 0 : Math.Max(1, (span + maxPoints - 1) / maxPoints);

        var buckets = new List<TimelineBucket>();
        var transitions = new List<StateTransition>();
        var markers = new List<TimelineMarker>();
        var readings = 0;

        var open = default(OpenBucket);
        bool? previousState = null;

        foreach (var entry in entries)
        {
            if (entry.Kind != HistoryKind.Reading)
            {
                if (entry.Kind is not (HistoryKind.Search or HistoryKind.Selection) && entry.Event is { } details)
                {
                    markers.Add(new TimelineMarker(entry.Timestamp, entry.Kind, details.Summary, details.Detail));
                }

                continue;
            }

            readings++;

            if (isState)
            {
                // Only the moments the state changed. A valve that has been shut for an hour is one transition
                // and not three thousand identical points, and the chart draws it as a step.
                var state = entry.Value != 0;

                if (previousState != state)
                {
                    transitions.Add(new StateTransition(entry.Timestamp, state));
                    previousState = state;
                }

                continue;
            }

            var bucket = (entry.Timestamp - from).Ticks / width;

            if (open.Count > 0 && open.Index != bucket)
            {
                buckets.Add(open.Close(from, width, to));
                open = default;
            }

            open = open.Count == 0 ? OpenBucket.Start(bucket, entry.Value) : open.Add(entry.Value);
        }

        if (open.Count > 0)
        {
            buckets.Add(open.Close(from, width, to));
        }

        return new SensorTimeline(
            sensor.MacAddress,
            sensor.Unit,
            isState ? TimelineShape.Transitions : TimelineShape.Buckets,
            from,
            to,
            width / (double)TimeSpan.TicksPerSecond,
            readings,
            buckets,
            transitions,
            markers);
    }

    /// <summary>The bucket being filled. A struct so building a timeline allocates only its results.</summary>
    private readonly record struct OpenBucket(long Index, double Min, double Max, double Sum, int Count)
    {
        public static OpenBucket Start(long index, double value) => new(index, value, value, value, 1);

        public OpenBucket Add(double value) =>
            new(Index, Math.Min(Min, value), Math.Max(Max, value), Sum + value, Count + 1);

        public TimelineBucket Close(DateTimeOffset from, long width, DateTimeOffset to)
        {
            var start = from + TimeSpan.FromTicks(Index * width);
            var end = start + TimeSpan.FromTicks(width);

            // The last bucket may be wider than what is left of the range, so it is trimmed to the range.
            return new TimelineBucket(start, end > to ? to : end, Min, Max, Sum / Count, Count);
        }
    }
}
