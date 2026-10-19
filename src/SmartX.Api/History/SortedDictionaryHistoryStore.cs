namespace SmartX.Api.History;

/// <summary>
/// Holds history in memory: a dictionary of nodes, each owning a sorted dictionary of its entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two levels.</b> The outer <see cref="Dictionary{TKey, TValue}"/> is keyed by MAC, so every append and query finds "this
/// node" in O(1) whatever the mesh size. The inner <see cref="SortedDictionary{TKey, TValue}"/> is keyed by
/// <see cref="HistoryKey"/> and is read only in time order, so a timeline is a walk and never a sort, retention removes from the
/// front, and the red-black tree keeps inserts O(log n) even when a device stamps a reading out of order.
/// </para>
/// <para>
/// <b>A third structure for events.</b> Commands, alerts, trust transitions and searches also sit in one mesh-wide sorted
/// dictionary, so "what happened in the last hour" reads a small structure instead of walking every node's readings. Entries are
/// held by reference in both places, so an event costs one extra tree node, not a copy.
/// </para>
/// <para>
/// <b>Collisions.</b> Two entries can share a timestamp, so the arrival sequence in <see cref="HistoryKey"/> makes every key
/// unique, and <see cref="Append"/> uses <c>TryAdd</c> so nothing can be overwritten.
/// </para>
/// <para>
/// <b>Cost of a range query, stated honestly.</b> <see cref="SortedDictionary{TKey, TValue}"/> cannot seek to "the first entry at
/// or after 14:00". <see cref="GetRange"/> enumerates from the oldest entry, skips to <c>from</c>, collects until <c>to</c> and
/// stops: O(n) to reach the range plus O(k) for the k results, not the O(log n + k) of a balanced tree. A query for an old window
/// is cheap and one for the newest walks everything. This is the stated reason Part 3 builds its own tree.
/// <see cref="GetLatest"/> has the same limit: it walks forward and keeps the last <c>count</c> in a small queue.
/// </para>
/// <para>
/// <b>One lock.</b> Writers are the ingestion worker and a few services at a few per second, and every operation is short, so a
/// single <see cref="Lock"/> is simpler than reader-writer locking and does not contend. Readers copy results out under the lock.
/// </para>
/// </remarks>
internal sealed class SortedDictionaryHistoryStore : IHistoryStore
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, SortedDictionary<HistoryKey, HistoryEntry>> _nodes = new(StringComparer.Ordinal);
    private readonly SortedDictionary<HistoryKey, HistoryEntry> _events = [];

    private int _count;

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <inheritdoc />
    public bool Append(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var key = entry.Key;

        lock (_gate)
        {
            if (!_nodes.TryGetValue(entry.MacAddress, out var history))
            {
                history = [];
                _nodes.Add(entry.MacAddress, history);
            }

            // TryAdd and not the indexer. The indexer would replace an entry with the same key without
            // saying so, which is exactly the silent loss the pair key exists to prevent. With a unique
            // sequence this never fails; if it ever does, the caller reused a number and is told.
            if (!history.TryAdd(key, entry))
            {
                return false;
            }

            if (entry.IsEvent)
            {
                _events.Add(key, entry);
            }

            _count++;

            return true;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// O(n) to reach <paramref name="from"/>, then O(k), then stops at the first entry at or past
    /// <paramref name="to"/>. See the class remarks for why it cannot be better with this structure.
    /// </remarks>
    public IReadOnlyList<HistoryEntry> GetRange(string macAddress, DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to)
        {
            return [];
        }

        lock (_gate)
        {
            if (!_nodes.TryGetValue(macAddress, out var history))
            {
                return [];
            }

            return Collect(history, from, to);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HistoryEntry> GetLatest(string macAddress, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            if (!_nodes.TryGetValue(macAddress, out var history))
            {
                return [];
            }

            // A queue the size of the answer, not a list the size of the history: the tree can only be walked
            // oldest first, so each entry is added and the oldest dropped once the queue is full.
            var latest = new Queue<HistoryEntry>(Math.Min(count, history.Count));

            foreach (var (_, entry) in history)
            {
                if (latest.Count == count)
                {
                    latest.Dequeue();
                }

                latest.Enqueue(entry);
            }

            return [.. latest];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HistoryEntry> GetEvents(DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to)
        {
            return [];
        }

        lock (_gate)
        {
            return Collect(_events, from, to);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Walks each node from its oldest entry and stops at the first one that is new enough, so the cost is
    /// the number removed plus one per node — never a pass over everything that is kept. This is where a sorted
    /// structure pays: the entries to remove are exactly the front of each tree.
    /// </remarks>
    public int EvictOlderThan(DateTimeOffset cutoff)
    {
        var removed = 0;
        var expired = new List<HistoryKey>();

        lock (_gate)
        {
            foreach (var history in _nodes.Values)
            {
                expired.Clear();

                foreach (var (key, entry) in history)
                {
                    if (key.Timestamp >= cutoff)
                    {
                        break;
                    }

                    expired.Add(key);

                    if (entry.IsEvent)
                    {
                        _events.Remove(key);
                    }
                }

                // Removed after the walk, not during: changing a tree while enumerating it is not allowed.
                foreach (var key in expired)
                {
                    history.Remove(key);
                }

                removed += expired.Count;
            }

            _count -= removed;
        }

        return removed;
    }

    /// <summary>The entries of one tree in <c>[from, to)</c>, found by walking from the oldest and stopping early.</summary>
    private static List<HistoryEntry> Collect(
        SortedDictionary<HistoryKey, HistoryEntry> history,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var found = new List<HistoryEntry>();

        foreach (var (key, entry) in history)
        {
            if (key.Timestamp >= to)
            {
                // Everything after this is later still. This early exit is the only thing keeping a query for
                // an old window from paying for the whole history.
                break;
            }

            if (key.Timestamp >= from)
            {
                found.Add(entry);
            }
        }

        return found;
    }
}
