using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.Ingestion;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Realtime;

/// <summary>
/// Turns the system's domain events into messages for connected clients. The only place that pushes.
/// </summary>
/// <remarks>
/// <b>Why one broadcaster and not SignalR calls in the domain.</b> The trust engine, the alert set, the command
/// service and the ingestion worker do not reference SignalR. They raise events (<c>NodeEvaluated</c>,
/// <c>Changed</c>, <c>Processed</c>), as they would if nobody were listening, and this class subscribes to them and
/// forwards through <see cref="IHubContext{THub, T}"/>. Two reasons. The domain stays testable without a hub: its
/// tests construct it with no transport at all. And the transport can change again, as it already has once from
/// polling to push, without touching code that decides when a node is faulty or an alert is raised.
/// <para>
/// <b>What is batched, what is coalesced, what is immediate</b> — chosen by how often a message happens and how
/// much a delay would cost.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Readings are batched.</b> They are the highest-volume thing in the system: at 4,800 devices reporting every
/// few seconds that is over a thousand a second. One message per reading is a thousand sends and a thousand client
/// re-renders a second. They are collected and sent as one message every flush window (250 ms by default), so the
/// cost is the same however many arrived. A live chart cannot show the difference between 250 ms and instant.
/// </description></item>
/// <item><description>
/// <b>Trust is coalesced.</b> A node's score moves on every packet. Ten changes to one node inside a window are
/// not ten facts, they are one: where it is now. A set of "nodes that changed" is kept, and at each flush each is
/// sent once, at its latest state, and only if it moved by more than a threshold (see
/// <see cref="RealtimeOptions.TrustScoreEpsilon"/>). Sending the latest state, not the changes, is also what makes
/// a lost or reordered message harmless.
/// </description></item>
/// <item><description>
/// <b>Alerts, command changes and critical readings are immediate.</b> They are rare and they are the point of the
/// system. Delaying a critical reading by up to a flush window so it could ride in a batch would undo the
/// priority lane the ingestion queue exists to provide: the queue lets an alert overtake a backlog of ordinary
/// readings, and a batch timer would make it wait anyway. They go to the front of the line and are sent the
/// moment the previous send finishes. One exception inside alerts: when an already-active alert's repeat count
/// ticks up, that is a counter and not news, so those are coalesced like trust.
/// </description></item>
/// <item><description>
/// <b>Pipeline statistics are periodic</b>, once a second, because they are a gauge.
/// </description></item>
/// </list>
/// <para>
/// <b>Order and sequence numbers.</b> One loop sends everything, holding one lock, so messages go out in the order
/// they were numbered. Each message's content is read <i>before</i> its sequence number is taken, and a snapshot
/// reads the sequence <i>before</i> its data; together that means a snapshot as of sequence N contains everything
/// in messages up to N, which is what lets the client discard a message at or below N.
/// </para>
/// <para>
/// <b>Nothing is done for a topic nobody has joined.</b> Events for it are ignored on entry, so an idle gateway
/// pays a dictionary lookup per event, and the buffers cannot grow.
/// </para>
/// </remarks>
internal sealed class RealtimeBroadcaster : BackgroundService
{
    private readonly ITrustEngine _trust;
    private readonly AlertTracker _alerts;
    private readonly CommandService _commands;
    private readonly ProcessedReadings _processed;
    private readonly TelemetryIngestionQueue _queue;
    private readonly IHubContext<TelemetryHub, ITelemetryClient> _hub;
    private readonly RealtimeSubscriptions _subscriptions;
    private readonly RealtimeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RealtimeBroadcaster> _logger;

    // One send at a time, so messages leave in the order they were numbered.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // Sequence numbers, one per topic, and the recent critical readings. Together under one lock so a snapshot
    // can read a sequence and the list that goes with it as one consistent pair.
    private readonly Lock _sequenceGate = new();
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly Queue<PriorityAlert> _recentPriority = new();

    // What has changed since the last flush.
    private readonly Lock _pendingGate = new();
    private HashSet<string> _dirtyNodes = new(StringComparer.Ordinal);
    private HashSet<AlertKey> _repeatedAlerts = [];
    private List<ReadingPush> _readings = [];
    private int _droppedReadings;

    // What clients were last told about each node, so a score that only drifted is not sent again.
    private readonly Dictionary<string, SentTrust> _lastSentTrust = new(StringComparer.Ordinal);
    private long _trustGeneration;

    private readonly ConcurrentDictionary<string, ReadingPush> _latest = new(StringComparer.Ordinal);
    private readonly Channel<Func<Task>> _immediate;
    private long _droppedMessages;

