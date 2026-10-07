using System.Collections.Concurrent;
using System.Collections.Immutable;
using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Client.Realtime;

// The feeds other than trust. Each is the same shape as ITrustFeed: a current state, a change event, and Start and
// Stop that a page calls when it opens and when it is disposed. What is different is only what each one holds and
// how it merges a push, which is all that is written here; joining, snapshotting, the sequence gate and the
// fallback are RealtimeFeed's.

// ---- alerts ---------------------------------------------------------------------------------------------------

/// <summary>The alerts that are on, as of the last snapshot or push.</summary>
/// <param name="Alerts">Active alerts, oldest first.</param>
/// <param name="Stats">Raised, suppressed and cleared totals.</param>
/// <param name="RecentPriority">The latest critical readings, oldest first.</param>
/// <param name="RefreshedAt">When this was last brought up to date.</param>
/// <param name="Problem">Why the last attempt to fetch it failed, if it did.</param>
/// <param name="IsLoaded">Whether any snapshot has ever arrived.</param>
public sealed record AlertFeedState(
    IReadOnlyList<ActiveAlert> Alerts,
    AlertStats Stats,
    IReadOnlyList<PriorityAlert> RecentPriority,
    DateTimeOffset? RefreshedAt,
    ApiProblem? Problem,
    bool IsLoaded)
{
    public static AlertFeedState Empty { get; } = new([], new AlertStats(0, 0, 0, 0, 0), [], null, null, false);
}

/// <summary>A live view of the active alerts and critical readings.</summary>
public interface IAlertFeed : IFeed
{
    /// <summary>The latest state. Kept after the feed stops, so a return visit renders at once.</summary>
    AlertFeedState Current { get; }

    /// <summary>Raised for each critical reading as it is announced, so a page can draw attention to it.</summary>
    event Action<PriorityAlert>? PriorityAlertReceived;
}

/// <summary><see cref="IAlertFeed"/> over the alerts topic.</summary>
public sealed class AlertFeed(
    RealtimeConnection connection,
    IRealtimeSnapshots snapshots,
    TimeProvider time,
    RealtimeClientOptions options) : RealtimeFeed<AlertSnapshot>(connection, time, options), IAlertFeed
{
    private readonly int _recentLimit = 20;
    private Dictionary<AlertKey, ActiveAlert> _alerts = [];

    /// <inheritdoc />
    public event Action<PriorityAlert>? PriorityAlertReceived;

    /// <inheritdoc />
    public AlertFeedState Current { get; private set; } = AlertFeedState.Empty;

    protected override string Topic => RealtimeTopics.Alerts;

    protected override Task<ApiResult<AlertSnapshot>> FetchSnapshotAsync(CancellationToken cancellationToken) =>
        snapshots.GetAlertsAsync(cancellationToken);

    protected override void ApplySnapshot(AlertSnapshot snapshot)
    {
        _alerts = snapshot.Alerts.ToDictionary(alert => alert.Key);
        Current = new AlertFeedState(Ordered(), snapshot.Stats, snapshot.RecentPriority, Clock.GetUtcNow(), null, true);
    }

    protected override void RegisterHandlers()
    {
        // Alert updates and critical readings share one topic and one sequence, so a critical reading is applied
        // or discarded by the same rule as everything else.
        Listen<AlertUpdate>(RealtimeMethods.AlertsUpdated, update =>
        {
            foreach (var key in update.Cleared)
            {
                _alerts.Remove(key);
            }

            foreach (var alert in update.Upserted)
            {
                _alerts[alert.Key] = alert;
            }

            Current = Current with { Alerts = Ordered(), Stats = update.Stats, RefreshedAt = Clock.GetUtcNow(), Problem = null, IsLoaded = true };
        });

        Listen<PriorityAlert>(RealtimeMethods.PriorityAlerted, alert =>
        {
            var recent = Current.RecentPriority.Append(alert).TakeLast(_recentLimit).ToList();

            Current = Current with { RecentPriority = recent, RefreshedAt = Clock.GetUtcNow() };
            _pendingPriority.Enqueue(alert);
        });
    }

    // A queue and not a single slot: two pushes can be applied before the first has been announced, and a slot
    // would lose one of them.
    private readonly ConcurrentQueue<PriorityAlert> _pendingPriority = new();

    protected override void OnFetchFailed(ApiProblem problem) => Current = Current with { Problem = problem };

    protected override void Raise()
    {
        base.Raise();

        while (_pendingPriority.TryDequeue(out var priority))
        {
            PriorityAlertReceived?.Invoke(priority);
        }
    }

    // The order the API uses: oldest first, then by MAC, then by kind, so the list does not move on a refresh.
    private List<ActiveAlert> Ordered() =>
        [.. _alerts.Values.OrderBy(alert => alert.RaisedAt).ThenBy(alert => alert.Key.Mac, StringComparer.Ordinal).ThenBy(alert => alert.Key.Kind)];
}

