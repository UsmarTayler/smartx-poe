using System.Collections.Concurrent;
using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// Holds the desired state for each device: the device-shadow half of device management.
/// </summary>
/// <remarks>
/// An operator changes what a device <i>should</i> do; the device finds out on its next poll and applies it.
/// The API never pushes, and never holds a connection open to a device, which is what makes the pattern work
/// for hardware that sleeps, roams or sits behind a NAT.
/// </remarks>
public interface IDeviceShadowStore
{
    /// <summary>Records the state an operator wants, and returns it with its new version.</summary>
    Task<DeviceDesiredState> SetAsync(string macAddress, SimulationScenario scenario, CancellationToken cancellationToken = default);

    /// <summary>The desired state for one device, or null when nothing has been asked of it.</summary>
    Task<DeviceDesiredState?> FindAsync(string macAddress, CancellationToken cancellationToken = default);

    /// <summary>Every device with a desired state, ordered by MAC.</summary>
    Task<IReadOnlyList<DeviceDesiredState>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Keeps desired state in memory for the life of the process.</summary>
/// <remarks>
/// Losing the shadow on restart is survivable, and honest: a restarted gateway has no instructions
/// outstanding, and each device carries its own default behaviour until an operator asks for something else.
/// <para>
/// Keyed on the canonical MAC, like the sensor registry and the trust engine, so the three agree on what
/// counts as the same device however the address was typed.
/// </para>
/// </remarks>
internal sealed class InMemoryDeviceShadowStore(TimeProvider time) : IDeviceShadowStore
{
    private readonly ConcurrentDictionary<string, DeviceDesiredState> _desired = new(StringComparer.Ordinal);

    public Task<DeviceDesiredState> SetAsync(
        string macAddress,
        SimulationScenario scenario,
        CancellationToken cancellationToken = default)
    {
        var mac = MacAddressValidator.Normalise(macAddress);

        // AddOrUpdate applies the version bump atomically, so two operators changing one device at the same
        // moment cannot both read version 4 and both write version 5.
        var state = _desired.AddOrUpdate(
            mac,
            _ => new DeviceDesiredState(mac, scenario, 1, time.GetUtcNow()),
            (_, existing) => existing with
            {
                Scenario = scenario,
                Version = existing.Version + 1,
                UpdatedAt = time.GetUtcNow(),
            });

        return Task.FromResult(state);
    }

    public Task<DeviceDesiredState?> FindAsync(string macAddress, CancellationToken cancellationToken = default)
    {
        if (!MacAddressValidator.TryNormalise(macAddress, out var mac))
        {
            return Task.FromResult<DeviceDesiredState?>(null);
        }

        return Task.FromResult(_desired.TryGetValue(mac, out var state) ? state : null);
    }

    public Task<IReadOnlyList<DeviceDesiredState>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DeviceDesiredState> snapshot =
            [.. _desired.Values.OrderBy(state => state.MacAddress, StringComparer.Ordinal)];

        return Task.FromResult(snapshot);
    }
}