    public RealtimeBroadcaster(
        ITrustEngine trust,
        AlertTracker alerts,
        CommandService commands,
        ProcessedReadings processed,
        TelemetryIngestionQueue queue,
        IHubContext<TelemetryHub, ITelemetryClient> hub,
        RealtimeSubscriptions subscriptions,
        IOptions<RealtimeOptions> options,
        TimeProvider time,
        ILogger<RealtimeBroadcaster> logger)
    {
        _trust = trust;
        _alerts = alerts;
        _commands = commands;
        _processed = processed;
        _queue = queue;
        _hub = hub;
        _subscriptions = subscriptions;
        _options = options.Value;
        _time = time;
        _logger = logger;

        // Bounded, and a full queue refuses the newest rather than blocking the thread that raised the event:
        // that thread is the ingestion worker or a request, and neither may ever wait on the network.
        _immediate = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(_options.MaxQueuedMessages)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

        _trust.NodeEvaluated += OnNodeEvaluated;
        _alerts.Changed += OnAlertChanged;
        _commands.Changed += OnCommandChanged;
        _processed.Processed += OnReadingProcessed;
    }

    // ---- sequence numbers and snapshots --------------------------------------------------------------------

    /// <summary>The sequence number of the last message sent on a topic, or zero if none has been.</summary>
    /// <remarks>
    /// A snapshot endpoint reads this <i>before</i> it reads the data it returns. If it read the data first, a
    /// message could be sent between the two, carrying a sequence number the snapshot claims to include and
    /// state it does not.
    /// </remarks>
    public long SequenceOf(string topic)
    {
        lock (_sequenceGate)
        {
            return _sequences.GetValueOrDefault(topic);
        }
    }

    /// <summary>The alerts topic's sequence and the recent critical readings, read together.</summary>
    public (long Sequence, IReadOnlyList<PriorityAlert> Recent) ReadPriorityAlerts()
    {
        lock (_sequenceGate)
        {
            return (_sequences.GetValueOrDefault(RealtimeTopics.Alerts), [.. _recentPriority]);
        }
    }

    /// <summary>The latest reading from each node that has reported, as of a sequence number.</summary>
    public StreamSnapshot GetStreamSnapshot()
    {
        var sequence = SequenceOf(RealtimeTopics.Stream);

        return new StreamSnapshot(sequence, [.. _latest.Values.OrderBy(reading => reading.MacAddress, StringComparer.Ordinal)]);
    }

    private long NextSequence(string topic)
    {
        lock (_sequenceGate)
        {
            var next = _sequences.GetValueOrDefault(topic) + 1;
            _sequences[topic] = next;

            return next;
        }
    }

    // ---- events in: each is cheap and returns at once ------------------------------------------------------

    private void OnNodeEvaluated(string mac)
    {
        if (!_subscriptions.Has(RealtimeTopics.Trust))
        {
            return;
        }

        lock (_pendingGate)
        {
            _dirtyNodes.Add(mac);
        }
    }

    private void OnAlertChanged(AlertChange change)
    {
        if (!_subscriptions.Has(RealtimeTopics.Alerts))
        {
            return;
        }

        if (change.Kind == AlertChangeKind.Suppressed)
        {
            // A counter ticking up, not news: coalesced into the next flush.
            lock (_pendingGate)
            {
                _repeatedAlerts.Add(change.Key);
            }

            return;
        }

        Enqueue(() => SendAlertChangeAsync(change));
    }

    private void OnCommandChanged(Command command, CommandEvent commandEvent)
    {
        if (!_subscriptions.Has(RealtimeTopics.Commands))
        {
            return;
        }

        Enqueue(() => SendCommandAsync(command, commandEvent));
    }

    private void OnReadingProcessed(ProcessedReading reading)
    {
        var push = ToPush(reading);

        // Always kept, for the stream's snapshot, however few are listening. One entry per node, so it cannot grow.
        _latest[push.MacAddress] = push;

        if (reading.Alert is { } alert)
        {
            // Always queued, with no subscriber check: the recent critical readings are part of the alerts
            // snapshot, so they are recorded whether or not anyone was listening when one happened.
            Enqueue(() => SendPriorityAsync(push, alert));
        }

        if (!_subscriptions.Has(RealtimeTopics.Stream) && !_subscriptions.HasDevice(push.MacAddress))
        {
            return;
        }

        lock (_pendingGate)
        {
            if (_readings.Count >= _options.MaxBufferedReadings)
            {
                _droppedReadings++;
            }
            else
            {
                _readings.Add(push);
            }
        }
    }

    private void Enqueue(Func<Task> send)
    {
        if (!_immediate.Writer.TryWrite(send) && Interlocked.Increment(ref _droppedMessages) % 1000 == 1)
        {
            _logger.LogWarning(
                "The real-time queue is full ({Limit}); {Count} messages have been dropped. Clients will resynchronise on reconnect.",
                _options.MaxQueuedMessages,
                Interlocked.Read(ref _droppedMessages));
        }
    }

