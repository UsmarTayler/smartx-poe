using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// Where accepted telemetry packets are kept.
/// </summary>
/// <remarks>
/// Takes <see cref="ITelemetryPacket"/> rather than a generic parameter, because a single store has
/// to hold readings of every payload type at once and
/// <c>NumericPacket&lt;int&gt;</c> and <c>NumericPacket&lt;float&gt;</c> are unrelated constructed
/// types with no common base but that interface. Holding a reference to an already-allocated packet
/// through an interface costs nothing; it is the payload inside that must not be boxed, and it is
/// not — see <see cref="ITelemetryPacket"/>.
/// </remarks>
public interface ITelemetryStore
{
    /// <summary>Records an accepted packet.</summary>
    Task AddAsync(ITelemetryPacket packet, CancellationToken cancellationToken = default);

    /// <summary>How many packets have been accepted.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>How many packets have been accepted from one node.</summary>
    Task<int> CountForAsync(string nodeId, CancellationToken cancellationToken = default);
}
