using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Client.Realtime;

/// <summary>
/// Where a feed gets its current state from, over HTTP.
/// </summary>
/// <remarks>
/// A push connection tells a client what changed. It cannot tell it what is, and anything pushed while the
/// connection was down is gone. So every feed starts from a snapshot and layers pushes on top. This is an interface
/// so a test can hold a snapshot back, or hand one over at a chosen moment, which is how the ordering rules are
/// proved against the exact interleavings they exist for.
/// </remarks>
public interface IRealtimeSnapshots
{
    Task<ApiResult<TrustSnapshot>> GetTrustAsync(CancellationToken cancellationToken);

    Task<ApiResult<AlertSnapshot>> GetAlertsAsync(CancellationToken cancellationToken);

    Task<ApiResult<CommandSnapshot>> GetCommandsAsync(CancellationToken cancellationToken);

    Task<ApiResult<PipelineUpdate>> GetPipelineAsync(CancellationToken cancellationToken);

    Task<ApiResult<StreamSnapshot>> GetStreamAsync(CancellationToken cancellationToken);

    Task<ApiResult<DeviceSnapshot>> GetDeviceAsync(string macAddress, CancellationToken cancellationToken);

    Task<ApiResult<SuggestionsUpdate>> GetSuggestionsAsync(CancellationToken cancellationToken);
}

/// <summary><see cref="IRealtimeSnapshots"/> over the API's <c>/api/realtime</c> endpoints.</summary>
public sealed class ApiRealtimeSnapshots(SmartXApiClient api) : IRealtimeSnapshots
{
    public Task<ApiResult<TrustSnapshot>> GetTrustAsync(CancellationToken cancellationToken) =>
        api.GetTrustSnapshotAsync(cancellationToken);

    public Task<ApiResult<AlertSnapshot>> GetAlertsAsync(CancellationToken cancellationToken) =>
        api.GetAlertSnapshotAsync(cancellationToken);

    public Task<ApiResult<CommandSnapshot>> GetCommandsAsync(CancellationToken cancellationToken) =>
        api.GetCommandSnapshotAsync(cancellationToken);

    public Task<ApiResult<PipelineUpdate>> GetPipelineAsync(CancellationToken cancellationToken) =>
        api.GetPipelineSnapshotAsync(cancellationToken);

    public Task<ApiResult<StreamSnapshot>> GetStreamAsync(CancellationToken cancellationToken) =>
        api.GetStreamSnapshotAsync(cancellationToken);

    public Task<ApiResult<DeviceSnapshot>> GetDeviceAsync(string macAddress, CancellationToken cancellationToken) =>
        api.GetDeviceSnapshotAsync(macAddress, cancellationToken);

    public Task<ApiResult<SuggestionsUpdate>> GetSuggestionsAsync(CancellationToken cancellationToken) =>
        api.GetSuggestionsSnapshotAsync(cancellationToken);
}
