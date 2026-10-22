using SmartX.Shared;

namespace SmartX.Api.History;

/// <summary>
/// Holds history in memory: a dictionary of nodes, each owning an <see cref="AvlTree{TKey, TValue}"/> of its entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same shape, different tree.</b> The layout, the key and the locking are those of <see cref="SortedDictionaryHistoryStore"/>:
/// a dictionary by MAC, one tree per node keyed by <see cref="HistoryKey"/> (timestamp, then arrival sequence, so equal
/// timestamps are all kept in arrival order), a mesh-wide tree of events, and one <see cref="Lock"/>. What changes is that the
/// tree can seek. <see cref="GetRange"/> descends to the first entry at or after <c>from</c> and reads forward, so it costs
/// O(log n + k) wherever the window sits, where the sorted dictionary costs O(n + k) for a recent one.
/// </para>
/// <para>
/// <b>Latest is also a seek.</b> The newest N entries are the right edge of the tree, so <see cref="GetLatest"/> reads N entries
/// backward from the maximum, O(log n + N), where the sorted dictionary walks the whole history to find them.
/// </para>
/// <para>
/// <b>Eviction</b> reads each tree from its oldest entry, collects the keys older than the cutoff and removes them. Each
/// removal is O(log n) and rebalances, so retention costs O(k log n) for k removed; the sorted dictionary's red-black tree
/// pays about the same per removal.
/// </para>
/// </remarks>
internal sealed class AvlHistoryStore : IHistoryStore
{
    /// <summary>The name <see cref="GetStats"/> reports.</summary>
    internal const string StoreName = "Avl";

    private readonly Lock _gate = new();

    private readonly Dictionary<string, AvlTree<HistoryKey, HistoryEntry>> _nodes = new(StringComparer.Ordinal);
    private readonly AvlTree<HistoryKey, HistoryEntry> _events = new();

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
                history = new AvlTree<HistoryKey, HistoryEntry>();
                _nodes.Add(entry.MacAddress, history);
            }

            // Insert refuses a key that is already there instead of replacing it, which is what the pair key exists for.
            if (!history.Insert(key, entry))
            {
                return false;
            }

            if (entry.IsEvent)
            {
                _events.Insert(key, entry);
            }

            _count++;

            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HistoryEntry> GetRange(string macAddress, DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to)
        {
            return [];
        }

        lock (_gate)
        {
            return _nodes.TryGetValue(macAddress, out var history) ? Collect(history, from, to) : [];
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

            // Newest first off the right edge, then turned around: the contract is oldest first.
            var latest = new List<HistoryEntry>(Math.Min(count, history.Count));

            foreach (var (_, entry) in history.InReverseOrder())
            {
                latest.Add(entry);

                if (latest.Count == count)
                {
                    break;
                }
            }

            latest.Reverse();

            return latest;
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
    public int EvictOlderThan(DateTimeOffset cutoff)
    {
        var removed = 0;
        var expired = new List<HistoryEntry>();

        lock (_gate)
        {
            foreach (var history in _nodes.Values)
            {
                expired.Clear();

                foreach (var (key, entry) in history.InOrder())
                {
                    if (key.Timestamp >= cutoff)
                    {
                        break;
                    }

                    expired.Add(entry);
                }

                // Removed after the walk: the walk is lazy over the tree, and a tree must not change under it.
                foreach (var entry in expired)
                {
                    history.Remove(entry.Key);

                    if (entry.IsEvent)
                    {
                        _events.Remove(entry.Key);
                    }
                }

                removed += expired.Count;
            }

            _count -= removed;
        }

        return removed;
    }

    /// <inheritdoc />
    public HistoryStoreStats GetStats(int largest = 10)
    {
        lock (_gate)
        {
            var biggest = _nodes
                .OrderByDescending(pair => pair.Value.Count)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(Math.Max(largest, 0))
                .Select(pair => Describe(pair.Key, pair.Value));

            return new HistoryStoreStats(StoreName, _count, _nodes.Count + 1, [Describe("events", _events), .. biggest]);
        }
    }

    private static HistoryTreeStats Describe(string scope, AvlTree<HistoryKey, HistoryEntry> tree) =>
        new(scope, tree.Count, tree.Height, AvlTree<HistoryKey, HistoryEntry>.MinimumHeightFor(tree.Count));

    /// <summary>The entries of one tree in <c>[from, to)</c>: seek to the start, read forward, stop at the end.</summary>
    private static List<HistoryEntry> Collect(AvlTree<HistoryKey, HistoryEntry> history, DateTimeOffset from, DateTimeOffset to)
    {
        var found = new List<HistoryEntry>();

        // long.MinValue sorts before every real sequence at the same instant, so the seek lands on the first entry stamped
        // at or after `from` and none are skipped.
        foreach (var (key, entry) in history.From(new HistoryKey(from, long.MinValue)))
        {
            if (key.Timestamp >= to)
            {
                break;
            }

            found.Add(entry);
        }

        return found;
    }
}
