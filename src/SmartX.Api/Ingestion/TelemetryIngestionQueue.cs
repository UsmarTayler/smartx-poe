using Microsoft.Extensions.Options;
using SmartX.Api.History;
using SmartX.Shared;

namespace SmartX.Api.Ingestion;

/// <summary>A reading waiting to be processed, with what the queue needs to know about it.</summary>
/// <param name="ReceiptId">The id handed back to the caller when the reading was accepted.</param>
/// <param name="Sequence">
/// Its arrival number, taken from the one counter every history entry shares. It orders readings that carry the
/// same timestamp, and it is the second half of the key the reading is stored under in history.
/// </param>
/// <param name="Packet">The validated packet.</param>
/// <param name="EnqueuedAt">When it was accepted, so its wait can be measured on the way out.</param>
/// <param name="Alert">Set when the reading crossed a critical threshold.</param>
internal sealed record QueuedReading(
    Guid ReceiptId,
    long Sequence,
    ITelemetryPacket Packet,
    DateTimeOffset EnqueuedAt,
    CriticalAlert? Alert)
{
    public bool IsCritical => Alert is not null;

    public string Lane => IsCritical ? TelemetryIngestionQueue.CriticalLane : TelemetryIngestionQueue.StandardLane;
}

/// <summary>
/// The two lanes every accepted reading passes through: a FIFO queue for ordinary telemetry and a priority
/// queue for alerts.
/// </summary>
/// <remarks>
/// <b>Why <see cref="Queue{T}"/> and not a channel.</b> Production .NET would reach for
/// <see cref="System.Threading.Channels.Channel{T}"/>, which is a bounded, thread-safe, async-aware queue
/// with backpressure already built in. This uses <see cref="Queue{T}"/> because the brief asks for it, and
/// because the FIFO mechanics stay visible: the lock, the bound and the hand-off to the worker are written
/// out rather than hidden behind a library. The trade-off is real, and getting it right is this class.
/// <para>
/// <b>The lock and the semaphore do different jobs.</b> <see cref="Queue{T}"/> is not thread-safe and
/// requests arrive on many threads at once, so every touch of either lane happens inside one lock. The lock
/// cannot make a worker wait for an item, though — holding it while idle would block every incoming
/// request — so a <see cref="SemaphoreSlim"/> carries the "something arrived" signal instead. One release
/// per enqueue, one wait per dequeue.
/// </para>
/// <para>
/// <b>Both lanes are bounded.</b> When a lane is full the reading is refused rather than queued, and the
/// endpoint answers 503 with Retry-After. An unbounded queue under sustained overload is only a slower way
/// to run out of memory, and it hides the overload until the process dies.
/// </para>
/// </remarks>
internal sealed class TelemetryIngestionQueue(IOptions<IngestionOptions> options, TimeProvider time, ArrivalSequence arrival)
{
    internal const string StandardLane = "standard";
    internal const string CriticalLane = "critical";

    private readonly IngestionOptions _options = options.Value;
    private readonly Lock _gate = new();

    private readonly Queue<QueuedReading> _standard = new();

    // Ordered by (severity, arrival). PriorityQueue is a binary heap, and heaps are not stable: two entries
    // of equal priority can come out in either order. Pairing the severity with the arrival sequence number
    // makes every priority unique, which restores arrival order among equally severe alerts — the property
    // an operator assumes when two pumps fail seconds apart. The same number later becomes the history key.
    private readonly PriorityQueue<QueuedReading, (int Severity, long Sequence)> _critical = new();

    private readonly SemaphoreSlim _signal = new(0);

    private readonly LaneCounters _standardCounters = new();
    private readonly LaneCounters _criticalCounters = new();

    private int _consecutiveCritical;
    private long _rejectedWhenFull;

    /// <summary>Accepts a reading into the lane its classification calls for.</summary>
    /// <param name="packet">The validated packet.</param>
    /// <param name="alert">Set when the reading is critical.</param>
    /// <param name="receipt">What the caller is told: the lane it joined, and its position in that lane.</param>
    /// <returns><see langword="false"/> when the lane is full and the reading was refused.</returns>
    public bool TryEnqueue(ITelemetryPacket packet, CriticalAlert? alert, out TelemetryReceipt? receipt)
    {
        var now = time.GetUtcNow();
        var reading = new QueuedReading(Guid.NewGuid(), arrival.Next(), packet, now, alert);
        int position;

        lock (_gate)
        {
            if (reading.IsCritical)
            {
                if (_critical.Count >= _options.MaxCriticalQueueDepth)
                {
                    _rejectedWhenFull++;
                    receipt = null;

                    return false;
                }

                _critical.Enqueue(reading, (alert!.Value.Severity, reading.Sequence));
                position = _critical.Count;
            }
            else
            {
                if (_standard.Count >= _options.MaxQueueDepth)
                {
                    _rejectedWhenFull++;
                    receipt = null;

                    return false;
                }

                _standard.Enqueue(reading);
                position = _standard.Count;
            }
        }

        // Released after the lock, so a worker woken by it never finds the lock still held.
        _signal.Release();

        receipt = new TelemetryReceipt(reading.ReceiptId, packet.NodeId, reading.Lane, position, now);

        return true;
    }

