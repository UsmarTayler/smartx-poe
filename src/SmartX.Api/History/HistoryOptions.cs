namespace SmartX.Api.History;

/// <summary>
/// Configuration for history: how long it is kept, and how the timeline endpoints bound what they return.
/// </summary>
public sealed class HistoryOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "History";

    /// <summary>How long an entry is kept before retention removes it.</summary>
    /// <remarks>
    /// A day: long enough to answer "what happened overnight" on a morning check, short enough that the
    /// in-memory store stays small. At the demo's rate that is a little over a hundred thousand readings.
    /// </remarks>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often retention runs.</summary>
    /// <remarks>
    /// On a timer and not on every append. Eviction touches every node's tree, and doing it per reading would
    /// put that cost on the ingestion path for no benefit: nothing is harmed by an entry living a minute past
    /// its window.
    /// </remarks>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How far back a timeline looks when the caller gives no <c>from</c>.</summary>
    public TimeSpan DefaultTimelineWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many buckets a timeline has when the caller gives no <c>maxPoints</c>.</summary>
    public int DefaultMaxPoints { get; set; } = 200;

    /// <summary>The most buckets a caller may ask a timeline for.</summary>
    /// <remarks>
    /// A chart is a few hundred pixels wide. Past this a request is asking for the raw data back under another
    /// name, which is what downsampling exists to avoid.
    /// </remarks>
    public int MaxPointsLimit { get; set; } = 2000;

    /// <summary>How many events the mesh-wide stream returns when the caller gives no <c>limit</c>.</summary>
    public int DefaultEventLimit { get; set; } = 500;

    /// <summary>The most events a caller may ask the stream for.</summary>
    public int MaxEventLimit { get; set; } = 5000;
}
