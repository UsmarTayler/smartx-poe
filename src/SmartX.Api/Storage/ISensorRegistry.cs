using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// The set of sensors the gateway knows about.
/// </summary>
/// <remarks>
/// An interface over what is currently a dictionary, so that the in-memory store can be swapped for
/// a database without touching an endpoint.
/// <para>
/// Every member is asynchronous even though the in-memory implementation completes synchronously.
/// That is the point: a synchronous interface would force every caller to be rewritten when the
/// store becomes a database, and the usual shortcut at that moment — blocking on the new async call
/// with <c>.Result</c> — deadlocks under load. Committing to the async shape now costs a
/// <see cref="Task.FromResult{TResult}(TResult)"/> per call and nothing else.
/// </para>
/// </remarks>
public interface ISensorRegistry
{
    /// <summary>Every registered sensor, ordered by MAC.</summary>
    Task<IReadOnlyList<SensorRegistration>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds one sensor by MAC, in any accepted format.</summary>
    /// <returns><see langword="null"/> when no sensor carries that address.</returns>
    Task<SensorRegistration?> FindAsync(string macAddress, CancellationToken cancellationToken = default);

    /// <summary>Registers a sensor if its MAC is not already taken.</summary>
    /// <returns><see langword="false"/> when a sensor with that MAC is already registered.</returns>
    /// <remarks>
    /// Returns a flag rather than throwing, because a duplicate registration is an ordinary thing
    /// for a client to attempt — a device retrying after a dropped response — and the endpoint
    /// answers it with 409 rather than treating it as a fault.
    /// </remarks>
    Task<bool> TryAddAsync(SensorRegistration sensor, CancellationToken cancellationToken = default);
}
