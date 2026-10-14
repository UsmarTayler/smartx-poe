using System.Security.Cryptography;
using System.Text;

namespace SmartX.Shared;

/// <summary>The headers a device sends to say who it is and prove it.</summary>
public static class DeviceAuthHeaders
{
    /// <summary>The device's MAC address.</summary>
    public const string Mac = "X-Device-Mac";

    /// <summary>The device's key, derived from the fleet secret and the MAC.</summary>
    public const string Key = "X-Device-Key";
}

/// <summary>
/// Per-device keys, derived and not stored: <c>key = HMAC-SHA256(fleetSecret, MAC)</c>.
/// </summary>
/// <remarks>
/// This is how Azure IoT Hub's derived device keys work (a group enrolment's master key signs the device's registration id), and
/// for the same reason: the gateway never has to hold a table of keys. It recomputes the key from the secret it already has and
/// compares. Losing one device's key gives an attacker that one device and nothing else, because HMAC cannot be run backwards
/// to the fleet secret; the key does not unlock any other MAC. The secret itself stays on the gateway and in the factory that
/// writes keys into devices, and never travels.
/// <para>
/// Both sides call this one function, which is why it is in the shared project: if they ever disagreed about the MAC's casing or
/// the encoding, every device would be refused and nothing would say why.
/// </para>
/// </remarks>
public static class DeviceKeys
{
    /// <summary>The key for a device, as an unpadded URL-safe base64 string that is safe in a header.</summary>
    public static string Derive(string fleetSecret, string macAddress)
    {
        ArgumentException.ThrowIfNullOrEmpty(fleetSecret);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(fleetSecret), Encoding.UTF8.GetBytes(Canonical(macAddress)));

        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Does this key belong to this MAC under this secret?</summary>
    /// <remarks>Compared in constant time, so the time a refusal takes says nothing about how much of the key was right.</remarks>
    public static bool Verify(string fleetSecret, string macAddress, string? presentedKey)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(Derive(fleetSecret, macAddress));
        var presented = Encoding.UTF8.GetBytes(presentedKey);

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>The MAC as it is signed: upper case, colons. A device that writes it another way still gets the same key.</summary>
    public static string Canonical(string macAddress) =>
        MacAddressValidator.TryNormalise(macAddress, out var canonical) ? canonical : macAddress.Trim().ToUpperInvariant();
}