    private static ReadingPush ToPush(ProcessedReading reading)
    {
        var packet = reading.Packet;
        var isState = packet is StatePacket;

        var value = packet is StatePacket state
            ? state.Value ? 1d : 0d
            : packet.TryGetNumericValue(out var number) ? number : 0d;

        return new ReadingPush(packet.NodeId, packet.Timestamp, value, packet.Unit, isState, reading.AcceptedAt);
    }

    // ---- sending: immediate messages -----------------------------------------------------------------------

    private async Task SendAlertChangeAsync(AlertChange change)
    {
        // The alert's state now, read before the sequence number is taken.
        IReadOnlyList<ActiveAlert> upserted = [];
        IReadOnlyList<AlertKey> cleared = [];

        if (change.Kind == AlertChangeKind.Cleared)
        {
            cleared = [change.Key];
        }
        else if (_alerts.TryGet(change.Key, out var alert))
        {
            upserted = [alert];
        }
        else
        {
            // Raised and already cleared by the time this ran. The clear is its own message, behind this one.
            return;
        }

        var stats = _alerts.Stats();
        var update = new AlertUpdate(NextSequence(RealtimeTopics.Alerts), upserted, cleared, stats);

        await _hub.Clients.Group(RealtimeTopics.Alerts).AlertsUpdated(update);
    }

    private async Task SendCommandAsync(Command command, CommandEvent commandEvent)
    {
        var update = new CommandUpdate(NextSequence(RealtimeTopics.Commands), command, commandEvent);

        await _hub.Clients.Group(RealtimeTopics.Commands).CommandChanged(update);
    }

    private async Task SendPriorityAsync(ReadingPush push, CriticalAlert alert)
    {
        PriorityAlert message;

        // Numbered and recorded under one lock, so a snapshot that reads the sequence also sees this alert.
        lock (_sequenceGate)
        {
            var sequence = _sequences.GetValueOrDefault(RealtimeTopics.Alerts) + 1;
            _sequences[RealtimeTopics.Alerts] = sequence;

            message = new PriorityAlert(
                sequence, push.MacAddress, alert.Reason, alert.Severity, push.Value, push.Unit, push.Timestamp, push.AcceptedAt);

            _recentPriority.Enqueue(message);

            while (_recentPriority.Count > _options.RecentPriorityAlerts)
            {
                _recentPriority.Dequeue();
            }
        }

        await _hub.Clients.Group(RealtimeTopics.Alerts).PriorityAlerted(message);
    }

    // ---- sending: batched and coalesced --------------------------------------------------------------------

