using System.Collections.Concurrent;
using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// Holds accepted telemetry in memory for the lifetime of the process.
/// </summary>
/// <remarks>
/// Partitioned by node rather than kept as one flat list. Telemetry is written constantly and read
/// per device, so a single shared collection would make every ingestion contend with every other,
/// and every later per-node query a full scan. Keying by node means two devices reporting at once
/// touch different queues.
/// <para>
/// This grows without bound, which is correct for a process-lifetime store backing a demonstration
/// and would not be for a real deployment. Retention belongs with the durable store that replaces
/// this one, not bolted onto a dictionary.
/// </para>
/// </remarks>
internal sealed class InMemoryTelemetryStore : ITelemetryStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ITelemetryPacket>> _byNode =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task AddAsync(ITelemetryPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        _byNode.GetOrAdd(packet.NodeId, _ => new ConcurrentQueue<ITelemetryPacket>()).Enqueue(packet);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_byNode.Values.Sum(queue => queue.Count));

    /// <inheritdoc />
    public Task<int> CountForAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        if (!MacAddressValidator.TryNormalise(nodeId, out var canonical))
        {
            return Task.FromResult(0);
        }

        return Task.FromResult(_byNode.TryGetValue(canonical, out var queue) ? queue.Count : 0);
    }
}
