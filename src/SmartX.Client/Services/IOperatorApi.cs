using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// Everything the operator's page asks of the API: search, timelines, commands, undo, alerts and the activity record.
/// </summary>
/// <remarks>
/// The components on the Command Stream page do not call <c>HttpClient</c>; services do. This is the seam between
/// the two, an interface so that a component test can give a page an API that answers exactly what the test says, and
/// can count what it was asked. Each call reports whether the API was reachable, so the connection indicator reflects
/// this page's traffic as well.
/// </remarks>
public interface IOperatorApi
{
    Task<ApiResult<DeviceSearchResponse>> SearchDevicesAsync(DeviceSearchFilters filters, CancellationToken cancellationToken = default);

    Task<ApiResult<SensorTimeline>> GetTimelineAsync(
        string macAddress, DateTimeOffset from, DateTimeOffset to, int maxPoints, CancellationToken cancellationToken = default);

    Task<ApiResult<Command>> IssueCommandAsync(string macAddress, CommandAction action, double? level, CancellationToken cancellationToken = default);

    Task<ApiResult<ZonePreview>> PreviewZoneAsync(string zoneId, CommandAction action, double? level, CancellationToken cancellationToken = default);

    Task<ApiResult<CompositeCommandResult>> IssueZoneCommandAsync(
        string zoneId, CommandAction action, double? level, CancellationToken cancellationToken = default);

    Task<ApiResult<IReadOnlyList<UndoStackEntry>>> GetUndoStackAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<UndoResult>> UndoAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<CommandLimits>> GetCommandLimitsAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<ActiveAlert>> AcknowledgeAlertAsync(AlertKey key, CancellationToken cancellationToken = default);

    Task<ApiResult<DeviceDesiredState>> SetScenarioAsync(string macAddress, SimulationScenario scenario, CancellationToken cancellationToken = default);

    Task<ApiResult<IReadOnlyList<ActivityEntry>>> GetActivityAsync(CancellationToken cancellationToken = default);

    /// <summary>Reports that the operator opened a device. Null on success, otherwise why it failed.</summary>
    Task<ApiProblem?> RecordSelectionAsync(string macAddress, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IOperatorApi"/> over <see cref="SmartXApiClient"/>.</summary>
public sealed class OperatorApi(SmartXApiClient api, ApiConnection connection) : IOperatorApi
{
    public Task<ApiResult<DeviceSearchResponse>> SearchDevicesAsync(DeviceSearchFilters filters, CancellationToken cancellationToken = default) =>
        Reported(api.SearchDevicesAsync(filters, cancellationToken));

    public Task<ApiResult<SensorTimeline>> GetTimelineAsync(
        string macAddress, DateTimeOffset from, DateTimeOffset to, int maxPoints, CancellationToken cancellationToken = default) =>
        Reported(api.GetTimelineAsync(macAddress, from, to, maxPoints, cancellationToken));

    public Task<ApiResult<Command>> IssueCommandAsync(string macAddress, CommandAction action, double? level, CancellationToken cancellationToken = default) =>
        Reported(api.IssueCommandAsync(new IssueCommandRequest(macAddress, action, level), cancellationToken));

    public Task<ApiResult<ZonePreview>> PreviewZoneAsync(string zoneId, CommandAction action, double? level, CancellationToken cancellationToken = default) =>
        Reported(api.PreviewZoneCommandAsync(zoneId, action, level, cancellationToken));

    public Task<ApiResult<CompositeCommandResult>> IssueZoneCommandAsync(
        string zoneId, CommandAction action, double? level, CancellationToken cancellationToken = default) =>
        Reported(api.IssueZoneCommandAsync(new ZoneCommandRequest(zoneId, action, level), cancellationToken));

    public Task<ApiResult<IReadOnlyList<UndoStackEntry>>> GetUndoStackAsync(CancellationToken cancellationToken = default) =>
        Reported(api.GetUndoStackAsync(cancellationToken));

    public Task<ApiResult<UndoResult>> UndoAsync(CancellationToken cancellationToken = default) =>
        Reported(api.UndoAsync(cancellationToken));

    public Task<ApiResult<CommandLimits>> GetCommandLimitsAsync(CancellationToken cancellationToken = default) =>
        Reported(api.GetCommandLimitsAsync(cancellationToken));

    public Task<ApiResult<ActiveAlert>> AcknowledgeAlertAsync(AlertKey key, CancellationToken cancellationToken = default) =>
        Reported(api.AcknowledgeAlertAsync(key, cancellationToken));

    public Task<ApiResult<DeviceDesiredState>> SetScenarioAsync(string macAddress, SimulationScenario scenario, CancellationToken cancellationToken = default) =>
        Reported(api.SetScenarioAsync(macAddress, scenario, cancellationToken));

    public Task<ApiResult<IReadOnlyList<ActivityEntry>>> GetActivityAsync(CancellationToken cancellationToken = default) =>
        Reported(api.GetActivityAsync(cancellationToken: cancellationToken));

    public async Task<ApiProblem?> RecordSelectionAsync(string macAddress, CancellationToken cancellationToken = default)
    {
        var problem = await api.RecordSelectionAsync(macAddress, cancellationToken);

        connection.Report(problem is not { IsUnreachable: true });

        return problem;
    }

    /// <summary>Passes a result through, telling the connection indicator whether the API answered.</summary>
    private async Task<ApiResult<T>> Reported<T>(Task<ApiResult<T>> call)
    {
        var result = await call;

        connection.Report(result.Problem is not { IsUnreachable: true });

        return result;
    }
}
