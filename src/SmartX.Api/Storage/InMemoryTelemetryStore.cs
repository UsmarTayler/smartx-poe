using System.Collections.Concurrent;
using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// Holds a bounded sliding window of recent telemetry per node.
/// </summary>
/// <remarks>
/// Partitioned by node, so two devices reporting at once touch different buffers rather than
/// contending on one shared collection, and a per-node query never scans another node's readings.
/// <para>
/// Each partition is a <see cref="TelemetryRingBuffer{T}"/> rather than a growing list. That is
/// what makes retention a property of the structure instead of a chore: the buffer holds the last
/// <see cref="DefaultRetentionPerNode"/> readings and overwrites the oldest in place, at O(1), with
/// no trimming pass and no reallocation. The store previously grew without bound, which was fine
/// for a demonstration and would have been a leak in anything longer-lived.
/// </para>
/// </remarks>
internal sealed class InMemoryTelemetryStore : ITelemetryStore
{
    /// <summary>
    /// Readings retained per node before the oldest is overwritten.
    /// </summary>
    /// <remarks>
    /// 256 is enough history for a dashboard to draw a trend and for the trust scoring in a later
    /// part to weigh recent behaviour, while keeping the whole store bounded at a predictable size:
    /// nodes × 256 references, fixed at startup, regardless of how long the process runs.
    /// </remarks>
    internal const int DefaultRetentionPerNode = 256;

    private readonly ConcurrentDictionary<string, NodeHistory> _byNode = new(StringComparer.Ordinal);
    private readonly int _retentionPerNode;

    /// <summary>Creates the store.</summary>
    /// <param name="retentionPerNode">Readings to retain per node.</param>
    public InMemoryTelemetryStore(int retentionPerNode = DefaultRetentionPerNode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionPerNode);

        _retentionPerNode = retentionPerNode;
    }

    /// <inheritdoc />
    public Task AddAsync(ITelemetryPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // The factory takes the capacity as an argument rather than capturing it, so the delegate
        // is static and no closure object is allocated per call on the ingestion hot path.
        _byNode
            .GetOrAdd(packet.NodeId, static (_, capacity) => new NodeHistory(capacity), _retentionPerNode)
            .Add(packet);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_byNode.Values.Sum(history => history.Count));

    /// <inheritdoc />
    public Task<int> CountForAsync(string nodeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryFind(nodeId, out var history) ? history.Count : 0);

    /// <inheritdoc />
    public Task<IReadOnlyList<ITelemetryPacket>> RecentAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ITelemetryPacket> snapshot =
            TryFind(nodeId, out var history) ? history.Snapshot() : [];

        return Task.FromResult(snapshot);
    }

    private bool TryFind(string nodeId, out NodeHistory history)
    {
        if (MacAddressValidator.TryNormalise(nodeId, out var canonical))
        {
            return _byNode.TryGetValue(canonical, out history!);
        }

        history = null!;

        return false;
    }

    /// <summary>
    /// One node's sliding window, with the serialisation the buffer itself does not provide.
    /// </summary>
    /// <remarks>
    /// <see cref="TelemetryRingBuffer{T}"/> is deliberately not thread-safe — locking inside it
    /// would charge every single-threaded caller for synchronisation it does not need. The store is
    /// where concurrency actually happens, so the lock lives here, and it is per node rather than
    /// global so that ingestion from one device never blocks another.
    /// <para>
    /// <see cref="Lock"/> rather than locking on a plain field: it is the purpose-built type for
    /// this, and it avoids taking a monitor on an object that something else could also lock.
    /// </para>
    /// </remarks>
    private sealed class NodeHistory(int capacity)
    {
        private readonly Lock _gate = new();
        private readonly TelemetryRingBuffer<ITelemetryPacket> _readings = new(capacity);

        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    return _readings.Count;
                }
            }
        }

        internal void Add(ITelemetryPacket packet)
        {
            lock (_gate)
            {
                _readings.Add(packet);
            }
        }

        /// <summary>Copies the window out, oldest first.</summary>
        /// <remarks>
        /// Copied under the lock, so a response can never observe the buffer mid-overwrite. The
        /// list that leaves here is the caller's own.
        /// </remarks>
        internal List<ITelemetryPacket> Snapshot()
        {
            lock (_gate)
            {
                return _readings.ToList();
            }
        }
    }
}
