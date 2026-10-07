namespace SmartX.Client.Realtime;

/// <summary>
/// How the client's real-time connection retries, and when it gives up on push for polling.
/// </summary>
/// <remarks>
/// Read from the <c>Realtime</c> section of the client's <c>appsettings.json</c>. Every value has a default, so the
/// section is optional.
/// </remarks>
public sealed class RealtimeClientOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Realtime";

    /// <summary>
    /// The wait before each reconnection attempt after the connection drops. The last value repeats for ever.
    /// </summary>
    /// <remarks>
    /// SignalR's own default stops after four attempts (0, 2, 10 and 30 seconds) and then gives up for good, which
    /// leaves a dashboard on a laptop that slept for an hour permanently offline until someone reloads. This list
    /// never ends: quick at first, because most drops are a moment, then settling at a pace that does not hammer a
    /// server that is down.
    /// </remarks>
    public TimeSpan[] ReconnectDelays { get; set; } =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
    ];

    /// <summary>
    /// How long the connection may stay in the reconnecting state before the feeds fall back to polling.
    /// </summary>
    /// <remarks>
    /// A drop is usually brief, and polling for a few seconds only to switch back would flicker. Past this long, the
    /// dashboard is better off asking for its data than waiting for a push that may not come.
    /// </remarks>
    public TimeSpan ReconnectingFallbackAfter { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often a connection that could not be made at all tries again, while the feeds poll.
    /// </summary>
    /// <remarks>
    /// This is the upgrade path: a proxy that was blocking WebSockets and is fixed, or an API that was not yet up,
    /// is noticed within this interval and the dashboard returns to push without a reload.
    /// </remarks>
    public TimeSpan UpgradeRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often a feed that is polling asks for a fresh snapshot.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a feed waits before trying again when its snapshot could not be fetched.</summary>
    public TimeSpan SnapshotRetryInterval { get; set; } = TimeSpan.FromSeconds(3);
}
