using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.Ingestion;
using SmartX.Api.Patterns;
using SmartX.Api.Topology;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Realtime;

/// <summary>
/// Turns the system's domain events into messages for connected clients. The only place that pushes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one broadcaster and not SignalR calls in the domain.</b> The trust engine, alert set, command service and ingestion
/// worker do not reference SignalR. They raise events, as they would with nobody listening, and this class subscribes and
/// forwards through <see cref="IHubContext{THub, T}"/>. The domain stays testable without a hub, and the transport can change
/// again (it already moved from polling to push) without touching code that decides when a node is faulty.
/// </para>
/// <para>
/// <b>Batched, coalesced or immediate</b> is chosen by how often a message happens and what a delay costs. Readings are the
/// highest volume, so they are <b>batched</b>: one message per flush window (250 ms by default). A node's trust score moves on
/// every packet, so it is <b>coalesced</b>: each changed node is sent once per window at its latest state, and only if it moved
/// by more than <see cref="RealtimeOptions.TrustScoreEpsilon"/>. Sending the latest state, not the changes, also makes a lost or
/// reordered message harmless.
/// </para>
/// <para>
/// Alerts, command changes, critical readings and suggestions are <b>immediate</b>: they are rare and they are the point of the
/// system, and holding a critical reading for a batch would undo the priority lane the ingestion queue exists to provide. (A
/// repeat count ticking up on an active alert is a counter, not news, so it is coalesced.) Suggestions are never sent per
/// reading, and each message carries the whole list so a lost one is replaced by the next. Pipeline statistics are a gauge, sent
/// once a second.
/// </para>
/// <para>
/// <b>Order.</b> One loop sends everything, under one lock, so messages leave in the order they were numbered. A message's
/// content is read before its sequence number is taken, and a snapshot reads the sequence before its data, so a snapshot as of N
/// contains everything up to N, which is what lets the client discard a message at or below N.
/// </para>
/// <para>
/// Events for a topic nobody has joined are ignored on entry, so an idle gateway pays a dictionary lookup per event and the
/// buffers cannot grow.
/// </para>
/// </remarks>
internal sealed class RealtimeBroadcaster : BackgroundService
{
    private readonly ITrustEngine _trust;
    private readonly AlertTracker _alerts;
    private readonly CommandService _commands;
    private readonly ProcessedReadings _processed;
    private readonly ISuggestionService _suggestions;
    private readonly InsightService _insights;
    private readonly TopologyService _topology;
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
        ISuggestionService suggestions,
        InsightService insights,
        TopologyService topology,
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
        _suggestions = suggestions;
        _insights = insights;
        _topology = topology;
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
        _suggestions.Changed += OnSuggestionsChanged;
        _insights.Changed += OnSuggestionsChanged;
        _topology.RevisionChanged += OnTopologyChanged;
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

    /// <summary>The suggestions topic's sequence and what the suggestions and insights are, read in that order.</summary>
    public SuggestionsUpdate GetSuggestionsSnapshot()
    {
        // The sequence first, then the state, as for every snapshot: a message numbered at or below it was built from state
        // that is already in what is read next.
        var sequence = SequenceOf(RealtimeTopics.Suggestions);

        return BuildSuggestions(sequence);
    }

    private SuggestionsUpdate BuildSuggestions(long sequence)
    {
        var state = _suggestions.Current;

        return new SuggestionsUpdate(sequence, state.Suggestions, _insights.Current, state.Dismissed, _time.GetUtcNow());
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

    private int _suggestionsQueued;
    private int _topologyQueued;
    private long _lastTopologyChangeSent;

    private void OnTopologyChanged()
    {
        if (!_subscriptions.Has(RealtimeTopics.Topology))
        {
            return;
        }

        // Raised with the topology service's lock held, so this only marks and queues. One message waiting is enough: it is built from
        // the state when it is sent, so a burst of revisions is one message and not one each.
        if (Interlocked.Exchange(ref _topologyQueued, 1) == 0)
        {
            Enqueue(SendTopologyAsync);
        }
    }

    private async Task SendTopologyAsync()
    {
        // Cleared before the state is read, so a revision made after the read but before the send queues a message of its own.
        Interlocked.Exchange(ref _topologyQueued, 0);

        // Read, then numbered, then sent, as for suggestions. The route changes are the ones since the last message went out.
        var snapshot = _topology.Current;
        var changes = _topology.RouteChangesAfter(Interlocked.Read(ref _lastTopologyChangeSent));
        var update = new TopologyUpdate(NextSequence(RealtimeTopics.Topology), snapshot.Revision, snapshot.BuiltAt, [.. changes.Select(change => change.ToView())]);

        if (changes.Count > 0)
        {
            Interlocked.Exchange(ref _lastTopologyChangeSent, changes[0].Id);
        }

        await _hub.Clients.Group(RealtimeTopics.Topology).TopologyUpdated(update);
    }

    /// <summary>The mesh's revision and its recent route changes, with the topology topic's sequence, for a client to start from.</summary>
    public TopologyUpdate GetTopologySnapshot()
    {
        // The sequence first, then the state: a message numbered at or below it was built from state that is already in what is read next.
        var sequence = SequenceOf(RealtimeTopics.Topology);
        var snapshot = _topology.Current;

        return new TopologyUpdate(sequence, snapshot.Revision, snapshot.BuiltAt, [.. _topology.RouteChanges(50).Select(change => change.ToView())]);
    }

    private void OnSuggestionsChanged()
    {
        if (!_subscriptions.Has(RealtimeTopics.Suggestions))
        {
            return;
        }

        // One message waiting is enough: it is built from the state when it is sent, so anything that changes before then is in it.
        // A burst of changes is one message and not one each.
        if (Interlocked.Exchange(ref _suggestionsQueued, 1) == 0)
        {
            Enqueue(SendSuggestionsAsync);
        }
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

    private static ReadingPush ToPush(ProcessedReading reading) => ReadingPush.From(reading.Packet, reading.AcceptedAt);

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

    private async Task SendSuggestionsAsync()
    {
        // Cleared before the state is read, so a change made after the read but before the send queues a message of its own.
        Interlocked.Exchange(ref _suggestionsQueued, 0);

        // Read, then numbered, then sent. Each message is the whole picture, so two changes close together are two messages
        // that agree with the state at the moment each was read, and the later one always wins.
        var update = BuildSuggestions(0);
        update = update with { Sequence = NextSequence(RealtimeTopics.Suggestions) };

        await _hub.Clients.Group(RealtimeTopics.Suggestions).SuggestionsUpdated(update);
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

            await _hub.Clients.Group(topic).ReadingsArrived(new ReadingsBatch(NextSequence(topic), mine, 0, topic));
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
        _suggestions.Changed -= OnSuggestionsChanged;
        _insights.Changed -= OnSuggestionsChanged;

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
