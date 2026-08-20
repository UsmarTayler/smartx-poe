using System.Collections;

namespace SmartX.Shared;

/// <summary>
/// A fixed-capacity window of the most recent items, oldest evicted first.
/// </summary>
/// <typeparam name="T">The item type. Unconstrained — this holds packets as readily as readings.</typeparam>
/// <remarks>
/// <b>Why this exists rather than a <see cref="List{T}"/>.</b> Sensor telemetry is a sliding window
/// of recent history: the last few hundred readings from a node matter, and everything older is
/// dead weight. A <see cref="List{T}"/> models that badly in both directions.
/// <para>
/// It grows without bound, so a node reporting every few seconds fills memory until the process
/// dies. Trimming it means <c>RemoveAt(0)</c>, and that is the expensive part —
/// <see cref="List{T}"/> is an array behind an index, so removing the first element has to shift
/// every remaining element down one slot. That is O(n): with 1,000 retained readings, each new
/// reading copies 999 elements. Ingest a thousand readings and the list performs roughly a million
/// element copies purely to stay the same size.
/// </para>
/// <para>
/// A ring buffer does no shifting at all. The array never moves; two integers move instead. Writing
/// past the end wraps to index 0 and overwrites the oldest entry in place, so both append and
/// indexed read are O(1) regardless of capacity — the same thousand readings cost a thousand
/// writes, not a million copies. The allocation is made once, at construction, and never again.
/// </para>
/// <para>
/// The cost of the design, stated plainly: capacity is fixed, so data genuinely is lost once the
/// window fills. That is correct for this use — it is retention, not storage — but it makes the
/// type wrong for anything that must keep everything.
/// </para>
/// <para>
/// Not thread-safe. Callers sharing one across threads must serialise access; the store that holds
/// these does exactly that, per node.
/// </para>
/// </remarks>
public sealed class TelemetryRingBuffer<T> : IEnumerable<T>
{
    private readonly T[] _items;

    // Index of the oldest item. Everything is addressed relative to this, which is what removes the
    // need to shift: evicting the oldest entry is an increment, not a copy.
    private int _head;
    private int _count;

