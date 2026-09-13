using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// Everything the trust panel shows, as of one refresh.
/// </summary>
/// <param name="Summary">Mesh-wide confidence and per-band counts.</param>
/// <param name="Nodes">
/// Every tracked node, worst first, as the API ordered them, each with its recent history.
/// </param>
/// <param name="RefreshedAt">When this data arrived.</param>
/// <param name="Problem">Why the most recent refresh failed, if it did.</param>
/// <param name="IsLoaded">Whether any refresh has ever succeeded.</param>
/// <remarks>
/// Immutable, and replaced wholesale on each refresh. A component rendering it can never catch it
/// half-updated — the summary from one poll alongside the node list from the next.
/// <para>
/// A failed refresh keeps the last good data and only sets <see cref="Problem"/>. An operator who
/// loses the API connection still sees the last known state of the mesh, clearly marked as stale,
/// rather than a blank page at the moment they most need the information.
/// </para>
/// </remarks>
public sealed record TrustFeedSnapshot(
    TrustSummary? Summary,
    IReadOnlyList<NodeTrustSnapshot> Nodes,
    DateTimeOffset? RefreshedAt,
    ApiProblem? Problem,
    bool IsLoaded)
{
    public static TrustFeedSnapshot Empty { get; } = new(null, [], null, null, false);
}

/// <summary>
/// A live source of trust data for the panel.
/// </summary>
/// <remarks>
/// <b>The seam for Part 2.</b> The panel's components know only this interface: they read
/// <see cref="Current"/> and re-render on <see cref="Updated"/>. Today the implementation polls the
/// REST endpoints on a timer, which is simple and easy to demonstrate. When it becomes a SignalR
/// subscription the server pushes changes instead, <see cref="RefreshInterval"/> becomes null, and
/// not one component has to change — which is only true because none of them ever learned that
/// polling was happening.
/// </remarks>
public interface ITrustFeed : IAsyncDisposable
{
    /// <summary>Raised whenever <see cref="Current"/> is replaced.</summary>
    event Action? Updated;

    /// <summary>The latest data. Kept after the feed stops, so a return visit renders immediately.</summary>
    TrustFeedSnapshot Current { get; }

    /// <summary>How often data is pulled, or null when it is pushed.</summary>
    TimeSpan? RefreshInterval { get; }

    /// <summary>Begins delivering updates. Calling it while already running does nothing.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops delivering updates, keeping <see cref="Current"/>.</summary>
    Task StopAsync();

    /// <summary>Fetches fresh data now, outside the normal cadence.</summary>
    Task RefreshNowAsync(CancellationToken cancellationToken = default);
}
