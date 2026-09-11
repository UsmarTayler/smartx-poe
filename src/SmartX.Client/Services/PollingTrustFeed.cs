using SmartX.Client.State;
using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// An <see cref="ITrustFeed"/> that polls the trust endpoints on a fixed interval.
/// </summary>
/// <remarks>
/// Each refresh fetches the summary and the node list concurrently, then every node's history
/// concurrently. That is N + 2 requests per interval — fine for a demonstration mesh, and exactly
/// the cost a push-based feed removes, which is part of why the interface exists.
/// <para>
/// Scoped, so the data outlives the page: leave the trust panel and come back, and the last snapshot
/// renders at once while the next poll is on its way. Polling runs only while the panel is open;
/// there is no reason to spend requests on a view nobody is looking at.
/// </para>
/// </remarks>
public sealed class PollingTrustFeed(
    SmartXApiClient api,
    ApiConnection connection,
    AppState state,
    TimeProvider time,
    IConfiguration configuration) : ITrustFeed
{
    // Three seconds: fast enough that a node visibly drops down the list within moments of the
    // readings that caused it, slow enough that a mesh of a few dozen nodes is not a request storm.
    private const int DefaultIntervalSeconds = 3;

    private CancellationTokenSource? _polling;

    /// <inheritdoc />
    public event Action? Updated;

    /// <inheritdoc />
    public TrustFeedSnapshot Current { get; private set; } = TrustFeedSnapshot.Empty;

    /// <inheritdoc />
    public TimeSpan? RefreshInterval { get; } = TimeSpan.FromSeconds(
        int.TryParse(configuration["TrustFeed:PollIntervalSeconds"], out var seconds) && seconds > 0
            ? seconds
            : DefaultIntervalSeconds);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_polling is not null)
        {
            return;
        }

        _polling = new CancellationTokenSource();

        // The first refresh is awaited, so the panel has data as soon as it can; the loop after it
        // runs on its own. It is a Task that is deliberately not awaited — never an async void — so an
        // exception inside it cannot tear down the application.
        await RefreshNowAsync(cancellationToken);
        _ = PollAsync(RefreshInterval!.Value, _polling.Token);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_polling is null)
        {
            return;
        }

        await _polling.CancelAsync();
        _polling.Dispose();
        _polling = null;
    }

    /// <inheritdoc />
    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        var summaryCall = api.GetTrustSummaryAsync(cancellationToken);
        var nodesCall = api.GetTrustAsync(cancellationToken);

        var summary = await summaryCall;
        var nodes = await nodesCall;

        connection.Report(summary.Problem is not { IsUnreachable: true } && nodes.Problem is not { IsUnreachable: true });

        if (!summary.IsSuccess || !nodes.IsSuccess)
        {
            // Keep the last good data and say why it is stale.
            Current = Current with { Problem = summary.Problem ?? nodes.Problem };
            Updated?.Invoke();

            return;
        }

        var historyCalls = nodes.Value.Select(async node =>
            (node.MacAddress, History: await api.GetTrustHistoryAsync(node.MacAddress, cancellationToken)));

        var histories = new Dictionary<string, IReadOnlyList<TrustHistoryPoint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (macAddress, history) in await Task.WhenAll(historyCalls))
        {
            // One missing sparkline is not a reason to withhold every node's score.
            histories[macAddress] = history.IsSuccess ? history.Value : [];
        }

        var now = time.GetUtcNow();

        Current = new TrustFeedSnapshot(summary.Value, nodes.Value, histories, now, null, IsLoaded: true);
        state.MarkRefreshed(now);
        Updated?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task PollAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // A PeriodicTimer ticks on a fixed schedule, where a delay after each refresh would drift by
        // however long the refresh took.
        using var timer = new PeriodicTimer(interval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshNowAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The panel closed.
        }
    }
}
