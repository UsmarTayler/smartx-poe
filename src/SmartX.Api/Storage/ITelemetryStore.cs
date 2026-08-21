using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// Where accepted telemetry packets are kept.
/// </summary>
/// <remarks>
/// Takes <see cref="ITelemetryPacket"/> rather than a generic parameter, because a single store has
/// to hold readings of every payload type at once and <c>NumericPacket&lt;int&gt;</c> and
/// <c>NumericPacket&lt;float&gt;</c> are unrelated constructed types with no common base but that
/// interface. Holding a reference to an already-allocated packet through an interface costs
/// nothing; it is the payload inside that must not be boxed, and it is not — see
/// <see cref="ITelemetryPacket"/>.
/// <para>
/// Retention is bounded per node, so a reading accepted long enough ago is no longer here. That is
/// a property of the store rather than a caller's responsibility.
/// </para>
/// </remarks>
public interface ITelemetryStore
{
    /// <summary>Records an accepted packet.</summary>
    Task AddAsync(ITelemetryPacket packet, CancellationToken cancellationToken = default);

    /// <summary>How many packets are currently retained across every node.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>How many packets are currently retained for one node.</summary>
    Task<int> CountForAsync(string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A snapshot of one node's retained readings, oldest first.
    /// </summary>
    /// <remarks>
    /// The final stage of the pipeline: a copy, taken so that a response is never serialising a
    /// buffer that ingestion is still writing into. Empty for a node with no retained readings,
    /// rather than null — an absent node and a silent one are the same thing to a caller.
    /// </remarks>
    Task<IReadOnlyList<ITelemetryPacket>> RecentAsync(
        string nodeId,
        CancellationToken cancellationToken = default);
}
