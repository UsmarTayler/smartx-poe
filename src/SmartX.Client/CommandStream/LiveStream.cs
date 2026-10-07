using SmartX.Client.Realtime;
using SmartX.Client.State;
using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>One line of the live stream.</summary>
/// <param name="Id">Increases by one per row, so a row has an identity that survives rows falling off the buffer.</param>
/// <param name="Reading">The reading.</param>
/// <param name="Critical">Whether it crossed a critical threshold, and so took the critical lane.</param>
/// <param name="Reason">For a critical reading, why.</param>
public sealed record StreamRow(long Id, ReadingPush Reading, bool Critical, string? Reason);

/// <summary>A critical reading held at the top of the stream until it is acknowledged or its time runs out.</summary>
/// <param name="Alert">The critical reading as announced.</param>
/// <param name="PinnedAt">When it was pinned.</param>
public sealed record PinnedCritical(PriorityAlert Alert, DateTimeOffset PinnedAt);

/// <summary>
/// The live stream of readings: a bounded buffer, a pause that holds what arrives, and critical readings pinned on top.
/// </summary>
/// <remarks>
/// <b>Bounded, twice.</b> The stream and the pause queue are each a <see cref="TelemetryRingBuffer{T}"/> from the shared
/// project, the structure that was built for this: a fixed number of slots, adding to a full one overwrites the oldest,
/// and iterating allocates nothing. A stream left open for a day holds the same few hundred rows as one opened a minute
/// ago, and a stream paused for an hour does not hoard an hour of readings.
/// <para>
/// <b>Pause holds, resume applies in order.</b> While paused, arriving readings go to the queue and a counter says how
/// many are waiting. Resuming moves them into the stream in the order they arrived, so what the user sees afterwards is
/// exactly what they would have seen had they never paused.
/// </para>
/// <para>
/// <b>Critical readings pin.</b> A critical reading arrives twice: at once as a priority alert, which is what pins it, and
/// a moment later inside the next batch of ordinary readings, where it is marked critical by matching the device and
/// timestamp. A pinned reading stays until the user acknowledges it or <see cref="CommandStreamOptions.PinDuration"/>
/// passes. Pinning is not affected by pausing, because a critical event is exactly what must not wait for a user to resume.
/// </para>
/// <para>
/// Scoped: the buffer, the pause state and the pins are held for the life of the tab, so navigating away and back finds the
/// stream where it was. It does not touch the connection; it listens to the two feeds built on it.
/// </para>
/// </remarks>
public sealed class LiveStream : IAsyncDisposable
{
    private readonly ITelemetryStream _stream;
    private readonly IAlertFeed _alerts;
    private readonly AppState _state;
    private readonly TimeProvider _time;
    private readonly CommandStreamOptions _options;
    private readonly Lock _gate = new();

    private readonly TelemetryRingBuffer<StreamRow> _rows;
    private readonly TelemetryRingBuffer<StreamRow> _pending;
    private readonly List<PinnedCritical> _pinned = [];
    private readonly Dictionary<(string Mac, DateTimeOffset At), PriorityAlert> _criticalByReading = [];
    private readonly Queue<(string Mac, DateTimeOffset At)> _criticalOrder = new();

    private long _nextId;
    private long _version;
    private long _pendingDropped;
    private int _attached;
    private ITimer? _expiry;

    public LiveStream(ITelemetryStream stream, IAlertFeed alerts, AppState state, TimeProvider time, CommandStreamOptions options)
    {
        _stream = stream;
        _alerts = alerts;
        _state = state;
        _time = time;
        _options = options;
        _rows = new TelemetryRingBuffer<StreamRow>(options.StreamCapacity);
        _pending = new TelemetryRingBuffer<StreamRow>(options.StreamCapacity);
    }

    /// <summary>Raised whenever what the stream shows changes.</summary>
    public event Action? Changed;

    /// <summary>Goes up on every change, so a component can tell whether there is anything new to draw.</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>Whether the stream is paused.</summary>
    public bool Paused => _state.StreamPaused;

    /// <summary>How many readings arrived while paused and are waiting for resume.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>How many waiting readings were pushed out because the pause lasted longer than the queue holds.</summary>
    public long PendingDropped => Volatile.Read(ref _pendingDropped);

    /// <summary>How many rows the stream holds. Never more than the configured capacity.</summary>
    public int RowCount
    {
        get
        {
            lock (_gate)
            {
                return _rows.Count;
            }
        }
    }

    /// <summary>The pinned critical readings, newest first.</summary>
    public IReadOnlyList<PinnedCritical> Pinned
    {
        get
        {
            lock (_gate)
            {
                return [.. _pinned.AsEnumerable().Reverse()];
            }
        }
    }