// ---- commands -------------------------------------------------------------------------------------------------

/// <summary>Recent commands, as of the last snapshot or push.</summary>
/// <param name="Commands">Newest first.</param>
/// <param name="RefreshedAt">When this was last brought up to date.</param>
/// <param name="Problem">Why the last attempt to fetch it failed, if it did.</param>
/// <param name="IsLoaded">Whether any snapshot has ever arrived.</param>
public sealed record CommandFeedState(
    IReadOnlyList<Command> Commands,
    DateTimeOffset? RefreshedAt,
    ApiProblem? Problem,
    bool IsLoaded)
{
    public static CommandFeedState Empty { get; } = new([], null, null, false);
}

/// <summary>A live view of commands and what is happening to them.</summary>
public interface ICommandFeed : IFeed
{
    /// <summary>The latest state. Kept after the feed stops.</summary>
    CommandFeedState Current { get; }
}

/// <summary><see cref="ICommandFeed"/> over the commands topic.</summary>
public sealed class CommandFeed(
    RealtimeConnection connection,
    IRealtimeSnapshots snapshots,
    TimeProvider time,
    RealtimeClientOptions options) : RealtimeFeed<CommandSnapshot>(connection, time, options), ICommandFeed
{
    // As many as the snapshot carries. A command history that grows without bound in a browser tab is the same
    // fault as one that grows without bound on a server.
    private const int Limit = 100;

    private Dictionary<Guid, Command> _commands = [];

    /// <inheritdoc />
    public CommandFeedState Current { get; private set; } = CommandFeedState.Empty;

    protected override string Topic => RealtimeTopics.Commands;

    protected override Task<ApiResult<CommandSnapshot>> FetchSnapshotAsync(CancellationToken cancellationToken) =>
        snapshots.GetCommandsAsync(cancellationToken);

    protected override void ApplySnapshot(CommandSnapshot snapshot)
    {
        _commands = snapshot.Commands.ToDictionary(command => command.Id);
        Current = new CommandFeedState(Ordered(), Clock.GetUtcNow(), null, true);
    }

    protected override void RegisterHandlers() =>
        Listen<CommandUpdate>(RealtimeMethods.CommandChanged, update =>
        {
            _commands[update.Command.Id] = update.Command;

            if (_commands.Count > Limit)
            {
                foreach (var oldest in _commands.Values.OrderBy(command => command.IssuedAt).Take(_commands.Count - Limit).ToList())
                {
                    _commands.Remove(oldest.Id);
                }
            }

            Current = new CommandFeedState(Ordered(), Clock.GetUtcNow(), null, true);
        });

    protected override void OnFetchFailed(ApiProblem problem) => Current = Current with { Problem = problem };

    private List<Command> Ordered() =>
        [.. _commands.Values.OrderByDescending(command => command.IssuedAt).ThenByDescending(command => command.Id)];
}

// ---- the reading stream ---------------------------------------------------------------------------------------

/// <summary>The latest reading from every node, as of the last snapshot or push.</summary>
/// <param name="Latest">The most recent reading from each node that has reported, by MAC address.</param>
/// <param name="ReadingsReceived">How many readings pushes have delivered since the snapshot.</param>
/// <param name="Dropped">How many readings the server reported dropping because a window held too many.</param>
/// <param name="RefreshedAt">When this was last brought up to date.</param>
/// <param name="Problem">Why the last attempt to fetch it failed, if it did.</param>
/// <param name="IsLoaded">Whether any snapshot has ever arrived.</param>
public sealed record TelemetryStreamState(
    ImmutableDictionary<string, ReadingPush> Latest,
    long ReadingsReceived,
    long Dropped,
    DateTimeOffset? RefreshedAt,
    ApiProblem? Problem,
    bool IsLoaded)
{
    public static TelemetryStreamState Empty { get; } =
        new(ImmutableDictionary<string, ReadingPush>.Empty.WithComparers(StringComparer.Ordinal), 0, 0, null, null, false);
}

/// <summary>A live view of readings as they are accepted.</summary>
public interface ITelemetryStream : IFeed
{
    /// <summary>The latest reading from each node. Kept after the feed stops.</summary>
    TelemetryStreamState Current { get; }

    /// <summary>
    /// Raised with each batch of readings, after it has been applied, for a page that wants every reading and not
    /// only the latest per node.
    /// </summary>
    event Action<ReadingsBatch>? BatchReceived;
}

