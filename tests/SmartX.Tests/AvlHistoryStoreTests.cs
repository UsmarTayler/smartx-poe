using SmartX.Api.History;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Runs the whole <see cref="HistoryStoreContract"/> against the AVL store, unchanged. If this passes, the AVL store behaves
/// as the sorted-dictionary store does, which is what makes it a safe replacement.
/// </summary>
public sealed class AvlHistoryStoreContractTests : HistoryStoreContract
{
    private protected override IHistoryStore Create() => new AvlHistoryStore();
}

/// <summary>What is particular to the AVL store: that it seeks, and that it can say how tall its trees are.</summary>
public class AvlHistoryStoreTests
{
    private const string Mac = "AA:BB:CC:00:00:01";

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static HistoryEntry Reading(long sequence, DateTimeOffset at) => new()
    {
        Sequence = sequence,
        Timestamp = at,
        MacAddress = Mac,
        Kind = HistoryKind.Reading,
        Value = sequence,
    };

    [Fact]
    public void A_window_in_the_middle_of_a_large_history_returns_exactly_its_readings()
    {
        var store = new AvlHistoryStore();

        for (var i = 0; i < 5000; i++)
        {
            store.Append(Reading(i + 1, T0.AddSeconds(i)));
        }

        var window = store.GetRange(Mac, T0.AddSeconds(2000), T0.AddSeconds(2060));

        Assert.Equal(60, window.Count);
        Assert.Equal(2001, window[0].Sequence);
        Assert.Equal(2060, window[^1].Sequence);
    }

    [Fact]
    public void A_window_that_starts_on_a_shared_timestamp_includes_every_entry_stamped_then()
    {
        var store = new AvlHistoryStore();

        // The seek key uses the smallest sequence, so the first of several readings at the same instant is not skipped.
        store.Append(Reading(1, T0));
        store.Append(Reading(2, T0));
        store.Append(Reading(3, T0));

        Assert.Equal([1L, 2, 3], store.GetRange(Mac, T0, T0.AddSeconds(1)).Select(entry => entry.Sequence));
    }

    [Fact]
    public void Eviction_inside_a_big_history_keeps_every_tree_balanced()
    {
        var store = new AvlHistoryStore();

        for (var i = 0; i < 4000; i++)
        {
            store.Append(Reading(i + 1, T0.AddSeconds(i)));
        }

        Assert.Equal(1000, store.EvictOlderThan(T0.AddSeconds(1000)));

        var tree = store.GetStats().Largest.Single(tree => tree.Scope == Mac);

        Assert.Equal(3000, tree.Entries);
        Assert.InRange(tree.Height!.Value, tree.MinimumHeight, tree.MinimumHeight + 3);
    }

    [Fact]
    public void Stats_report_the_height_of_each_tree_next_to_the_best_height_possible()
    {
        var store = new AvlHistoryStore();

        // In ascending order, the arrival pattern that would turn an unbalanced tree into a list.
        for (var i = 0; i < 10_000; i++)
        {
            store.Append(Reading(i + 1, T0.AddSeconds(i)));
        }

        var stats = store.GetStats();
        var tree = stats.Largest.Single(tree => tree.Scope == Mac);

        Assert.Equal("Avl", stats.Store);
        Assert.Equal(10_000, stats.Entries);
        Assert.Equal(14, tree.MinimumHeight);   // ceil(log2(10,001))
        Assert.InRange(tree.Height!.Value, 14, 15);
    }

    [Fact]
    public void The_sorted_dictionary_store_reports_no_height_because_it_cannot_see_one()
    {
        var store = new SortedDictionaryHistoryStore();
        store.Append(Reading(1, T0));

        var tree = store.GetStats().Largest.Single(tree => tree.Scope == Mac);

        Assert.Equal("SortedDictionary", store.GetStats().Store);
        Assert.Null(tree.Height);
        Assert.Equal(1, tree.MinimumHeight);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(7, 3)]
    [InlineData(8, 4)]
    [InlineData(1_000_000, 20)]
    public void The_best_possible_height_is_the_ceiling_of_log2_of_n_plus_one(int count, int expected) =>
        Assert.Equal(expected, AvlTree<int, int>.MinimumHeightFor(count));
}