    /// <summary>Whether anything is listening to the feeds. For tests: one per active page, never more.</summary>
    public int AttachedPages => Volatile.Read(ref _attached);

    // ---- starting and stopping ----------------------------------------------------------------------------

    /// <summary>Begins listening. Each call is matched by a <see cref="StopAsync"/>.</summary>
    public async Task StartAsync()
    {
        if (Interlocked.Increment(ref _attached) == 1)
        {
            _stream.BatchReceived += OnBatch;
            _alerts.PriorityAlertReceived += OnPriorityAlert;
            _expiry = _time.CreateTimer(_ => Prune(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        await _stream.StartAsync();
        await _alerts.StartAsync();
    }

    /// <summary>Ends one page's interest.</summary>
    public async Task StopAsync()
    {
        if (Volatile.Read(ref _attached) == 0)
        {
            return;
        }

        await _stream.StopAsync();
        await _alerts.StopAsync();

        if (Interlocked.Decrement(ref _attached) == 0)
        {
            _stream.BatchReceived -= OnBatch;
            _alerts.PriorityAlertReceived -= OnPriorityAlert;
            _expiry?.Dispose();
            _expiry = null;
        }
    }

    // ---- pausing ----------------------------------------------------------------------------------------------

    /// <summary>Pauses or resumes. Resuming applies, in order, everything that arrived while paused.</summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (!paused)
            {
                foreach (var row in _pending)
                {
                    _rows.Add(row);
                }

                _pending.Clear();
                _pendingDropped = 0;
            }

            _state.SetStreamPaused(paused);
        }

        Touch();
    }

    // ---- reading the stream -----------------------------------------------------------------------------------

    /// <summary>
    /// The rows to show, newest first.
    /// </summary>
    /// <param name="allowed">
    /// The devices that match the page's filters, or null to show everything. The stream is filtered by the same filters as
    /// the device list because it asks the same question: which devices am I interested in.
    /// </param>
    public IReadOnlyList<StreamRow> Rows(IReadOnlySet<string>? allowed)
    {
        lock (_gate)
        {
            var rows = new List<StreamRow>(_rows.Count);

            for (var index = _rows.Count - 1; index >= 0; index--)
            {
                var row = _rows[index];

                if (allowed is null || allowed.Contains(row.Reading.MacAddress))
                {
                    rows.Add(row);
                }
            }

            return rows;
        }
    }

    // ---- critical readings --------------------------------------------------------------------------------------

    /// <summary>Acknowledges a pinned critical reading, which unpins it.</summary>
    public void Acknowledge(long sequence)
    {
        bool removed;

        lock (_gate)
        {
            removed = _pinned.RemoveAll(pinned => pinned.Alert.Sequence == sequence) > 0;
        }

        if (removed)
        {
            Touch();
        }
    }

    /// <summary>Unpins critical readings whose time has run out.</summary>
    /// <returns>How many were unpinned.</returns>
    public int Prune()
    {
        int removed;
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            removed = _pinned.RemoveAll(pinned => now - pinned.PinnedAt >= _options.PinDuration);
        }

        if (removed > 0)
        {
            Touch();
        }

        return removed;
    }

    // ---- what arrives ---------------------------------------------------------------------------------------------

    private void OnPriorityAlert(PriorityAlert alert)
    {
        lock (_gate)
        {
            _pinned.Add(new PinnedCritical(alert, _time.GetUtcNow()));

            while (_pinned.Count > _options.MaxPinned)
            {
                _pinned.RemoveAt(0);
            }

            // Remembered so the same reading, arriving in its batch a moment later, is marked as critical. Bounded,
            // like everything else here.
            var key = (alert.MacAddress, alert.Timestamp);
            _criticalByReading[key] = alert;
            _criticalOrder.Enqueue(key);

            while (_criticalOrder.Count > _options.MaxPinned * 4)
            {
                _criticalByReading.Remove(_criticalOrder.Dequeue());
            }
        }

        Touch();
    }

    private void OnBatch(ReadingsBatch batch)
    {
        lock (_gate)
        {
            foreach (var reading in batch.Readings)
            {
                var critical = _criticalByReading.GetValueOrDefault((reading.MacAddress, reading.Timestamp));
                var row = new StreamRow(++_nextId, reading, critical is not null, critical?.Reason);

                if (_state.StreamPaused)
                {
                    // Held, in arrival order. A full queue pushes its oldest out, and says so.
                    if (_pending.IsFull)
                    {
                        _pendingDropped++;
                    }

                    _pending.Add(row);
                }
                else
                {
                    _rows.Add(row);
                }
            }
        }

        Touch();
    }

    private void Touch()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        while (Volatile.Read(ref _attached) > 0)
        {
            await StopAsync();
        }
    }
}
