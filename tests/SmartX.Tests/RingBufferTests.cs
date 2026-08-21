using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Covers the ring buffer: wrapping, enumeration order, the degenerate capacities, and the
/// allocation guarantee.
/// </summary>
public class RingBufferTests
{
    private static TelemetryRingBuffer<int> Filled(int capacity, int itemCount)
    {
        var buffer = new TelemetryRingBuffer<int>(capacity);

        for (var item = 0; item < itemCount; item++)
        {
            buffer.Add(item);
        }

        return buffer;
    }

    [Fact]
    public void A_buffer_below_capacity_holds_everything_added()
    {
        var buffer = Filled(capacity: 5, itemCount: 3);

        Assert.Equal(3, buffer.Count);
        Assert.Equal(5, buffer.Capacity);
        Assert.False(buffer.IsFull);
        Assert.Equal([0, 1, 2], buffer);
    }

    [Fact]
    public void Writing_past_capacity_overwrites_the_oldest_entry()
    {
        // Capacity 3 given five items: 0 and 1 are evicted, and the count stays at capacity.
        var buffer = Filled(capacity: 3, itemCount: 5);

        Assert.Equal(3, buffer.Count);
        Assert.True(buffer.IsFull);
        Assert.Equal([2, 3, 4], buffer);
    }

    [Fact]
    public void Enumeration_runs_oldest_to_newest_before_wrapping()
    {
        Assert.Equal([0, 1, 2], Filled(capacity: 4, itemCount: 3));
    }

    [Fact]
    public void Enumeration_runs_oldest_to_newest_after_wrapping()
    {
        // The order has to survive the head moving off index 0, which is where a naive
        // implementation reports the array in physical order instead.
        Assert.Equal([7, 8, 9], Filled(capacity: 3, itemCount: 10));
    }

    [Fact]
    public void A_partially_filled_buffer_yields_only_real_entries()
    {
        // Capacity 10 holding 3 items must not yield seven default zeros.
        var buffer = Filled(capacity: 10, itemCount: 3);

        Assert.Equal(3, buffer.Count());
        Assert.Equal([0, 1, 2], buffer);
    }

    [Fact]
    public void Indexing_addresses_items_by_age_not_by_slot()
    {
        var buffer = Filled(capacity: 3, itemCount: 5);

        Assert.Equal(2, buffer[0]);
        Assert.Equal(4, buffer[2]);
        Assert.Equal(2, buffer.Oldest);
        Assert.Equal(4, buffer.Newest);
    }

    [Fact]
    public void Indexing_outside_the_retained_items_throws()
    {
        var buffer = Filled(capacity: 5, itemCount: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
    }

    [Fact]
    public void A_capacity_of_one_keeps_only_the_newest_item()
    {
        var buffer = Filled(capacity: 1, itemCount: 4);

        Assert.Equal(1, buffer.Count);
        Assert.Equal([3], buffer);
        Assert.Equal(3, buffer.Oldest);
        Assert.Equal(3, buffer.Newest);
    }

    [Fact]
    public void A_capacity_of_zero_accepts_writes_and_retains_nothing()
    {
        // Retention disabled. Writes are accepted rather than throwing, so a caller configured this
        // way needs no special case.
        var buffer = Filled(capacity: 0, itemCount: 5);

        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.IsEmpty);
        Assert.True(buffer.IsFull);
        Assert.Empty(buffer);
        Assert.Empty(buffer.ToList());
    }

    [Fact]
    public void A_negative_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryRingBuffer<int>(-1));
    }

    [Fact]
    public void Reading_an_empty_buffer_throws_rather_than_returning_a_default()
    {
        var buffer = new TelemetryRingBuffer<int>(4);

        Assert.Throws<InvalidOperationException>(() => buffer.Oldest);
        Assert.Throws<InvalidOperationException>(() => buffer.Newest);
    }

    [Fact]
    public void A_snapshot_is_a_copy_that_later_writes_do_not_disturb()
    {
        var buffer = Filled(capacity: 3, itemCount: 3);

        var snapshot = buffer.ToList();
        buffer.Add(99);

        Assert.Equal([0, 1, 2], snapshot);
        Assert.Equal([1, 2, 99], buffer);
    }

    [Fact]
    public void A_snapshot_of_a_wrapped_buffer_is_ordered_oldest_first()
    {
        // ToList copies two runs rather than one, so wrapping is where it would go wrong.
        Assert.Equal([7, 8, 9], Filled(capacity: 3, itemCount: 10).ToList());
    }

    [Fact]
    public void A_snapshot_is_sized_to_the_item_count()
    {
        Assert.Equal(3, Filled(capacity: 3, itemCount: 10).ToList().Capacity);
    }

    [Fact]
    public void Clearing_empties_the_buffer_but_keeps_its_capacity()
    {
        var buffer = Filled(capacity: 4, itemCount: 6);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(4, buffer.Capacity);

        buffer.Add(42);
        Assert.Equal([42], buffer);
    }

    [Fact]
    public void Enumerating_the_buffer_allocates_nothing()
    {
        // The reason the enumerator is a struct and foreach binds to the concrete GetEnumerator.
        // An iterator method would allocate one state machine object per enumeration, which is
        // charged per node on every dashboard refresh.
        var buffer = Filled(capacity: 64, itemCount: 100);

        // Warm up first: the very first call has to be JIT-compiled, and that allocates. Measuring
        // without this would record the compiler's work rather than the enumeration's.
        var warmup = 0;
        foreach (var item in buffer)
        {
            warmup += item;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        var total = 0;
        foreach (var item in buffer)
        {
            total += item;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(warmup, total);
    }
}
