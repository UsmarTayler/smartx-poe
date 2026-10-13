using System.Diagnostics;
using SmartX.Shared;

namespace SmartX.Api.Search;

/// <summary>
/// Secondary indexes over the devices, so a search is a set intersection and never a scan.
/// </summary>
/// <remarks>
/// <b>Why indexes.</b> "Find the valves that are faulted in Zone 3" asked of a plain list is a loop over every
/// device checking each condition in turn, and the loop gets slower with every device the mesh gains. Here
/// each condition is answered ahead of time by a set: the devices in each category, of each actuator kind, in
/// each trust band, with each kind of alert, in each zone. A query then picks the sets its filters name and
/// intersects them. <see cref="HashSet{T}.IntersectWith"/> costs about the size of the smaller set, so a search
/// for "valves in the fault band" does not touch a thermometer.
/// <para>
/// <b>Smallest first.</b> The sets are intersected starting from the smallest. Intersection cost is driven by
/// the set being whittled down, so beginning with the rarest condition does the least work, and an empty
/// result — the common case for "pumps in fault" on a healthy mesh — is reached after one set.
/// </para>
/// <para>
/// <b>Kept in step, in one lock.</b> An index that disagrees with reality is worse than no index, because it
/// returns confident wrong answers. So the primary facts about each device (its band and its active alerts)
/// live in <see cref="DeviceEntry"/>, every index is derived from them, and every change updates the fact and
/// the index sets together inside <see cref="Gate"/>. The alert tracker takes the same lock for its own
/// changes, so an alert appearing and the device becoming findable by it are one step that no query can see
/// half of. <see cref="FindInconsistencies"/> rebuilds every index from the facts and reports any difference,
/// which is what the consistency test runs after random changes.
/// </para>
/// <para>
/// <b>What still scans.</b> The text filter. Matching "pump" against a MAC or a name cannot be answered by a
/// set, so it runs last, over only the devices that survived every other filter. With no other filter it scans
/// everything, which is the honest cost of a free-text search.
/// </para>
/// <para>
/// <b>Zones.</b> Devices are indexed by the zone they are deployed in. A search for a zone must also find
/// devices in every zone beneath it, so the search asks the Part 1 recursive walk
/// (<see cref="ZoneTree.ZoneIdsBeneath"/>) which zones those are and unions their sets. The walk is over zones,
/// of which there are a handful, and not over devices.
/// </para>
/// </remarks>
internal sealed class DeviceIndex
{
    private readonly Dictionary<string, DeviceEntry> _devices = new(StringComparer.Ordinal);

    // Every set below uses the same comparer. HashSet.IntersectWith takes a fast path, probing the other set
    // directly, only when both sets agree on how to compare; with different comparers it falls back to
    // enumerating the other set, which would make the cost depend on the larger one.
    private readonly Dictionary<SensorCategory, HashSet<string>> _byCategory = NewIndex<SensorCategory>();
    private readonly Dictionary<ActuatorKind, HashSet<string>> _byActuatorKind = NewIndex<ActuatorKind>();
    private readonly Dictionary<TrustBand, HashSet<string>> _byBand = NewIndex<TrustBand>();
    private readonly Dictionary<AlertKind, HashSet<string>> _byAlert = NewIndex<AlertKind>();
    private readonly Dictionary<string, HashSet<string>> _byZone = new(StringComparer.OrdinalIgnoreCase);

    // A band change can arrive for a device whose registration has not been indexed yet: the first reading can
    // be processed in the moment between the registry accepting the sensor and the endpoint indexing it. The
    // change is held here and applied when the device registers, instead of being lost.
    private readonly Dictionary<string, TrustBand> _pendingBands = new(StringComparer.Ordinal);

    /// <summary>
    /// The lock every change and every query takes. Shared with the alert tracker so alert state and the
    /// alert index move together.
    /// </summary>
    internal Lock Gate { get; } = new();

    /// <summary>How many devices are indexed.</summary>
    public int Count
    {
        get
        {
            lock (Gate)
            {
                return _devices.Count;
            }
        }
    }

    /// <summary>Every registration, as a snapshot. For callers that group devices in ways the filters do not.</summary>
    public IReadOnlyList<SensorRegistration> Registrations()
    {
        lock (Gate)
        {
            return [.. _devices.Values.Select(entry => entry.Registration)];
        }
    }

