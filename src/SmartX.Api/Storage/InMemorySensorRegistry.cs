using System.Collections.Concurrent;
using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// Holds registered sensors in memory for the lifetime of the process.
/// </summary>
/// <remarks>
/// A <see cref="ConcurrentDictionary{TKey, TValue}"/> rather than a plain dictionary behind a lock:
/// the registry is a singleton served by whichever thread ASP.NET Core assigns to a request, so
/// concurrent registrations are the normal case rather than an edge one.
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/> also settles the duplicate-MAC race
/// atomically — checking for an existing key and then adding would leave a window where two
/// simultaneous registrations of the same device both pass the check.
/// <para>
/// Keyed ordinally on the canonical MAC. <see cref="SensorRegistration"/> normalises on
/// construction, so every key is already uppercase and colon-separated and a case-insensitive
/// comparer would only hide a normalisation bug.
/// </para>
/// </remarks>
internal sealed class InMemorySensorRegistry : ISensorRegistry
{
    private readonly ConcurrentDictionary<string, SensorRegistration> _sensors =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IReadOnlyList<SensorRegistration>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SensorRegistration> snapshot =
            [.. _sensors.Values.OrderBy(sensor => sensor.MacAddress, StringComparer.Ordinal)];

        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task<SensorRegistration?> FindAsync(string macAddress, CancellationToken cancellationToken = default)
    {
        // Callers supply whatever the URL contained, so the lookup key is canonicalised the same
        // way the stored key was. Without this, /api/sensors/aa:bb:... would 404 against a sensor
        // registered as AA:BB:...
        if (!MacAddressValidator.TryNormalise(macAddress, out var canonical))
        {
            return Task.FromResult<SensorRegistration?>(null);
        }

        return Task.FromResult(_sensors.TryGetValue(canonical, out var sensor) ? sensor : null);
    }

    /// <inheritdoc />
    public Task<bool> TryAddAsync(SensorRegistration sensor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        return Task.FromResult(_sensors.TryAdd(sensor.MacAddress, sensor));
    }
}