    /// <summary>Creates a buffer that retains at most <paramref name="capacity"/> items.</summary>
    /// <param name="capacity">
    /// How many items to retain. Zero is permitted and means retention is disabled: writes are
    /// accepted and immediately discarded.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <remarks>
    /// Zero is allowed rather than rejected because "retain nothing" is a real configuration, and
    /// supporting it here as a documented no-op is better than making every caller special-case it.
    /// A negative capacity is not a configuration — it is a mistake — so it throws.
    /// </remarks>
    public TelemetryRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _items = capacity == 0 ? [] : new T[capacity];
    }

    /// <summary>How many items this buffer retains before it starts overwriting.</summary>
    public int Capacity => _items.Length;

    /// <summary>How many items it currently holds.</summary>
    public int Count => _count;

    /// <summary>Is the next write going to evict the oldest item?</summary>
    public bool IsFull => _count == _items.Length;

    /// <summary>Is it holding nothing?</summary>
    public bool IsEmpty => _count == 0;

    /// <summary>
    /// Appends an item, overwriting the oldest if the buffer is full.
    /// </summary>
    /// <remarks>
    /// O(1) in every case. When full, the write lands on the slot the oldest item occupied and the
    /// head advances past it — no element is moved and no array is reallocated.
    /// </remarks>
    public void Add(T item)
    {
        // A zero-capacity buffer has nowhere to write. Returning here rather than dividing by
        // _items.Length keeps the modulo below safe.
        if (_items.Length == 0)
        {
            return;
        }

        var writeIndex = (_head + _count) % _items.Length;
        _items[writeIndex] = item;

        if (_count == _items.Length)
        {
            // Full: the write just overwrote the oldest item, so the head moves to what is now the
            // oldest. Count does not change, because the buffer is still full.
            _head = (_head + 1) % _items.Length;
        }
        else
        {
            _count++;
        }
    }

    /// <summary>
    /// Reads an item by age, where index 0 is the oldest retained item.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the retained items.</exception>
    /// <remarks>
    /// Indexed by age rather than by physical slot, so callers never have to know where the head
    /// happens to be sitting. O(1): one addition, one modulo, one array read.
    /// </remarks>
    public T this[int index]
    {
        get
        {
            // The unsigned cast folds the negative check into the upper-bound check: a negative
            // index reinterprets as a very large uint and fails the same comparison.
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, $"The buffer holds {_count} items.");
            }

            return _items[(_head + index) % _items.Length];
        }
    }

    /// <summary>The oldest retained item.</summary>
    /// <exception cref="InvalidOperationException">The buffer is empty.</exception>
    public T Oldest => _count > 0 ? this[0] : throw new InvalidOperationException("The buffer is empty.");

    /// <summary>The most recently added item.</summary>
    /// <exception cref="InvalidOperationException">The buffer is empty.</exception>
    public T Newest => _count > 0 ? this[_count - 1] : throw new InvalidOperationException("The buffer is empty.");

    /// <summary>Discards everything, without releasing the backing array.</summary>
    /// <remarks>
    /// The array is deliberately kept — its whole purpose is to be allocated once. The slots are
    /// cleared so that reference items are not held alive by a buffer that no longer logically
    /// contains them.
    /// </remarks>
    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Copies the retained items into a new list, oldest first.
    /// </summary>
    /// <remarks>
    /// The snapshot handed to API responses. Copying is the point: the buffer keeps overwriting
    /// itself as telemetry arrives, so a caller serialising it directly could observe it change
    /// mid-response.
    /// <para>
    /// Built at exact capacity so the list never reallocates while filling, and copied as two
    /// contiguous runs — from the head to the end of the array, then from the start of the array up
    /// to the head — which avoids a modulo per element and lets each run copy in bulk.
    /// </para>
    /// </remarks>
    public List<T> ToList()
    {
        var snapshot = new List<T>(_count);

        if (_count == 0)
        {
            return snapshot;
        }

        var firstRun = Math.Min(_count, _items.Length - _head);

        snapshot.AddRange(_items.AsSpan(_head, firstRun));
        snapshot.AddRange(_items.AsSpan(0, _count - firstRun));

        return snapshot;
    }

    /// <summary>Enumerates the retained items, oldest to newest.</summary>
    /// <remarks>
    /// Returns the concrete <see cref="Enumerator"/> struct rather than <see cref="IEnumerator{T}"/>.
    /// C# binds <c>foreach</c> to this method by name before it considers the interfaces, so
    /// iterating a <see cref="TelemetryRingBuffer{T}"/> directly allocates nothing at all.
    /// </remarks>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Enumerates through the interface, for LINQ and other generic consumers.</summary>
    /// <remarks>
    /// This path <i>does</i> allocate: returning a struct as an interface boxes it, once per
    /// enumeration. That is the unavoidable price of composing with LINQ, and it is why the
    /// hot path — the store copying a node's history — uses <see cref="ToList"/> or the concrete
    /// enumerator rather than a LINQ query.
    /// </remarks>
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Walks the buffer from oldest to newest without allocating.
    /// </summary>
    /// <remarks>
    /// A struct rather than an iterator method. A <c>yield return</c> iterator is compiled into a
    /// class, and calling it allocates one instance per enumeration — negligible once, but this is
    /// walked per node on every dashboard refresh. A struct enumerator lives on the caller's stack
    /// and costs nothing.
    /// <para>
    /// It holds a reference to the buffer and an index rather than copying anything out, so it
    /// reflects the buffer as it stands. Like the framework's own collection enumerators, it is not
    /// valid to keep enumerating across a concurrent write.
    /// </para>
    /// </remarks>
    public struct Enumerator : IEnumerator<T>
    {
        private readonly TelemetryRingBuffer<T> _buffer;
        private int _index;

        internal Enumerator(TelemetryRingBuffer<T> buffer)
        {
            _buffer = buffer;

            // Starts before the first item, because IEnumerator requires MoveNext to be called
            // before Current is read.
            _index = -1;
        }

        /// <summary>The item at the current position.</summary>
        public readonly T Current => _buffer[_index];

        /// <summary>
        /// The current item as an untyped reference.
        /// </summary>
        /// <remarks>
        /// Required by the non-generic <see cref="IEnumerator"/> that <see cref="IEnumerator{T}"/>
        /// derives from, and never reached by a <c>foreach</c> over the concrete type. It is
        /// explicit so that it does not appear on the struct's own surface, where it would offer a
        /// boxing read of a value type that the generic <see cref="Current"/> already returns
        /// without one.
        /// </remarks>
        readonly object? IEnumerator.Current => Current;

        /// <summary>Advances to the next item.</summary>
        /// <returns><see langword="false"/> once the newest item has been passed.</returns>
        public bool MoveNext()
        {
            if (_index + 1 >= _buffer.Count)
            {
                return false;
            }

            _index++;

            return true;
        }

        /// <summary>Returns to the position before the oldest item.</summary>
        public void Reset() => _index = -1;

        /// <summary>Nothing to release.</summary>
        public readonly void Dispose()
        {
        }
    }
}
