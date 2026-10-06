namespace SmartX.Api.History;

/// <summary>
/// Hands out the arrival sequence number: one counter for everything the gateway records.
/// </summary>
/// <remarks>
/// The ingestion queue numbers each reading as it is accepted, and the things that happen around readings —
/// a command, an alert, a trust transition, a search — take their numbers from the same counter. One counter
/// means the sequence is a total order over everything the gateway saw, so "which came first" has an answer
/// even when two things share a timestamp, and history entries of different kinds never collide on a key.
/// <para>
/// <see cref="Interlocked"/> rather than a lock: it is called from request threads, the worker and the
/// background services at once, and a single atomic increment is all the exclusion it needs.
/// </para>
/// </remarks>
internal sealed class ArrivalSequence
{
    private long _last;

    /// <summary>The next number. Starts at 1 and never repeats.</summary>
    public long Next() => Interlocked.Increment(ref _last);
}