/// <summary><see cref="ITelemetryStream"/> over the stream topic.</summary>
public sealed class TelemetryStream(
    RealtimeConnection connection,
    IRealtimeSnapshots snapshots,
    TimeProvider time,
    RealtimeClientOptions options) : RealtimeFeed<StreamSnapshot>(connection, time, options), ITelemetryStream
{
    // A queue and not a single slot, for the same reason: a consumer that wants every reading must get every batch.
    private readonly ConcurrentQueue<ReadingsBatch> _pendingBatches = new();

    /// <inheritdoc />
    public event Action<ReadingsBatch>? BatchReceived;

    /// <inheritdoc />
    public TelemetryStreamState Current { get; private set; } = TelemetryStreamState.Empty;

    protected override string Topic => RealtimeTopics.Stream;

    protected override Task<ApiResult<StreamSnapshot>> FetchSnapshotAsync(CancellationToken cancellationToken) =>
        snapshots.GetStreamAsync(cancellationToken);

    protected override void ApplySnapshot(StreamSnapshot snapshot) =>
        Current = new TelemetryStreamState(
            snapshot.Latest.ToImmutableDictionary(reading => reading.MacAddress, StringComparer.Ordinal),
            0,
            0,
            Clock.GetUtcNow(),
            null,
            true);

    protected override void RegisterHandlers() =>
        Listen<ReadingsBatch>(RealtimeMethods.ReadingsArrived, batch =>
        {
            // SetItems on an immutable dictionary costs the size of the batch, not the size of the mesh, and
            // hands readers a whole new dictionary, so a page rendering the old one never sees it half-changed.
            var latest = Current.Latest.SetItems(
                batch.Readings.Select(reading => KeyValuePair.Create(reading.MacAddress, reading)));

            Current = Current with
            {
                Latest = latest,
                ReadingsReceived = Current.ReadingsReceived + batch.Readings.Count,
                Dropped = Current.Dropped + batch.Dropped,
                RefreshedAt = Clock.GetUtcNow(),
            };

            _pendingBatches.Enqueue(batch);
        });

    protected override void OnFetchFailed(ApiProblem problem) => Current = Current with { Problem = problem };

    protected override void Raise()
    {
        base.Raise();

        while (_pendingBatches.TryDequeue(out var batch))
        {
            BatchReceived?.Invoke(batch);
        }
    }
}

// ---- the pipeline ---------------------------------------------------------------------------------------------

/// <summary>The ingestion pipeline's statistics, as of the last snapshot or push.</summary>
/// <param name="Stats">Depth, throughput and wait per lane. Null until the first snapshot.</param>
/// <param name="RefreshedAt">When this was last brought up to date.</param>
/// <param name="Problem">Why the last attempt to fetch it failed, if it did.</param>
/// <param name="IsLoaded">Whether any snapshot has ever arrived.</param>
public sealed record PipelineFeedState(PipelineStats? Stats, DateTimeOffset? RefreshedAt, ApiProblem? Problem, bool IsLoaded)
{
    public static PipelineFeedState Empty { get; } = new(null, null, null, false);
}

/// <summary>A live view of the ingestion pipeline.</summary>
public interface IPipelineFeed : IFeed
{
    /// <summary>The latest statistics. Kept after the feed stops.</summary>
    PipelineFeedState Current { get; }
}

/// <summary><see cref="IPipelineFeed"/> over the pipeline topic.</summary>
public sealed class PipelineFeed(
    RealtimeConnection connection,
    IRealtimeSnapshots snapshots,
    TimeProvider time,
    RealtimeClientOptions options) : RealtimeFeed<PipelineUpdate>(connection, time, options), IPipelineFeed
{
    /// <inheritdoc />
    public PipelineFeedState Current { get; private set; } = PipelineFeedState.Empty;

    protected override string Topic => RealtimeTopics.Pipeline;

    protected override Task<ApiResult<PipelineUpdate>> FetchSnapshotAsync(CancellationToken cancellationToken) =>
        snapshots.GetPipelineAsync(cancellationToken);

    protected override void ApplySnapshot(PipelineUpdate snapshot) =>
        Current = new PipelineFeedState(snapshot.Stats, Clock.GetUtcNow(), null, true);

    protected override void RegisterHandlers() =>
        Listen<PipelineUpdate>(RealtimeMethods.PipelineUpdated, update =>
            Current = new PipelineFeedState(update.Stats, Clock.GetUtcNow(), null, true));

    protected override void OnFetchFailed(ApiProblem problem) => Current = Current with { Problem = problem };
}
