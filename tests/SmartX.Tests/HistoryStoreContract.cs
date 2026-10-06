using SmartX.Api.History;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// The behaviour every <see cref="IHistoryStore"/> must have, independent of how it stores anything.
/// </summary>
/// <remarks>
/// <b>Why this is abstract.</b> The sorted-dictionary store is the implementation today, and Part 3 replaces
/// it with a balanced tree. A suite written against the concrete class would have to be rewritten for the tree,
/// and a rewritten suite proves nothing about whether the two behave alike. This one is written against the
/// interface and supplied an implementation by a subclass, so the tree is proven a safe replacement by adding
/// one subclass and running the identical tests.
/// <para>
/// <c>private protected</c> because the interface is internal: a public member cannot expose it, and the
/// subclasses are in this same assembly.
/// </para>
/// </remarks>
public abstract class HistoryStoreContract
{
    private const string NodeA = "AA:BB:CC:00:00:01";
    private const string NodeB = "AA:BB:CC:00:00:02";

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private long _sequence;

    /// <summary>The implementation under test, empty.</summary>
    private protected abstract IHistoryStore Create();

    private HistoryEntry Reading(string mac, DateTimeOffset at, double value = 1, long? sequence = null) => new()
    {
        Sequence = sequence ?? ++_sequence,
        Timestamp = at,
        MacAddress = mac,
        Kind = HistoryKind.Reading,
        Value = value,
        Unit = "C",
    };

    private HistoryEntry Event(string mac, DateTimeOffset at, HistoryKind kind = HistoryKind.Command) =>
        HistoryEntry.FromEvent(++_sequence, at, mac, kind, new HistoryEventDetails($"{kind} on {mac}"));

    private static IEnumerable<double> Values(IReadOnlyList<HistoryEntry> entries) => entries.Select(entry => entry.Value);

