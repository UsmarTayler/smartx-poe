using SmartX.Shared;

namespace SmartX.DeviceSimulator;

/// <summary>
/// What a device would have burned into it at the factory: its identity and the key that proves it. Here, one secret and a
/// function, because the simulator is a whole fleet.
/// </summary>
/// <remarks>
/// A real device holds only its own key. The simulator holds the fleet secret and derives each device's key on demand
/// (<see cref="DeviceKeys"/>), which is the same derivation the gateway checks, and which a real fleet's factory would run once per
/// device. Each request carries the device's MAC and key and nothing else: no operator token, no shared password. If the
/// simulator holds no secret it signs nothing, which is also what a device with no key would do; the gateway answers 401.
/// </remarks>
internal sealed class DeviceKeyring(string? fleetSecret)
{
    /// <summary>Whether there is a secret to sign with.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(fleetSecret);

    /// <summary>Puts the device's identity and key on a request.</summary>
    public void Sign(HttpRequestMessage request, string macAddress)
    {
        if (!IsConfigured)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(DeviceAuthHeaders.Mac, DeviceKeys.Canonical(macAddress));
        request.Headers.TryAddWithoutValidation(DeviceAuthHeaders.Key, DeviceKeys.Derive(fleetSecret!, macAddress));
    }
}
