using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// What the topology page reads, over HTTP.
/// </summary>
/// <remarks>
/// A seam, like <see cref="IOperatorApi"/>, so the page can be driven by an API that answers exactly what a test says and records what
/// it was asked. Changing a device's scenario (taking it offline) goes through <see cref="IOperatorApi.SetScenarioAsync"/>, as on
/// the other pages.
/// </remarks>
public interface ITopologyApi
{
    Task<ApiResult<TopologyView>> GetTopologyAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<MstView>> GetMstAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<ReachabilityView>> GetReachabilityAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<ArticulationView>> GetArticulationPointsAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<RouteView>> GetRouteAsync(string macAddress, CancellationToken cancellationToken = default);

    Task<ApiResult<HistoryRange>> GetHistoryRangeAsync(
        string macAddress, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default);
}

public sealed class TopologyApi(SmartXApiClient api, ApiConnection connection) : ITopologyApi
{
    public Task<ApiResult<TopologyView>> GetTopologyAsync(CancellationToken cancellationToken = default) =>
        Reported(api.GetTopologyAsync(cancellationToken));

    public Task<ApiResult<MstView>> GetMstAsync(CancellationToken cancellationToken = default) =>
        Reported(api.GetMstAsync(cancellationToken));

    public Task<ApiResult<ReachabilityView>> GetReachabilityAsync(CancellationToken cancellationToken = default) =>
        Reported(api.GetReachabilityAsync(cancellationToken));

    public Task<ApiResult<ArticulationView>> GetArticulationPointsAsync(CancellationToken cancellationToken = default) =>
        Reported(api.GetArticulationPointsAsync(cancellationToken));

    public Task<ApiResult<RouteView>> GetRouteAsync(string macAddress, CancellationToken cancellationToken = default) =>
        Reported(api.GetRouteAsync(macAddress, cancellationToken));

    public Task<ApiResult<HistoryRange>> GetHistoryRangeAsync(
        string macAddress, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default) =>
        Reported(api.GetHistoryRangeAsync(macAddress, from, to, limit, cancellationToken));

    private async Task<ApiResult<T>> Reported<T>(Task<ApiResult<T>> call)
    {
        var result = await call;

        connection.Report(result.Problem is not { IsUnreachable: true });

        return result;
    }
}
