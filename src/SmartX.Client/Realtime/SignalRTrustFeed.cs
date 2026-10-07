using SmartX.Client.Services;
using SmartX.Client.State;
using SmartX.Shared;

namespace SmartX.Client.Realtime;

/// <summary>
/// An <see cref="ITrustFeed"/> that is pushed to by the server, and polls when it cannot be.
/// </summary>
/// <remarks>
/// <b>Live.</b> The first state is a snapshot over HTTP, which also says what sequence it is as of. After that the
/// server pushes <see cref="TrustUpdate"/> messages carrying only the nodes that changed, each at its latest state.
/// Each is merged into the nodes held by MAC address and the list is put back in worst-first order with the same
/// comparer the API uses (<see cref="TrustFormula.CompareWorstFirst(NodeTrustSnapshot, NodeTrustSnapshot)"/>), so
/// the list does not reshuffle when a push arrives. Pushes at or below the last sequence applied are discarded; see
/// <see cref="RealtimeFeed{TSnapshot}"/>.
/// <para>
/// <b>Fallback.</b> When the hub cannot be reached at all, this hands over to <see cref="PollingTrustFeed"/>, the
/// feed that existed before push, and copies what it fetches. It becomes the fallback and is not deleted: the
/// dashboard must work whether or not a proxy lets WebSockets through. The moment the connection comes up, polling
/// stops and the feed resynchronises.
/// </para>
/// <para>
/// Scoped and long-lived, like the polling feed before it, so the data outlives the page: leave the panel and come
/// back and the last state renders at once.
/// </para>
/// </remarks>
public sealed class SignalRTrustFeed(
    RealtimeConnection connection,
    IRealtimeSnapshots snapshots,
    PollingTrustFeed polling,
    AppState state,
    ApiConnection api,
    TimeProvider time,
    RealtimeClientOptions options) : RealtimeFeed<TrustSnapshot>(connection, time, options), ITrustFeed
{
    private Dictionary<string, NodeTrustSnapshot> _nodes = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public TrustFeedSnapshot Current { get; private set; } = TrustFeedSnapshot.Empty;

    /// <inheritdoc />
    /// <remarks>Null while pushed. The polling interval only while the hub cannot be reached and the feed is polling.</remarks>
    public TimeSpan? RefreshInterval => IsPolling ? polling.RefreshInterval : null;

    protected override string Topic => RealtimeTopics.Trust;

    protected override async Task<ApiResult<TrustSnapshot>> FetchSnapshotAsync(CancellationToken cancellationToken)
    {
        var result = await snapshots.GetTrustAsync(cancellationToken);

        api.Report(result.Problem is not { IsUnreachable: true });

        return result;
    }

    protected override void ApplySnapshot(TrustSnapshot snapshot)
    {
        _nodes = snapshot.Nodes.ToDictionary(node => node.MacAddress, StringComparer.Ordinal);

        // Sorted here as well as by the API: the snapshot is whatever the server says, but the order a pushed
        // update produces is this client's own, and the two must agree for the list to hold still.
        Publish(snapshot.Summary, [.. snapshot.Nodes]);
    }

    protected override void RegisterHandlers() =>
        Listen<TrustUpdate>(RealtimeMethods.TrustUpdated, update =>
        {
            foreach (var node in update.Nodes)
            {
                _nodes[node.MacAddress] = node;
            }

            Publish(update.Summary, [.. _nodes.Values]);
        });

    protected override void OnFetchFailed(ApiProblem problem) =>
        Current = Current with { Problem = problem };

    private void Publish(TrustSummary summary, List<NodeTrustSnapshot> nodes)
    {
        nodes.Sort(TrustFormula.CompareWorstFirst);

        Current = new TrustFeedSnapshot(summary, nodes, Clock.GetUtcNow(), null, IsLoaded: true);
    }

    /// <summary>Raised after every change, and marks the refresh for the "Updated at" label.</summary>
    protected override void Raise()
    {
        if (Current is { IsLoaded: true, Problem: null, RefreshedAt: { } at })
        {
            state.MarkRefreshed(at);
        }

        base.Raise();
    }

    // ---- fallback: the polling feed that existed before push ---------------------------------------------

    protected override async Task BeginPollingAsync()
    {
        polling.Updated += OnPollingUpdated;

        await polling.StartAsync();
    }

    protected override async Task EndPollingAsync()
    {
        polling.Updated -= OnPollingUpdated;

        await polling.StopAsync();
    }

    private void OnPollingUpdated()
    {
        Current = polling.Current;
        Raise();
    }

    /// <inheritdoc />
    public override async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        if (IsPolling)
        {
            await polling.RefreshNowAsync(cancellationToken);

            return;
        }

        await base.RefreshNowAsync(cancellationToken);
    }
}
