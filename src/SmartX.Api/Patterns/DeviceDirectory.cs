using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SmartX.Api.Search;
using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>
/// Describes devices to the pattern engine from the device index and the zone tree.
/// </summary>
/// <remarks>
/// <b>Cached, because it is asked on every reading.</b> The trigger detector asks about a device for each packet it
/// processes, and a registration never changes once made, so a profile is worked out once. The zone tree is a gateway
/// configuration that does not change while it runs either; it is handed in once at start-up, and profiles built before it
/// arrived are dropped so they are rebuilt with it.
/// <para>
/// <b>The zone tree is what makes "the same zone" mean something.</b> A device is grouped at the general level under the
/// ancestor of its zone at a configured depth, found by walking up <see cref="DeploymentZone.ParentId"/>. That is the same
/// hierarchy the Part 1 validator walks, so Sub-Zone B and Zone 1 are in the same group and Zone 4 is not.
/// </para>
/// </remarks>
internal sealed class DeviceDirectory(DeviceIndex index, IOptions<PatternOptions> options) : IDeviceDirectory
{
    // The longest chain the walk will follow. A well-formed tree is a few levels deep; the bound is only here so that a
    // malformed one, with a loop in it, ends the walk and not the process.
    private const int MaxZoneDepth = 64;

    private readonly ConcurrentDictionary<string, DeviceProfile> _profiles = new(StringComparer.Ordinal);
    private IZoneCatalogue? _catalogue;

    /// <summary>The zone tree. Set once, when the API starts.</summary>
    public IZoneCatalogue? Catalogue
    {
        get => _catalogue;
        set
        {
            _catalogue = value;
            _profiles.Clear();
        }
    }

    public bool TryDescribe(string macAddress, out DeviceProfile profile)
    {
        if (_profiles.TryGetValue(macAddress, out profile!))
        {
            return true;
        }

        if (!index.TryGetRegistration(macAddress, out var sensor))
        {
            profile = null!;

            return false;
        }

        var (zoneId, zoneName) = GeneralZoneOf(sensor.Location.ZoneId);

        var deviceClass = sensor.ActuatorKind is { } kind ? $"Actuator/{kind}" : $"{sensor.Category}/{sensor.Unit}";
        var label = sensor.ActuatorKind is { } actuator ? actuator.ToString().ToLowerInvariant() : $"{sensor.Unit} sensor";

        profile = new DeviceProfile(
            sensor.MacAddress,
            sensor.Location.Room,
            sensor.Location.ZoneId,
            zoneId,
            zoneName,
            deviceClass,
            label,
            sensor.PayloadKind == TelemetryPayloadKind.State,
            sensor.ExpectedRange.Minimum,
            sensor.ExpectedRange.Maximum);

        _profiles[macAddress] = profile;

        return true;
    }

    // Kept with the number of devices it was worked out from, so a device registering after it was worked out is picked up
    // on the next ask and nothing else forces it to be rebuilt.
    private readonly ConcurrentDictionary<(string Class, string Zone), (int Count, IReadOnlyList<DeviceProfile> Devices)> _similar = new();

    public IReadOnlyList<DeviceProfile> Similar(DeviceProfile device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var count = index.Count;
        var key = (device.Class, device.GeneralZoneId);

        if (_similar.TryGetValue(key, out var cached) && cached.Count == count)
        {
            return cached.Devices;
        }

        var found = new List<DeviceProfile>();

        foreach (var registration in index.Registrations())
        {
            if (TryDescribe(registration.MacAddress, out var profile)
                && profile.Class == device.Class
                && profile.GeneralZoneId == device.GeneralZoneId)
            {
                found.Add(profile);
            }
        }

        found.Sort((left, right) => string.CompareOrdinal(left.MacAddress, right.MacAddress));
        _similar[key] = (count, found);

        return found;
    }

    /// <summary>Walks up from a zone to the ancestor at the general depth, or stops at the zone itself if it is shallower.</summary>
    private (string Id, string Name) GeneralZoneOf(string zoneId)
    {
        if (_catalogue is not { } catalogue || !catalogue.TryFind(zoneId, out var zone))
        {
            return (zoneId, zoneId);
        }

        var depthWanted = options.Value.GeneralZoneDepth;

        for (var step = 0; step < MaxZoneDepth && zone.Depth > depthWanted && zone.ParentId is { } parentId; step++)
        {
            if (!catalogue.TryFind(parentId, out var parent))
            {
                break;
            }

            zone = parent;
        }

        return (zone.Id, zone.Name);
    }
}
