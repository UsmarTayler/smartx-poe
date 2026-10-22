using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Client.Realtime;

/// <summary>What the topology feed holds: which revision of the mesh is current, and the routes that have healed lately.</summary>
/// <param name="Revision">
/// The mesh's revision. The page compares it with the one it drew and asks for the mesh again when it differs; the feed itself holds
/// no mesh, so a push stays small however big the network is.
/// </param>
/// <param name="BuiltAt">When that revision was built.</param>
/// <param name="Changes">Route changes, newest first, without repeats, at most <see cref="TopologyFeed.Limit"/>.</param>
/// <param name="RefreshedAt">When the feed last heard anything.</param>
/// <param name="Problem">Why the last snapshot failed, if it did.</param>
/// <param name="IsLoaded">Whether a snapshot has arrived.</param>
public sealed record TopologyFeedState(
    long Revision,
    DateTimeOffset? BuiltAt,
    IReadOnlyList<RouteChangeView> Changes,
    DateTimeOffset? RefreshedAt,
    ApiProblem? Problem,
    bool IsLoaded)
{
    public static TopologyFeedState Empty { get; } = new(0, null, [], null, null, false);
}

public interface ITopologyFeed : IFeed
{
    TopologyFeedState Current { get; }
}

/// <summary>
/// The mesh, as a feed: the same snapshot-then-stream shape as the others, carrying a revision number and the route changes.
/// </summary>
/// <remarks>
/// The snapshot and the pushes are both <see cref="TopologyUpdate"/>, so the sequence gate in <see cref="RealtimeFeed{TSnapshot}"/>
/// settles which is newer without anything particular to topology. A change can be in both a snapshot and a push that arrives
/// just after it (the snapshot reads the changes after taking its sequence number), so changes are merged by their id and a
/// repeat is dropped.
/// </remarks>
public sealed class TopologyFeed(
    RealtimeConnection connection,
    IRealtimeSnapshots snapshots,
    TimeProvider time,
    RealtimeClientOptions options) : RealtimeFeed<TopologyUpdate>(connection, time, options), ITopologyFeed
{
    /// <summary>How many route changes are kept. A feed that grows without bound in a browser tab is the same fault as on a server.</summary>
    public const int Limit = 50;

    public TopologyFeedState Current { get; private set; } = TopologyFeedState.Empty;

    protected override string Topic => RealtimeTopics.Topology;

    protected override Task<ApiResult<TopologyUpdate>> FetchSnapshotAsync(CancellationToken cancellationToken) =>
        snapshots.GetTopologyAsync(cancellationToken);

    protected override void ApplySnapshot(TopologyUpdate snapshot) =>
        Current = new TopologyFeedState(snapshot.Revision, snapshot.BuiltAt, Merge([], snapshot.NewChanges), Clock.GetUtcNow(), null, true);

    protected override void RegisterHandlers() =>
        Listen<TopologyUpdate>(RealtimeMethods.TopologyUpdated, update =>
            Current = new TopologyFeedState(update.Revision, update.BuiltAt, Merge(Current.Changes, update.NewChanges), Clock.GetUtcNow(), null, true));

    protected override void OnFetchFailed(ApiProblem problem) => Current = Current with { Problem = problem };

    private static List<RouteChangeView> Merge(IReadOnlyList<RouteChangeView> held, IReadOnlyList<RouteChangeView> arriving) =>
        [.. arriving.Concat(held)
            .GroupBy(change => change.Id)
            .Select(group => group.First())
            .OrderByDescending(change => change.Id)
            .Take(Limit)];
}
