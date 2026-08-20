using System.Runtime.CompilerServices;

namespace SmartX.Shared;

/// <summary>
/// Holds sequential batches of raw readings before they enter the validated pipeline.
/// </summary>
/// <typeparam name="T">The raw reading type. A value type, so rows store readings inline.</typeparam>
/// <remarks>
/// <b>Why a jagged <c>T[][]</c> and not a rectangular <c>T[,]</c>.</b> This is a memory layout
/// decision, and it is the whole reason the type exists in this shape.
/// <para>
/// A rectangular array is a single allocation of <c>rows × columns</c> cells, so its column count
/// has to be fixed at the width of the <i>widest</i> row. Ingestion windows do not have a fixed
/// width: one node may report 500 readings in a window while another reports 3, because sampling
/// rates differ per device and a window spanning a dropout is short. Sizing a rectangle to the
/// widest window means every other row is padded out to that width with cells that are allocated,
/// zeroed, and never written.
/// </para>
/// <para>
/// Concretely: 100 windows where one holds 500 readings and the rest hold 10 is 1,490 real
/// readings. A <c>T[,]</c> would allocate 100 × 500 = 50,000 cells — over thirty times the data,
/// and for <c>float</c> that is roughly 194 KB of padding to carry 6 KB of telemetry. A jagged
/// array allocates each row at its own length, so it allocates 1,490 cells and nothing else.
/// <see cref="Statistics"/> reports exactly this figure for whatever is currently staged.
/// </para>
/// <para>
/// The trade-off, stated honestly: a jagged array is one allocation per row plus one for the outer
/// array of references, so it has more GC objects and its rows are not guaranteed to be contiguous
/// in memory. A rectangular array is one allocation with perfect locality. That trade is worth
/// making here because the rows are written once and read sequentially, and the padding a rectangle
/// would cost dwarfs the locality it would buy.
/// </para>
/// </remarks>
public sealed class TelemetryStagingBuffer<T>
    where T : struct
{
    private const int DefaultWindowCapacity = 4;

    // Shared by every empty window, since a zero-length array holds no state and cannot be written
    // to. Allocating a fresh one per empty window would be pure waste.
    private static readonly T[] EmptyWindow = [];

    private T[][] _windows;
    private int _windowCount;
    private int _totalReadings;
    private int _longestWindow;

    /// <summary>Creates a staging buffer.</summary>
    /// <param name="initialWindowCapacity">
    /// How many windows the outer array can hold before it has to grow. The outer array holds
    /// references, so over-reserving here is cheap; the rows are what carry the data.
    /// </param>
    public TelemetryStagingBuffer(int initialWindowCapacity = DefaultWindowCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialWindowCapacity);

        _windows = initialWindowCapacity == 0 ? [] : new T[initialWindowCapacity][];
    }

    /// <summary>Ingestion windows currently staged.</summary>
    public int WindowCount => _windowCount;

    /// <summary>Readings across every staged window.</summary>
    public int TotalReadings => _totalReadings;

    /// <summary>What is staged, and what the jagged layout saved against a rectangular one.</summary>
    public StagingStatistics Statistics
    {
        get
        {
            // Widened to long before multiplying: the point of the figure is that a rectangular
            // equivalent can be far larger than the real data, so the hypothetical number is
            // exactly the one most likely to overflow an int.
            var rectangularCells = (long)_windowCount * _longestWindow;
            var wastedCells = rectangularCells - _totalReadings;

            return new StagingStatistics(
                _windowCount,
                _totalReadings,
                _longestWindow,
                rectangularCells,
                wastedCells,
                wastedCells * Unsafe.SizeOf<T>());
        }
    }

    /// <summary>Stages one ingestion window.</summary>
    /// <param name="readings">The window's readings, copied into a row of exactly this length.</param>
    /// <remarks>
    /// Takes a <see cref="ReadOnlySpan{T}"/> so a caller can stage from an array, a slice of one, or
    /// stack-allocated memory without first copying into an array the buffer would then copy again.
    /// The row itself is a private copy, so the caller is free to reuse its own buffer afterwards.
    /// </remarks>
    public void Stage(ReadOnlySpan<T> readings)
    {
        if (_windowCount == _windows.Length)
        {
            GrowWindows();
        }

        // The row is allocated at the window's actual length. This is the jagged advantage in one
        // line: a rectangular array has no equivalent, because its width is fixed for all rows.
        T[] row;
        if (readings.IsEmpty)
        {
            row = EmptyWindow;
        }
        else
        {
            row = new T[readings.Length];
            readings.CopyTo(row);
        }

        _windows[_windowCount] = row;
        _windowCount++;
        _totalReadings += readings.Length;

        if (readings.Length > _longestWindow)
        {
            _longestWindow = readings.Length;
        }
    }

    /// <summary>Reads one staged window without copying it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the staged windows.</exception>
    public ReadOnlySpan<T> Window(int windowIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(windowIndex, _windowCount);

        return _windows[windowIndex];
    }

    /// <summary>
    /// Flattens every staged window into one list, in the order the windows were staged.
    /// </summary>
    /// <remarks>
    /// The list is constructed at exactly <see cref="TotalReadings"/> capacity. A
    /// <see cref="List{T}"/> that grows organically doubles its backing array as it fills,
    /// reallocating and copying everything each time — filling n items that way costs roughly 2n
    /// element copies and leaves an array up to twice the size actually needed. The final count is
    /// already known here, so none of that has to happen.
    /// </remarks>
    public List<T> Flatten()
    {
        var flattened = new List<T>(_totalReadings);

        for (var window = 0; window < _windowCount; window++)
        {
            flattened.AddRange(_windows[window]);
        }

        return flattened;
    }

    /// <summary>Flattens the staged windows and empties the buffer.</summary>
    /// <remarks>
    /// The handover point in the pipeline: staging holds raw batches, and everything downstream
    /// works from the list. Draining rather than flattening makes it explicit that the staged copy
    /// is no longer the live one.
    /// </remarks>
    public List<T> Drain()
    {
        var drained = Flatten();
        Clear();

        return drained;
    }

    /// <summary>Discards every staged window.</summary>
    public void Clear()
    {
        // Clearing the references lets the rows be collected. Without this the outer array would
        // keep every drained batch alive for as long as the buffer itself lived.
        Array.Clear(_windows, 0, _windowCount);

        _windowCount = 0;
        _totalReadings = 0;
        _longestWindow = 0;
    }

    /// <summary>Doubles the outer array's capacity.</summary>
    private void GrowWindows() =>
        Array.Resize(ref _windows, _windows.Length == 0 ? DefaultWindowCapacity : _windows.Length * 2);
}