    /// <summary>Takes the next reading, waiting until one arrives.</summary>
    /// <remarks>
    /// <b>Critical first, but not to the exclusion of everything else.</b> The critical lane is drained
    /// ahead of the standard one, except that after <see cref="IngestionOptions.MaxConsecutiveCritical"/>
    /// alerts in a row the worker takes one ordinary reading. Without that, an alert storm would leave the
    /// standard lane untouched until it filled and began refusing readings, and the mesh would look quiet at
    /// exactly the moment it was busiest.
    /// </remarks>
    public async Task<QueuedReading> DequeueAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken);

        lock (_gate)
        {
            if (TakesCritical(_consecutiveCritical, _critical.Count, _standard.Count, _options.MaxConsecutiveCritical))
            {
                _consecutiveCritical++;

                return _critical.Dequeue();
            }

            _consecutiveCritical = 0;

            // The semaphore is released exactly once per enqueued reading, so a successful wait guarantees a
            // reading is here: if the critical lane was empty or yielding, the standard lane holds it.
            return _standard.Dequeue();
        }
    }

    /// <summary>
    /// Whether the worker's next reading comes from the critical lane. The one place the rule is written, used by
    /// <see cref="DequeueAsync"/> to choose and by <see cref="Stats"/> to say what will be chosen, so the order the interface
    /// shows cannot drift from the order the worker takes.
    /// </summary>
    private static bool TakesCritical(int consecutiveCritical, int criticalWaiting, int standardWaiting, int maxConsecutiveCritical) =>
        criticalWaiting > 0 && !(consecutiveCritical >= maxConsecutiveCritical && standardWaiting > 0);

    /// <summary>
    /// The next few readings in the order the worker will take them. Read under the queue lock, so it is one consistent moment.
    /// </summary>
    /// <remarks>
    /// Only the first <see cref="NextUpShown"/> of each lane can be among the next few, so that is all that is gathered. The
    /// heap is not sorted, so the critical lane's front is found by ordering a copy of its entries by the same
    /// (severity, arrival) key the heap uses, and LINQ's ordered Take keeps that a partial selection and not a full sort of a lane
    /// that can hold a thousand entries. The standard queue enumerates in arrival order already.
    /// </remarks>
    private List<QueuedItem> NextUp(DateTimeOffset now)
    {
        var critical = _critical.UnorderedItems.OrderBy(entry => entry.Priority).Take(NextUpShown).Select(entry => entry.Element).ToList();
        var standard = _standard.Take(NextUpShown).ToList();

        var items = new List<QueuedItem>(NextUpShown);
        var consecutive = _consecutiveCritical;
        int criticalTaken = 0, standardTaken = 0;

        while (items.Count < NextUpShown && (criticalTaken < critical.Count || standardTaken < standard.Count))
        {
            // The counts passed are what is still waiting, which is what the worker would see at that point.
            var fromCritical = TakesCritical(consecutive, _critical.Count - criticalTaken, _standard.Count - standardTaken, _options.MaxConsecutiveCritical);
            var reading = fromCritical ? critical[criticalTaken++] : standard[standardTaken++];

            consecutive = fromCritical ? consecutive + 1 : 0;
            items.Add(new QueuedItem(items.Count + 1, reading.Lane, reading.Packet.NodeId, (now - reading.EnqueuedAt).TotalMilliseconds));
        }

        return items;
    }

    /// <summary>How many of the next readings a snapshot names.</summary>
    internal const int NextUpShown = 6;

    /// <summary>Records that a reading has been fully processed, and how long that took from acceptance.</summary>
    /// <remarks>
    /// Called by the worker after the reading is stored and scored, not when it was taken off the queue, so
    /// "processed" in the stats means finished rather than started. A test that waits for this count can
    /// therefore be certain the reading is in the store.
    /// </remarks>
    public void Complete(QueuedReading reading)
    {
        var waited = (time.GetUtcNow() - reading.EnqueuedAt).TotalMilliseconds;

        lock (_gate)
        {
            (reading.IsCritical ? _criticalCounters : _standardCounters).Record(waited);
        }
    }

    /// <summary>A snapshot of both lanes, for the pipeline endpoint.</summary>
    public PipelineStats Stats()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();

            return new PipelineStats(
                [
                    _criticalCounters.ToStats(CriticalLane, _critical.Count),
                    _standardCounters.ToStats(StandardLane, _standard.Count),
                ],
                _options.MaxQueueDepth,
                _rejectedWhenFull,
                now,
                NextUp(now),
                _options.MaxConsecutiveCritical);
        }
    }

    /// <summary>What one lane has done. Only ever touched under the queue lock.</summary>
    private sealed class LaneCounters
    {
        private long _processed;
        private double _totalWaitMilliseconds;
        private double _maximumWaitMilliseconds;

        public void Record(double waitMilliseconds)
        {
            _processed++;
            _totalWaitMilliseconds += waitMilliseconds;
            _maximumWaitMilliseconds = Math.Max(_maximumWaitMilliseconds, waitMilliseconds);
        }

        public LaneStats ToStats(string lane, int depth) => new(
            lane,
            depth,
            _processed,
            _processed == 0 ? 0 : _totalWaitMilliseconds / _processed,
            _maximumWaitMilliseconds);
    }
}
