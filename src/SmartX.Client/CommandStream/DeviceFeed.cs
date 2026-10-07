using SmartX.Client.Realtime;
using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>One device's recent readings, as of the last snapshot or push.</summary>
/// <param name="MacAddress">The device.</param>
/// <param name="Recent">Its latest readings, oldest first. Bounded.</param>
/// <param name="RefreshedAt">When this was last brought up to date.</param>
/// <param name="Problem">Why the last attempt to fetch it failed, if it did.</param>
/// <param name="IsLoaded">Whether a snapshot has arrived.</param>
public sealed record DeviceFeedState(
    string MacAddress,
    IReadOnlyList<ReadingPush> Recent,
    DateTimeOffset? RefreshedAt,
    ApiProblem? Problem,
    bool IsLoaded);

/// <summary>
/// A feed of one device's readings, built on the app's one connection, that joins <c>device:{mac}</c> while it is open.
/// </summary>
/// <remarks>
/// One instance per device the panel shows, created when the user opens a device and disposed when they open another or
/// leave. It follows the same rule as every feed (join, snapshot, then pushes newer than the snapshot), so the timeline
/// chart can extend live without a second source of truth. It is not shared: a device nobody is looking at has no
/// subscription and costs the server nothing.
/// </remarks>
public sealed class DeviceFeed(
    string macAddress,
    RealtimeConnection connection,
    IRealtimeSnapshots snapshots,
    TimeProvider time,
    RealtimeClientOptions options) : RealtimeFeed<DeviceSnapshot>(connection, time, options)
{
    /// <summary>The most readings kept. The timeline's own buckets hold the long view; this is only the live edge.</summary>
    public const int Capacity = 360;

    /// <summary>The device this feed follows.</summary>
    public string MacAddress { get; } = macAddress;

    /// <summary>The latest readings.</summary>
    public DeviceFeedState Current { get; private set; } = new(macAddress, [], null, null, false);

    protected override string Topic => RealtimeTopics.Device(MacAddress);

    protected override Task<ApiResult<DeviceSnapshot>> FetchSnapshotAsync(CancellationToken cancellationToken) =>
        snapshots.GetDeviceAsync(MacAddress, cancellationToken);

    protected override void ApplySnapshot(DeviceSnapshot snapshot) =>
        Current = new DeviceFeedState(MacAddress, Bounded(snapshot.Readings), Clock.GetUtcNow(), null, true);

    protected override void RegisterHandlers() =>
        Listen<ReadingsBatch>(
            RealtimeMethods.ReadingsArrived,
            batch => Current = Current with
            {
                Recent = Bounded([.. Current.Recent, .. batch.Readings]),
                RefreshedAt = Clock.GetUtcNow(),
                Problem = null,
                IsLoaded = true,
            },
            // The stream topic shares this client method. Its batches are not this device's, and their sequence
            // numbers mean nothing here.
            accept: batch => batch.Topic == Topic);

    protected override void OnFetchFailed(ApiProblem problem) => Current = Current with { Problem = problem };

    private static IReadOnlyList<ReadingPush> Bounded(IReadOnlyList<ReadingPush> readings) =>
        readings.Count <= Capacity ? readings : [.. readings.Skip(readings.Count - Capacity)];
}

/// <summary>Makes a feed for one device.</summary>
public interface IDeviceFeedFactory
{
    /// <summary>A new feed for the device. The caller starts it, and disposes it when done.</summary>
    DeviceFeed Create(string macAddress);
}

/// <summary><see cref="IDeviceFeedFactory"/> over the app's one connection.</summary>
public sealed class DeviceFeedFactory(
    RealtimeConnection connection,
    IRealtimeSnapshots snapshots,
    TimeProvider time,
    RealtimeClientOptions options) : IDeviceFeedFactory
{
    public DeviceFeed Create(string macAddress) => new(macAddress, connection, snapshots, time, options);
}