    /// <summary>
    /// Sends everything that has accumulated for the batched and coalesced topics. Called on the flush timer; public
    /// to the test project so a test can flush exactly when it chooses and not race a timer.
    /// </summary>
    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);

        try
        {
            await FlushTrustAsync();
            await FlushRepeatedAlertsAsync();
            await FlushReadingsAsync();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task FlushTrustAsync()
    {
        HashSet<string> dirty;

        lock (_pendingGate)
        {
            if (_dirtyNodes.Count == 0)
            {
                return;
            }

            dirty = _dirtyNodes;
            _dirtyNodes = new HashSet<string>(StringComparer.Ordinal);
        }

        if (!_subscriptions.Has(RealtimeTopics.Trust))
        {
            return;
        }

        // A listener who joined since the last flush arrived with a snapshot of the state at that moment, which
        // says nothing about what this class last sent. Forgetting what it remembers is what stops a node whose
        // score has returned to an old value being judged "unchanged" for someone who never saw that value.
        var generation = _subscriptions.GenerationOf(RealtimeTopics.Trust);

        if (generation != _trustGeneration)
        {
            _lastSentTrust.Clear();
            _trustGeneration = generation;
        }

        var changed = new List<NodeTrustSnapshot>(dirty.Count);

        foreach (var mac in dirty)
        {
            if (!_trust.TryGet(mac, out var snapshot))
            {
                continue;
            }

            if (_lastSentTrust.TryGetValue(mac, out var last) && last.IsCloseTo(snapshot, _options.TrustScoreEpsilon))
            {
                continue;
            }

            _lastSentTrust[mac] = SentTrust.From(snapshot);
            changed.Add(snapshot);
        }

        if (changed.Count == 0)
        {
            return;
        }

        // State read, then numbered, then sent.
        var summary = _trust.Summary();
        var update = new TrustUpdate(NextSequence(RealtimeTopics.Trust), summary, changed);

        await _hub.Clients.Group(RealtimeTopics.Trust).TrustUpdated(update);
    }

    private async Task FlushRepeatedAlertsAsync()
    {
        HashSet<AlertKey> repeated;

        lock (_pendingGate)
        {
            if (_repeatedAlerts.Count == 0)
            {
                return;
            }

            repeated = _repeatedAlerts;
            _repeatedAlerts = [];
        }

        List<ActiveAlert> upserted = [];

        foreach (var key in repeated)
        {
            if (_alerts.TryGet(key, out var alert))
            {
                upserted.Add(alert);
            }
        }

        if (upserted.Count == 0)
        {
            return;
        }

        var stats = _alerts.Stats();
        var update = new AlertUpdate(NextSequence(RealtimeTopics.Alerts), upserted, [], stats);

        await _hub.Clients.Group(RealtimeTopics.Alerts).AlertsUpdated(update);
    }

    private async Task FlushReadingsAsync()
    {
        List<ReadingPush> batch;
        int dropped;

        lock (_pendingGate)
        {
            if (_readings.Count == 0 && _droppedReadings == 0)
            {
                return;
            }

            batch = _readings;
            dropped = _droppedReadings;
            _readings = new List<ReadingPush>(Math.Min(batch.Count + 16, _options.MaxBufferedReadings));
            _droppedReadings = 0;
        }

        if (_subscriptions.Has(RealtimeTopics.Stream))
        {
            await _hub.Clients.Group(RealtimeTopics.Stream)
                .ReadingsArrived(new ReadingsBatch(NextSequence(RealtimeTopics.Stream), batch, dropped));
        }

        if (!_subscriptions.HasAnyDevice)
        {
            return;
        }

        // One message per watched device, carrying only that device's readings.
        var byDevice = batch.ToLookup(reading => reading.MacAddress, StringComparer.Ordinal);

        foreach (var mac in _subscriptions.WatchedDevices)
        {
            var mine = byDevice[mac].ToList();

            if (mine.Count == 0)
            {
                continue;
            }

            var topic = RealtimeTopics.Device(mac);

            await _hub.Clients.Group(topic).ReadingsArrived(new ReadingsBatch(NextSequence(topic), mine, 0));
        }
    }

    /// <summary>Pushes pipeline statistics to anyone listening. Public to the test project for the same reason as flush.</summary>
    internal async Task SendPipelineAsync(CancellationToken cancellationToken = default)
    {
        if (!_subscriptions.Has(RealtimeTopics.Pipeline))
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken);

        try
        {
            var stats = _queue.Stats();

            await _hub.Clients.Group(RealtimeTopics.Pipeline)
                .PipelineUpdated(new PipelineUpdate(NextSequence(RealtimeTopics.Pipeline), stats));
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ---- the loops -----------------------------------------------------------------------------------------

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunImmediateAsync(stoppingToken), RunFlushAsync(stoppingToken), RunPipelineAsync(stoppingToken));

    /// <summary>Sends alerts, command changes and critical readings as soon as the previous send is done.</summary>
    private async Task RunImmediateAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var send in _immediate.Reader.ReadAllAsync(stoppingToken))
            {
                await _sendGate.WaitAsync(stoppingToken);

                try
                {
                    await send();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One failed send must not stop the loop, or every later alert would go unannounced.
                    _logger.LogError(exception, "A real-time message could not be sent. The next one will be tried.");
                }
                finally
                {
                    _sendGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private Task RunFlushAsync(CancellationToken stoppingToken) =>
        RunTimerAsync(_options.FlushInterval, FlushAsync, "flush", stoppingToken);

    private Task RunPipelineAsync(CancellationToken stoppingToken) =>
        RunTimerAsync(_options.PipelineInterval, SendPipelineAsync, "pipeline", stoppingToken);

    private async Task RunTimerAsync(TimeSpan interval, Func<CancellationToken, Task> tick, string name, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await tick(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "The real-time {Name} failed. The next one will be tried.", name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public override void Dispose()
    {
        _trust.NodeEvaluated -= OnNodeEvaluated;
        _alerts.Changed -= OnAlertChanged;
        _commands.Changed -= OnCommandChanged;
        _processed.Processed -= OnReadingProcessed;

        base.Dispose();
    }

    /// <summary>What a node looked like when it was last sent: enough to tell whether it has changed.</summary>
    private readonly record struct SentTrust(double Score, TrustBand Band, string Reason, int RecentSuccesses, int RecentFailures)
    {
        public static SentTrust From(NodeTrustSnapshot node) =>
            new(node.Score, node.Band, node.Reason, node.RecentSuccesses, node.RecentFailures);

        /// <summary>
        /// Whether a node is, for a viewer's purposes, as it was: same band, same reason, same counts, and a score
        /// that has moved by less than the threshold.
        /// </summary>
        public bool IsCloseTo(NodeTrustSnapshot node, double epsilon) =>
            Band == node.Band
            && RecentSuccesses == node.RecentSuccesses
            && RecentFailures == node.RecentFailures
            && string.Equals(Reason, node.Reason, StringComparison.Ordinal)
            && Math.Abs(Score - node.Score) < epsilon;
    }
}
