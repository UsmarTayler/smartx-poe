namespace SmartX.Api.History;

/// <summary>
/// The sort key of a history entry: when it happened, then the order it arrived in.
/// </summary>
/// <param name="Timestamp">When the entry happened. Compared as an instant, so offsets do not matter.</param>
/// <param name="Sequence">
/// The arrival sequence number. Unique across the process, so no two entries ever share a whole key.
/// </param>
/// <remarks>
/// <b>Why the key is a pair.</b> A <c>SortedDictionary&lt;DateTimeOffset, ...&gt;</c> would be wrong the first
/// time two readings share a timestamp, and they will: a device that stamps to the second sends a batch, or two
/// events are caused by one reading. The dictionary would either throw on the second
/// <c>Add</c>, or — written carelessly with the indexer — silently overwrite the first, losing a reading
/// without a sound. Both are the same design fault: the key was not unique.
/// <para>
/// Pairing the timestamp with the arrival sequence makes it unique, because the sequence is never reused, and
/// leaves the order meaningful: by time first, so a timeline is in time order, and by arrival second, so two
/// entries with the same timestamp come back in the order they were accepted.
/// </para>
/// <para>
/// A <c>readonly record struct</c> so building a key to look something up allocates nothing. It implements
/// <see cref="IComparable{T}"/> so <see cref="SortedDictionary{TKey, TValue}"/> compares it through the
/// generic interface instead of boxing it into <see cref="IComparable"/>, and so the ordering is written down
/// here instead of being whatever the default struct comparison happens to do.
/// </para>
/// </remarks>
internal readonly record struct HistoryKey(DateTimeOffset Timestamp, long Sequence) : IComparable<HistoryKey>
{
    /// <summary>Orders by timestamp, then by sequence.</summary>
    public int CompareTo(HistoryKey other)
    {
        // UtcTicks, not the DateTimeOffset itself: it compares the instant and ignores the offset, which is
        // what a timeline means by "earlier". Two readings of the same moment stamped in different time zones
        // are the same time.
        var byTime = Timestamp.UtcTicks.CompareTo(other.Timestamp.UtcTicks);

        return byTime != 0 ? byTime : Sequence.CompareTo(other.Sequence);
    }

    public static bool operator <(HistoryKey left, HistoryKey right) => left.CompareTo(right) < 0;

    public static bool operator >(HistoryKey left, HistoryKey right) => left.CompareTo(right) > 0;

    public static bool operator <=(HistoryKey left, HistoryKey right) => left.CompareTo(right) <= 0;

    public static bool operator >=(HistoryKey left, HistoryKey right) => left.CompareTo(right) >= 0;
}
