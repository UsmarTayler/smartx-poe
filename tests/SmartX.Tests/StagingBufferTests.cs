using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Covers the jagged staging buffer, including the memory figure that justifies the layout.
/// </summary>
public class StagingBufferTests
{
    [Fact]
    public void Windows_of_different_lengths_are_each_stored_at_their_own_length()
    {
        // The case a rectangular array cannot represent without padding.
        var staging = new TelemetryStagingBuffer<float>();

        staging.Stage([21.5f, 21.75f, 22.0f]);
        staging.Stage([18.0f]);
        staging.Stage([30.5f, 31.0f, 31.5f, 32.0f, 32.5f]);

        Assert.Equal(3, staging.WindowCount);
        Assert.Equal(9, staging.TotalReadings);
        Assert.Equal(3, staging.Window(0).Length);
        Assert.Equal(1, staging.Window(1).Length);
        Assert.Equal(5, staging.Window(2).Length);
    }

    [Fact]
    public void Statistics_report_what_a_rectangular_array_would_have_wasted()
    {
        // Three windows, longest holding 5, so a float[3,5] would allocate 15 cells to carry 9
        // readings. Six cells of padding at four bytes each.
        var staging = new TelemetryStagingBuffer<float>();

        staging.Stage([21.5f, 21.75f, 22.0f]);
        staging.Stage([18.0f]);
        staging.Stage([30.5f, 31.0f, 31.5f, 32.0f, 32.5f]);

        var statistics = staging.Statistics;

        Assert.Equal(5, statistics.LongestWindow);
        Assert.Equal(15, statistics.RectangularCells);
        Assert.Equal(6, statistics.WastedCells);
        Assert.Equal(24, statistics.WastedBytes);
    }

    [Fact]
    public void Uniform_windows_waste_nothing_which_is_the_case_the_layouts_agree_on()
    {
        var staging = new TelemetryStagingBuffer<int>();

        staging.Stage([1, 2]);
        staging.Stage([3, 4]);

        Assert.Equal(0, staging.Statistics.WastedCells);
        Assert.Equal(0, staging.Statistics.WastedBytes);
    }

    [Fact]
    public void Flattening_preserves_window_order_and_order_within_each_window()
    {
        var staging = new TelemetryStagingBuffer<int>();

        staging.Stage([1, 2, 3]);
        staging.Stage([4]);
        staging.Stage([5, 6]);

        Assert.Equal([1, 2, 3, 4, 5, 6], staging.Flatten());
    }

    [Fact]
    public void The_flattened_list_is_sized_to_the_reading_count()
    {
        // Pre-sizing is the optimisation: a list left to grow would reallocate as it filled.
        var staging = new TelemetryStagingBuffer<int>();
        staging.Stage([1, 2, 3, 4, 5]);

        var flattened = staging.Flatten();

        Assert.Equal(5, flattened.Count);
        Assert.Equal(5, flattened.Capacity);
    }

    [Fact]
    public void An_empty_window_is_staged_without_disturbing_the_order()
    {
        // A window spanning a total dropout is legitimately empty, not an error.
        var staging = new TelemetryStagingBuffer<int>();

        staging.Stage([1]);
        staging.Stage([]);
        staging.Stage([2]);

        Assert.Equal(3, staging.WindowCount);
        Assert.Equal([1, 2], staging.Flatten());
    }

    [Fact]
    public void The_outer_array_grows_past_its_initial_capacity()
    {
        var staging = new TelemetryStagingBuffer<int>(initialWindowCapacity: 2);

        for (var window = 0; window < 10; window++)
        {
            staging.Stage([window]);
        }

        Assert.Equal(10, staging.WindowCount);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], staging.Flatten());
    }

    [Fact]
    public void A_buffer_created_with_no_capacity_still_grows()
    {
        var staging = new TelemetryStagingBuffer<int>(initialWindowCapacity: 0);

        staging.Stage([7]);

        Assert.Equal([7], staging.Flatten());
    }

    [Fact]
    public void Draining_hands_over_the_readings_and_empties_the_buffer()
    {
        var staging = new TelemetryStagingBuffer<int>();
        staging.Stage([1, 2]);
        staging.Stage([3]);

        var drained = staging.Drain();

        Assert.Equal([1, 2, 3], drained);
        Assert.Equal(0, staging.WindowCount);
        Assert.Equal(0, staging.TotalReadings);
        Assert.Empty(staging.Flatten());
    }

    [Fact]
    public void A_staged_window_is_a_copy_so_the_caller_can_reuse_its_own_array()
    {
        var scratch = new[] { 1, 2, 3 };
        var staging = new TelemetryStagingBuffer<int>();

        staging.Stage(scratch);
        scratch[0] = 99;

        Assert.Equal([1, 2, 3], staging.Flatten());
    }

    [Fact]
    public void Reading_a_window_outside_the_staged_range_throws()
    {
        var staging = new TelemetryStagingBuffer<int>();
        staging.Stage([1]);

        Assert.Throws<ArgumentOutOfRangeException>(() => staging.Window(1).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => staging.Window(-1).Length);
    }
}
