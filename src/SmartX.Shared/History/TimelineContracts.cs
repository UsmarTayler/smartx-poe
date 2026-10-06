using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// One slice of a downsampled timeline.
/// </summary>
/// <param name="Start">The start of the slice, inclusive.</param>
/// <param name="End">The end of the slice, exclusive.</param>
/// <param name="Min">The smallest reading in the slice.</param>
/// <param name="Max">The largest reading in the slice.</param>
/// <param name="Average">The mean of the readings in the slice.</param>
/// <param name="Count">How many readings the slice summarises.</param>
/// <remarks>
/// Min and max travel with the average because an average alone hides the one thing a dashboard exists to
/// show. One reading of 900 among fifty-nine of 20 averages to about 34, which looks like a warm afternoon. The
/// slice's max says 900.
/// </remarks>
public sealed record TimelineBucket(
    DateTimeOffset Start,
    DateTimeOffset End,
    double Min,
    double Max,
    double Average,
    int Count);

/// <summary>A state sensor changing state.</summary>
/// <param name="Timestamp">When the new state was first seen.</param>
/// <param name="State">True for engaged, false for released.</param>
public sealed record StateTransition(DateTimeOffset Timestamp, bool State);

/// <summary>Something that happened to the node in the range, for the chart to draw on its timeline.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Kind">A command, an alert raised or cleared, or a trust transition.</param>
/// <param name="Label">A short phrase, such as "ValveClose issued" or "Disconnected raised".</param>
/// <param name="Detail">More, when there is more.</param>
public sealed record TimelineMarker(DateTimeOffset Timestamp, HistoryKind Kind, string Label, string? Detail);

/// <summary>What a timeline holds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TimelineShape>))]
public enum TimelineShape
{
    /// <summary>A numeric sensor, downsampled into buckets.</summary>
    Buckets,

    /// <summary>A state sensor, reduced to the moments it changed.</summary>
    Transitions
}

/// <summary>One node's readings over a range, ready to chart.</summary>
/// <param name="MacAddress">The node.</param>
/// <param name="Unit">The unit its readings are in.</param>
/// <param name="Shape">Whether <paramref name="Buckets"/> or <paramref name="Transitions"/> carries the data.</param>
/// <param name="From">The start of the range, inclusive.</param>
/// <param name="To">The end of the range, exclusive.</param>
/// <param name="BucketSeconds">The width of each bucket, or zero for a state sensor.</param>
/// <param name="ReadingCount">How many raw readings fell in the range, before downsampling.</param>
/// <param name="Buckets">Non-empty buckets in time order. A gap in time is a gap in the list, not a zero.</param>
/// <param name="Transitions">State changes in time order, starting with the state at the first reading.</param>
/// <param name="Markers">Commands, alerts and trust transitions in the range, in time order.</param>
public sealed record SensorTimeline(
    string MacAddress,
    string Unit,
    TimelineShape Shape,
    DateTimeOffset From,
    DateTimeOffset To,
    double BucketSeconds,
    int ReadingCount,
    IReadOnlyList<TimelineBucket> Buckets,
    IReadOnlyList<StateTransition> Transitions,
    IReadOnlyList<TimelineMarker> Markers);

/// <summary>One line of the mesh-wide event stream.</summary>
/// <param name="Sequence">The arrival number, which orders events that share a timestamp.</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="MacAddress">The node it happened to.</param>
/// <param name="Kind">What sort of event.</param>
/// <param name="Summary">A short phrase for a list.</param>
/// <param name="Detail">More, when there is more.</param>
public sealed record TimelineEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string MacAddress,
    HistoryKind Kind,
    string Summary,
    string? Detail);

/// <summary>The mesh-wide event stream over a range.</summary>
/// <param name="From">The start of the range, inclusive.</param>
/// <param name="To">The end of the range, exclusive.</param>
/// <param name="Events">Events in time order.</param>
/// <param name="Truncated">
/// True when more events fell in the range than the limit allowed, in which case these are the most recent.
/// </param>
public sealed record TimelineEvents(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TimelineEvent> Events,
    bool Truncated);