    /// <summary>Indexes a newly registered device.</summary>
    /// <returns><see langword="false"/> when the device is already indexed, in which case nothing changes.</returns>
    public bool Register(SensorRegistration sensor, TrustBand band = TrustBand.Unproven)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        lock (Gate)
        {
            if (_devices.ContainsKey(sensor.MacAddress))
            {
                return false;
            }

            if (_pendingBands.Remove(sensor.MacAddress, out var pending))
            {
                band = pending;
            }

            _devices.Add(sensor.MacAddress, new DeviceEntry(sensor, band));

            _byCategory[sensor.Category].Add(sensor.MacAddress);
            _byBand[band].Add(sensor.MacAddress);

            if (sensor.ActuatorKind is { } kind)
            {
                _byActuatorKind[kind].Add(sensor.MacAddress);
            }

            if (!_byZone.TryGetValue(sensor.Location.ZoneId, out var inZone))
            {
                inZone = new HashSet<string>(StringComparer.Ordinal);
                _byZone.Add(sensor.Location.ZoneId, inZone);
            }

            inZone.Add(sensor.MacAddress);

            return true;
        }
    }

    /// <summary>Moves a device to a new trust band.</summary>
    public void SetBand(string macAddress, TrustBand band)
    {
        lock (Gate)
        {
            if (!_devices.TryGetValue(macAddress, out var entry))
            {
                _pendingBands[macAddress] = band;

                return;
            }

            if (entry.Band == band)
            {
                return;
            }

            _byBand[entry.Band].Remove(macAddress);
            _byBand[band].Add(macAddress);
            entry.Band = band;
        }
    }

    /// <summary>Records that a device has, or no longer has, an active alert of a kind.</summary>
    /// <remarks>
    /// Caller must hold <see cref="Gate"/>. The alert tracker calls this from inside its own critical section so
    /// that the set of active alerts and this index are never seen apart.
    /// </remarks>
    internal void SetAlertLocked(string macAddress, AlertKind kind, bool active)
    {
        Debug.Assert(Gate.IsHeldByCurrentThread, "SetAlertLocked must be called with the gate held.");

        // An alert for a device that is not indexed (never registered) is still tracked by the alert tracker;
        // there is just nothing here to make findable.
        if (!_devices.TryGetValue(macAddress, out var entry))
        {
            return;
        }

        if (active)
        {
            entry.Alerts.Add(kind);
            _byAlert[kind].Add(macAddress);
        }
        else
        {
            entry.Alerts.Remove(kind);
            _byAlert[kind].Remove(macAddress);
        }
    }

    /// <summary>The room a device was registered in, when it is indexed. Caller must hold <see cref="Gate"/>.</summary>
    internal string? RoomOfLocked(string macAddress) =>
        _devices.TryGetValue(macAddress, out var entry) ? entry.Registration.Location.Room : null;

    /// <summary>The registration an indexed device was registered with.</summary>
    /// <returns><see langword="false"/> when the device is not indexed.</returns>
    public bool TryGetRegistration(string macAddress, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SensorRegistration? registration)
    {
        lock (Gate)
        {
            if (_devices.TryGetValue(macAddress, out var entry))
            {
                registration = entry.Registration;

                return true;
            }
        }

        registration = null;

        return false;
    }

    /// <summary>The trust band an indexed device is in now.</summary>
    public TrustBand? BandOf(string macAddress)
    {
        lock (Gate)
        {
            return _devices.TryGetValue(macAddress, out var entry) ? entry.Band : null;
        }
    }

    /// <summary>
    /// Finds the devices matching every filter that was given.
    /// </summary>
    /// <param name="filters">What to match. A member left null does not constrain the result.</param>
    /// <param name="catalogue">The zone hierarchy, used only when a zone filter was given.</param>
    /// <returns>The matches, ordered by MAC address.</returns>
    public IReadOnlyList<DeviceSearchHit> Search(DeviceSearchFilters filters, IZoneCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(catalogue);

        lock (Gate)
        {
            // The sets the filters name, by reference. Nothing here is copied until intersection starts.
            var narrowing = new List<HashSet<string>>(5);

            if (filters.Category is { } category)
            {
                narrowing.Add(_byCategory[category]);
            }

            if (filters.ActuatorKind is { } kind)
            {
                narrowing.Add(_byActuatorKind[kind]);
            }

            if (filters.Band is { } band)
            {
                narrowing.Add(_byBand[band]);
            }

            if (filters.Alert is { } alert)
            {
                narrowing.Add(_byAlert[alert]);
            }

            if (!string.IsNullOrWhiteSpace(filters.Zone))
            {
                narrowing.Add(DevicesInOrBeneath(filters.Zone, catalogue));
            }

            IEnumerable<string> candidates;

            if (narrowing.Count == 0)
            {
                candidates = _devices.Keys;
            }
            else
            {
                // Smallest set first, copied, then every other set is intersected into the copy. The copy is
                // sized by the smallest set, so the work done is bounded by the rarest condition asked for.
                narrowing.Sort(static (left, right) => left.Count.CompareTo(right.Count));

                var survivors = new HashSet<string>(narrowing[0], StringComparer.Ordinal);

                for (var i = 1; i < narrowing.Count && survivors.Count > 0; i++)
                {
                    survivors.IntersectWith(narrowing[i]);
                }

                candidates = survivors;
            }

            // The only scan, and only over what is left.
            var text = filters.Q?.Trim();
            var matches = new List<DeviceSearchHit>();

            foreach (var mac in candidates)
            {
                var entry = _devices[mac];

                if (!string.IsNullOrEmpty(text) && !Matches(entry.Registration, text))
                {
                    continue;
                }

                matches.Add(ToHit(entry));
            }

            matches.Sort(static (left, right) => string.CompareOrdinal(left.MacAddress, right.MacAddress));

            return matches;
        }
    }

    /// <summary>
    /// Compares every index with what it should be, derived from the facts about each device, and describes any
    /// difference.
    /// </summary>
    /// <returns>An empty list when every index is exactly right.</returns>
    /// <remarks>
    /// The check that makes "kept in step" testable instead of asserted. It rebuilds each index from scratch the
    /// slow, obviously correct way and compares.
    /// </remarks>
    internal IReadOnlyList<string> FindInconsistencies()
    {
        lock (Gate)
        {
            var problems = new List<string>();

            Compare("category", _byCategory, entry => [entry.Registration.Category], problems);
            Compare("actuator kind", _byActuatorKind, entry => entry.Registration.ActuatorKind is { } k ? [k] : [], problems);
            Compare("band", _byBand, entry => [entry.Band], problems);
            Compare("alert", _byAlert, entry => entry.Alerts, problems);

            var expectedZones = _devices.Values
                .GroupBy(entry => entry.Registration.Location.ZoneId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.Registration.MacAddress).ToHashSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);

            foreach (var (zone, macs) in _byZone)
            {
                var expected = expectedZones.GetValueOrDefault(zone) ?? [];

                if (!macs.SetEquals(expected))
                {
                    problems.Add($"zone '{zone}' index holds [{string.Join(", ", macs.Order())}] but should hold [{string.Join(", ", expected.Order())}]");
                }
            }

            foreach (var zone in expectedZones.Keys.Where(zone => !_byZone.ContainsKey(zone)))
            {
                problems.Add($"zone '{zone}' has devices but no index entry");
            }

            return problems;
        }

        void Compare<TKey>(
            string name,
            Dictionary<TKey, HashSet<string>> index,
            Func<DeviceEntry, IEnumerable<TKey>> keysOf,
            List<string> problems)
            where TKey : notnull
        {
            var expected = new Dictionary<TKey, HashSet<string>>();

            foreach (var entry in _devices.Values)
            {
                foreach (var key in keysOf(entry))
                {
                    if (!expected.TryGetValue(key, out var macs))
                    {
                        macs = new HashSet<string>(StringComparer.Ordinal);
                        expected.Add(key, macs);
                    }

                    macs.Add(entry.Registration.MacAddress);
                }
            }

            foreach (var (key, macs) in index)
            {
                var wanted = expected.GetValueOrDefault(key) ?? [];

                if (!macs.SetEquals(wanted))
                {
                    problems.Add($"{name} index [{key}] holds [{string.Join(", ", macs.Order())}] but should hold [{string.Join(", ", wanted.Order())}]");
                }
            }
        }
    }

    /// <summary>The devices in a zone and in every zone beneath it, as one set.</summary>
    private HashSet<string> DevicesInOrBeneath(string zoneId, IZoneCatalogue catalogue)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in catalogue.ZoneIdsBeneath(zoneId))
        {
            if (_byZone.TryGetValue(id, out var inZone))
            {
                union.UnionWith(inZone);
            }
        }

        return union;
    }

    private static bool Matches(SensorRegistration sensor, string text) =>
        sensor.MacAddress.Contains(text, StringComparison.OrdinalIgnoreCase)
        || sensor.Location.NodeId.Contains(text, StringComparison.OrdinalIgnoreCase)
        || sensor.Location.Room.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static DeviceSearchHit ToHit(DeviceEntry entry) => new(
        entry.Registration.MacAddress,
        entry.Registration.Location.Room,
        entry.Registration.Location.NodeId,
        entry.Registration.Location.ZoneId,
        entry.Registration.Category,
        entry.Registration.ActuatorKind,
        entry.Band,
        [.. entry.Alerts.Order()]);

    /// <summary>One empty set per member of the enum, so a lookup never has to ask whether the set exists.</summary>
    private static Dictionary<TEnum, HashSet<string>> NewIndex<TEnum>()
        where TEnum : struct, Enum
    {
        var index = new Dictionary<TEnum, HashSet<string>>();

        foreach (var value in Enum.GetValues<TEnum>())
        {
            index.Add(value, new HashSet<string>(StringComparer.Ordinal));
        }

        return index;
    }

    /// <summary>The facts about one device that the index sets are derived from.</summary>
    private sealed class DeviceEntry(SensorRegistration registration, TrustBand band)
    {
        public SensorRegistration Registration { get; } = registration;

        public TrustBand Band { get; set; } = band;

        public HashSet<AlertKind> Alerts { get; } = [];
    }
}