    [Fact]
    public void Two_entries_with_an_identical_timestamp_are_both_kept_in_arrival_order()
    {
        var store = Create();

        // Same node, same instant. A store keyed on the timestamp alone would throw on the second or overwrite
        // the first. Written in the order 10, 20, 30 and read back in the order 10, 20, 30.
        Assert.True(store.Append(Reading(NodeA, T0, value: 10)));
        Assert.True(store.Append(Reading(NodeA, T0, value: 20)));
        Assert.True(store.Append(Reading(NodeA, T0, value: 30)));

        var all = store.GetRange(NodeA, T0, T0.AddSeconds(1));

        Assert.Equal([10d, 20d, 30d], Values(all));
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void Entries_come_back_in_time_order_even_when_they_were_appended_out_of_order()
    {
        var store = Create();

        // A device with a slow clock, or a reading that sat in a retry buffer, arrives after a newer one.
        store.Append(Reading(NodeA, T0.AddSeconds(30), value: 3));
        store.Append(Reading(NodeA, T0.AddSeconds(10), value: 1));
        store.Append(Reading(NodeA, T0.AddSeconds(20), value: 2));

        Assert.Equal([1d, 2d, 3d], Values(store.GetRange(NodeA, T0, T0.AddMinutes(1))));
    }

    [Fact]
    public void An_entry_that_repeats_a_timestamp_and_sequence_is_refused_and_does_not_replace_the_original()
    {
        var store = Create();

        Assert.True(store.Append(Reading(NodeA, T0, value: 1, sequence: 500)));
        Assert.False(store.Append(Reading(NodeA, T0, value: 2, sequence: 500)));

        var kept = Assert.Single(store.GetRange(NodeA, T0, T0.AddSeconds(1)));
        Assert.Equal(1d, kept.Value);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void The_same_sequence_at_a_different_time_is_a_different_entry()
    {
        var store = Create();

        // The key is the pair, so reusing a number only collides when the timestamp matches too.
        Assert.True(store.Append(Reading(NodeA, T0, sequence: 7)));
        Assert.True(store.Append(Reading(NodeA, T0.AddSeconds(1), sequence: 7)));
    }

    [Fact]
    public void Each_nodes_history_is_separate()
    {
        var store = Create();

        store.Append(Reading(NodeA, T0, value: 1));
        store.Append(Reading(NodeB, T0, value: 2));

        Assert.Equal([1d], Values(store.GetRange(NodeA, T0, T0.AddSeconds(1))));
        Assert.Equal([2d], Values(store.GetRange(NodeB, T0, T0.AddSeconds(1))));
    }

    [Fact]
    public void A_range_includes_its_start_and_excludes_its_end()
    {
        var store = Create();

        for (var second = 0; second < 5; second++)
        {
            store.Append(Reading(NodeA, T0.AddSeconds(second), value: second));
        }

        // [T0+1s, T0+3s) holds the readings at 1s and 2s: the one on the start is in, the one on the end is out.
        Assert.Equal([1d, 2d], Values(store.GetRange(NodeA, T0.AddSeconds(1), T0.AddSeconds(3))));
    }

    [Fact]
    public void Consecutive_ranges_tile_without_overlap_or_gap()
    {
        var store = Create();

        for (var second = 0; second < 10; second++)
        {
            store.Append(Reading(NodeA, T0.AddSeconds(second), value: second));
        }

        var first = Values(store.GetRange(NodeA, T0, T0.AddSeconds(5)));
        var second2 = Values(store.GetRange(NodeA, T0.AddSeconds(5), T0.AddSeconds(10)));

        // The reading exactly on the boundary belongs to the later range only.
        Assert.Equal([0d, 1d, 2d, 3d, 4d], first);
        Assert.Equal([5d, 6d, 7d, 8d, 9d], second2);
    }

    [Fact]
    public void A_range_that_is_empty_or_backwards_or_for_an_unknown_node_returns_nothing()
    {
        var store = Create();
        store.Append(Reading(NodeA, T0));

        Assert.Empty(store.GetRange(NodeA, T0, T0));
        Assert.Empty(store.GetRange(NodeA, T0.AddSeconds(5), T0));
        Assert.Empty(store.GetRange(NodeA, T0.AddHours(1), T0.AddHours(2)));
        Assert.Empty(store.GetRange("AA:BB:CC:00:00:99", T0, T0.AddHours(1)));
    }

    [Fact]
    public void A_range_compares_instants_not_offsets()
    {
        var store = Create();
        store.Append(Reading(NodeA, T0, value: 1));

        // The same instant written in another offset must find the same entry.
        var inPlusTwo = T0.ToOffset(TimeSpan.FromHours(2));

        Assert.Equal([1d], Values(store.GetRange(NodeA, inPlusTwo, inPlusTwo.AddSeconds(1))));
        Assert.Empty(store.GetRange(NodeA, inPlusTwo.AddSeconds(1), inPlusTwo.AddSeconds(2)));
    }

    [Fact]
    public void Latest_returns_the_most_recent_entries_oldest_first()
    {
        var store = Create();

        for (var second = 0; second < 10; second++)
        {
            store.Append(Reading(NodeA, T0.AddSeconds(second), value: second));
        }

        Assert.Equal([7d, 8d, 9d], Values(store.GetLatest(NodeA, 3)));
    }

    [Fact]
    public void Latest_returns_everything_when_asked_for_more_than_exists()
    {
        var store = Create();
        store.Append(Reading(NodeA, T0, value: 1));
        store.Append(Reading(NodeA, T0.AddSeconds(1), value: 2));

        Assert.Equal([1d, 2d], Values(store.GetLatest(NodeA, 50)));
    }

    [Fact]
    public void Latest_of_nothing_is_empty()
    {
        var store = Create();
        store.Append(Reading(NodeA, T0));

        Assert.Empty(store.GetLatest(NodeA, 0));
        Assert.Empty(store.GetLatest(NodeA, -1));
        Assert.Empty(store.GetLatest("AA:BB:CC:00:00:99", 5));
    }

    [Fact]
    public void Latest_orders_by_time_and_not_by_arrival()
    {
        var store = Create();

        store.Append(Reading(NodeA, T0.AddSeconds(30), value: 3));
        store.Append(Reading(NodeA, T0.AddSeconds(10), value: 1));
        store.Append(Reading(NodeA, T0.AddSeconds(20), value: 2));

        // The newest by time is the one that arrived first.
        Assert.Equal([2d, 3d], Values(store.GetLatest(NodeA, 2)));
    }

    [Fact]
    public void Events_exclude_readings_span_every_node_and_come_back_in_time_order()
    {
        var store = Create();

        store.Append(Reading(NodeA, T0.AddSeconds(1)));
        store.Append(Event(NodeB, T0.AddSeconds(3), HistoryKind.AlertRaised));
        store.Append(Event(NodeA, T0.AddSeconds(2), HistoryKind.Command));
        store.Append(Reading(NodeB, T0.AddSeconds(4)));

        var events = store.GetEvents(T0, T0.AddMinutes(1));

        Assert.Equal([HistoryKind.Command, HistoryKind.AlertRaised], events.Select(entry => entry.Kind));
        Assert.Equal([NodeA, NodeB], events.Select(entry => entry.MacAddress));
    }

    [Fact]
    public void Events_follow_the_same_range_boundaries()
    {
        var store = Create();

        store.Append(Event(NodeA, T0, HistoryKind.Command));
        store.Append(Event(NodeA, T0.AddSeconds(5), HistoryKind.Command));

        Assert.Single(store.GetEvents(T0, T0.AddSeconds(5)));
        Assert.Single(store.GetEvents(T0.AddSeconds(5), T0.AddSeconds(6)));
        Assert.Empty(store.GetEvents(T0.AddSeconds(5), T0.AddSeconds(5)));
    }

    [Fact]
    public void Events_with_the_same_timestamp_keep_arrival_order()
    {
        var store = Create();

        var first = Event(NodeA, T0, HistoryKind.AlertRaised);
        var second = Event(NodeB, T0, HistoryKind.TrustTransition);
        store.Append(first);
        store.Append(second);

        Assert.Equal([first.Sequence, second.Sequence], store.GetEvents(T0, T0.AddSeconds(1)).Select(entry => entry.Sequence));
    }

    [Fact]
    public void A_node_scope_and_the_mesh_scope_are_both_queryable()
    {
        var store = Create();

        // A search belongs to the mesh and not to a node, but it is held and queried like any other entry.
        store.Append(Event(HistoryEntry.MeshScope, T0, HistoryKind.Search));

        Assert.Single(store.GetEvents(T0, T0.AddSeconds(1)));
        Assert.Single(store.GetRange(HistoryEntry.MeshScope, T0, T0.AddSeconds(1)));
    }

    [Fact]
    public void Eviction_removes_the_oldest_entries_and_nothing_newer()
    {
        var store = Create();

        for (var minute = 0; minute < 10; minute++)
        {
            store.Append(Reading(NodeA, T0.AddMinutes(minute), value: minute));
        }

        var removed = store.EvictOlderThan(T0.AddMinutes(4));

        Assert.Equal(4, removed);
        Assert.Equal(6, store.Count);
        Assert.Equal([4d, 5d, 6d, 7d, 8d, 9d], Values(store.GetRange(NodeA, T0, T0.AddHours(1))));
    }

    [Fact]
    public void Eviction_keeps_an_entry_exactly_at_the_cutoff()
    {
        var store = Create();

        store.Append(Reading(NodeA, T0, value: 1));
        store.Append(Reading(NodeA, T0.AddMinutes(1), value: 2));

        // Older than the cutoff means strictly older. An entry on it is still inside the window.
        Assert.Equal(1, store.EvictOlderThan(T0.AddMinutes(1)));
        Assert.Equal([2d], Values(store.GetLatest(NodeA, 10)));
    }

    [Fact]
    public void Eviction_covers_every_node_and_the_event_stream()
    {
        var store = Create();

        store.Append(Reading(NodeA, T0));
        store.Append(Reading(NodeB, T0));
        store.Append(Event(NodeA, T0.AddSeconds(1), HistoryKind.Command));
        store.Append(Event(NodeB, T0.AddHours(2), HistoryKind.Command));

        Assert.Equal(3, store.EvictOlderThan(T0.AddHours(1)));

        // The old event is gone from the stream as well as from its node, and the new one is untouched.
        var events = store.GetEvents(T0, T0.AddDays(1));
        Assert.Equal([NodeB], events.Select(entry => entry.MacAddress));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Eviction_with_nothing_old_enough_removes_nothing()
    {
        var store = Create();
        store.Append(Reading(NodeA, T0));

        Assert.Equal(0, store.EvictOlderThan(T0.AddDays(-1)));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void An_evicted_node_can_be_written_to_again()
    {
        var store = Create();

        store.Append(Reading(NodeA, T0));
        store.EvictOlderThan(T0.AddHours(1));
        Assert.True(store.Append(Reading(NodeA, T0.AddHours(2), value: 9)));

        Assert.Equal([9d], Values(store.GetLatest(NodeA, 5)));
    }

    [Fact]
    public async Task Concurrent_appends_from_many_threads_are_all_kept_and_sorted()
    {
        var store = Create();
        var writers = Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                // Every writer uses the same few timestamps on purpose, so the keys differ only by sequence.
                var at = T0.AddSeconds(i % 20);
                store.Append(Reading(NodeA, at, value: writer, sequence: (writer * 1000L) + i + 1));
            }
        }));

        await Task.WhenAll(writers);

        var all = store.GetRange(NodeA, T0, T0.AddMinutes(1));

        Assert.Equal(8 * 500, all.Count);
        Assert.Equal(all.OrderBy(entry => entry.Timestamp).ThenBy(entry => entry.Sequence).Select(entry => entry.Sequence), all.Select(entry => entry.Sequence));
    }
}

/// <summary>Runs the whole contract against the sorted-dictionary implementation.</summary>
public sealed class SortedDictionaryHistoryStoreTests : HistoryStoreContract
{
    private protected override IHistoryStore Create() => new SortedDictionaryHistoryStore();
}
