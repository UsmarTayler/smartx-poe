using SmartX.Api.History;

namespace SmartX.Tests;

/// <summary>
/// The history key is the thing the "poorly designed keys" failure is about, so it is tested on its own.
/// </summary>
public class HistoryKeyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Keys_order_by_timestamp_first()
    {
        var earlier = new HistoryKey(T0, 99);
        var later = new HistoryKey(T0.AddTicks(1), 1);

        // A later timestamp wins even with a smaller sequence: time is the primary order.
        Assert.True(earlier < later);
        Assert.True(later > earlier);
    }

    [Fact]
    public void Keys_with_the_same_timestamp_order_by_sequence()
    {
        var first = new HistoryKey(T0, 1);
        var second = new HistoryKey(T0, 2);

        Assert.True(first < second);
        Assert.Equal(-1, Math.Sign(first.CompareTo(second)));
        Assert.Equal(1, Math.Sign(second.CompareTo(first)));
    }

    [Fact]
    public void Two_keys_with_the_same_timestamp_and_sequence_are_equal()
    {
        Assert.Equal(new HistoryKey(T0, 5), new HistoryKey(T0, 5));
        Assert.Equal(0, new HistoryKey(T0, 5).CompareTo(new HistoryKey(T0, 5)));
        Assert.True(new HistoryKey(T0, 5) <= new HistoryKey(T0, 5));
        Assert.True(new HistoryKey(T0, 5) >= new HistoryKey(T0, 5));
    }

    [Fact]
    public void The_same_instant_in_different_offsets_orders_as_equal_on_time()
    {
        var utc = new HistoryKey(T0, 1);
        var plusFive = new HistoryKey(T0.ToOffset(TimeSpan.FromHours(5)), 2);

        // Not "later because 17:00 is bigger than 12:00": they are the same moment, so the sequence decides.
        Assert.True(utc < plusFive);
    }

    [Fact]
    public void The_key_is_comparable_through_the_generic_interface_so_a_sorted_dictionary_never_boxes_it()
    {
        Assert.IsAssignableFrom<IComparable<HistoryKey>>(new HistoryKey(T0, 1));
        Assert.True(typeof(HistoryKey).IsValueType);
    }

    [Fact]
    public void A_sorted_dictionary_keyed_on_the_timestamp_alone_cannot_hold_two_readings_at_one_instant()
    {
        // The failure the pair key exists to prevent, shown rather than described.
        var naive = new SortedDictionary<DateTimeOffset, double> { [T0] = 1 };
        Assert.Throws<ArgumentException>(() => naive.Add(T0, 2));

        // And the careless fix — the indexer — loses the first reading without a sound.
        naive[T0] = 2;
        Assert.Equal(2, Assert.Single(naive).Value);

        // The pair key holds both.
        var safe = new SortedDictionary<HistoryKey, double>
        {
            [new HistoryKey(T0, 1)] = 1,
            [new HistoryKey(T0, 2)] = 2,
        };
        Assert.Equal([1d, 2d], safe.Values);
    }

    [Fact]
    public void The_arrival_sequence_never_repeats_even_across_threads()
    {
        var sequence = new ArrivalSequence();
        var numbers = new System.Collections.Concurrent.ConcurrentBag<long>();

        Parallel.For(0, 10_000, _ => numbers.Add(sequence.Next()));

        Assert.Equal(10_000, numbers.Distinct().Count());
        Assert.Equal(1, numbers.Min());
    }
}
