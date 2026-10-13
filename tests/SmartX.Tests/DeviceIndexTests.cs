using System.Diagnostics;
using SmartX.Api.Alerts;
using SmartX.Api.Search;
using SmartX.Api.Storage;
using SmartX.Shared;
using Xunit.Abstractions;

namespace SmartX.Tests;

/// <summary>
/// Device search: each filter alone, filters combined, zones that include their sub-zones, and indexes that
/// never drift from the truth.
/// </summary>
/// <remarks>
/// Uses the real demo fleet and the real demo hierarchy, so zone filtering runs over a tree three levels deep
/// with siblings, and the categories, actuator kinds and zones are the ones an operator would actually see.
/// </remarks>
public class DeviceIndexTests(ITestOutputHelper output)
{
    private static readonly IZoneCatalogue Catalogue = new ZoneCatalogue(DemoZoneHierarchy.Zones);

    private static string Mac(byte suffix) => $"B8:27:EB:10:00:{suffix:X2}";

    private sealed class World
    {
        public required MeshTestHost Mesh { get; init; }

        public required IReadOnlyList<SensorRegistration> Sensors { get; init; }

        public DeviceIndex Index => Mesh.Index;

        public static World Create()
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
            var mesh = MeshTestHost.Create(clock);

            var sensors = DemoDevices.All
                .Select(device =>
                {
                    Assert.True(device.Registration.TryCreateRegistration(out var registration, out _));

                    return registration;
                })
                .ToList();

            foreach (var sensor in sensors)
            {
                mesh.Index.Register(sensor);
            }

            return new World { Mesh = mesh, Sensors = sensors };
        }

        public IReadOnlyList<string> Find(DeviceSearchFilters filters) =>
            [.. Index.Search(filters, Catalogue).Select(hit => hit.MacAddress)];

        public void Fault(byte suffix) => Index.SetBand(Mac(suffix), TrustBand.Fault);
    }

    // ---- each filter alone -----------------------------------------------------------------------------------

    [Fact]
    public void With_no_filter_every_device_is_returned_in_mac_order()
    {
        var world = World.Create();

        var all = world.Find(new DeviceSearchFilters());

        Assert.Equal(DemoDevices.All.Count, all.Count);
        Assert.Equal(all.Order(StringComparer.Ordinal), all);
    }

    [Theory]
    [InlineData(SensorCategory.Actuator, 22)]
    [InlineData(SensorCategory.PowerConsumption, 12)]
    [InlineData(SensorCategory.Environmental, 21)]
    public void The_category_filter_returns_that_category_only(SensorCategory category, int expected)
    {
        var world = World.Create();

        var found = world.Index.Search(new DeviceSearchFilters(Category: category), Catalogue);

        Assert.Equal(expected, found.Count);
        Assert.All(found, hit => Assert.Equal(category, hit.Category));
    }

    [Theory]
    [InlineData(ActuatorKind.Pump, 6)]
    [InlineData(ActuatorKind.Relay, 5)]
    [InlineData(ActuatorKind.Valve, 6)]
    [InlineData(ActuatorKind.Dimmer, 5)]
    public void The_actuator_kind_filter_returns_that_kind_only(ActuatorKind kind, int expected)
    {
        var world = World.Create();

        var found = world.Index.Search(new DeviceSearchFilters(ActuatorKind: kind), Catalogue);

        Assert.Equal(expected, found.Count);
        Assert.All(found, hit => Assert.Equal(kind, hit.ActuatorKind));
    }

    [Fact]
    public void The_band_filter_returns_devices_in_that_band_and_follows_them_as_they_move()
    {
        var world = World.Create();

        // A newly registered device is unproven, so all of them start there.
        Assert.Equal(DemoDevices.All.Count, world.Find(new DeviceSearchFilters(Band: TrustBand.Unproven)).Count);
        Assert.Empty(world.Find(new DeviceSearchFilters(Band: TrustBand.Fault)));

        world.Fault(0x19);
        world.Index.SetBand(Mac(0x1A), TrustBand.Degrading);

        Assert.Equal([Mac(0x19)], world.Find(new DeviceSearchFilters(Band: TrustBand.Fault)));
        Assert.Equal([Mac(0x1A)], world.Find(new DeviceSearchFilters(Band: TrustBand.Degrading)));
        Assert.Equal(DemoDevices.All.Count - 2, world.Find(new DeviceSearchFilters(Band: TrustBand.Unproven)).Count);

        // And back again.
        world.Index.SetBand(Mac(0x19), TrustBand.Nominal);

        Assert.Empty(world.Find(new DeviceSearchFilters(Band: TrustBand.Fault)));
        Assert.Equal([Mac(0x19)], world.Find(new DeviceSearchFilters(Band: TrustBand.Nominal)));
    }

    [Fact]
    public void The_alert_filter_returns_devices_with_that_alert_active_and_not_after_it_clears()
    {
        var world = World.Create();

        world.Mesh.Alerts.Raise(new AlertKey(Mac(0x0B), AlertKind.Disconnected));
        world.Mesh.Alerts.Raise(new AlertKey(Mac(0x20), AlertKind.Disconnected));
        world.Mesh.Alerts.Raise(new AlertKey(Mac(0x0B), AlertKind.OutOfRange));

        Assert.Equal([Mac(0x0B), Mac(0x20)], world.Find(new DeviceSearchFilters(Alert: AlertKind.Disconnected)));
        Assert.Equal([Mac(0x0B)], world.Find(new DeviceSearchFilters(Alert: AlertKind.OutOfRange)));

        world.Mesh.Alerts.Clear(new AlertKey(Mac(0x0B), AlertKind.Disconnected));

        Assert.Equal([Mac(0x20)], world.Find(new DeviceSearchFilters(Alert: AlertKind.Disconnected)));
    }

    [Fact]
    public void A_hit_reports_the_alerts_the_device_has_now()
    {
        var world = World.Create();
        world.Mesh.Alerts.Raise(new AlertKey(Mac(0x0B), AlertKind.OutOfRange));
        world.Mesh.Alerts.Raise(new AlertKey(Mac(0x0B), AlertKind.Disconnected));

        var hit = Assert.Single(world.Index.Search(new DeviceSearchFilters(Q: Mac(0x0B)), Catalogue));

        Assert.Equal([AlertKind.Disconnected, AlertKind.OutOfRange], hit.ActiveAlerts);
        Assert.Equal("Line 2 Conveyor", hit.Room);
        Assert.Equal("sub-zone-c", hit.ZoneId);
    }

    [Fact]
    public void The_zone_filter_includes_devices_in_every_sub_zone_beneath_it()
    {
        var world = World.Create();

        // Zone 3 holds nothing directly. Its devices are in the freezer and chiller bays beneath it, so a flat
        // filter on the zone id would find none of them.
        var zone3 = world.Find(new DeviceSearchFilters(Zone: "zone-3"));

        Assert.Equal(
            [Mac(0x17), Mac(0x18), Mac(0x19), Mac(0x1A), Mac(0x1B), Mac(0x1C), Mac(0x29), Mac(0x2A), Mac(0x2B), Mac(0x2C), Mac(0x2D)],
            zone3);

        // A leaf zone finds only its own devices.
        Assert.Equal([Mac(0x17), Mac(0x18), Mac(0x19), Mac(0x2A), Mac(0x2D)], world.Find(new DeviceSearchFilters(Zone: "sub-zone-f")));
    }

    [Fact]
    public void A_facility_reaches_through_two_levels_of_zones()
    {
        var world = World.Create();

        var facilityB = world.Find(new DeviceSearchFilters(Zone: "facility-b"));

        // Zone 4, Zone 5 and the hydroponics bay (Zone 6) directly, plus Sub-Zone H, J and K beneath them.
        Assert.Contains(Mac(0x21), facilityB);
        Assert.Contains(Mac(0x2E), facilityB);
        Assert.Contains(Mac(0x1D), facilityB);
        Assert.DoesNotContain(Mac(0x19), facilityB);
        Assert.Equal(DemoDevices.All.Count(device => device.Registration.ZoneId is "zone-4" or "sub-zone-h" or "zone-5" or "sub-zone-j" or "sub-zone-k" or "zone-6"), facilityB.Count);
    }

    [Fact]
    public void The_zone_filter_ignores_case()
    {
        var world = World.Create();

        Assert.Equal(world.Find(new DeviceSearchFilters(Zone: "zone-3")), world.Find(new DeviceSearchFilters(Zone: "ZONE-3")));
    }

    [Fact]
    public void An_unknown_zone_finds_nothing()
    {
        var world = World.Create();

        Assert.Empty(world.Find(new DeviceSearchFilters(Zone: "zone-99")));
    }

    [Theory]
    [InlineData("conveyor", 2)]
    [InlineData("CONVEYOR", 2)]
    [InlineData("pump", 6)]
    [InlineData("GW-B2", 6)]
    [InlineData("10:00:2B", 1)]
    [InlineData("no such thing", 0)]
    public void The_text_filter_matches_the_mac_the_room_and_the_node_id_ignoring_case(string text, int expected)
    {
        var world = World.Create();

        Assert.Equal(expected, world.Find(new DeviceSearchFilters(Q: text)).Count);
    }

    // ---- combined -------------------------------------------------------------------------------------------

    [Fact]
    public void Filters_combine_with_and()
    {
        var world = World.Create();

        world.Fault(0x19);   // freezer valve, zone 3
        world.Fault(0x21);   // dock drain valve, facility B
        world.Fault(0x1A);   // a chiller thermometer, zone 3 but not an actuator

        var found = world.Find(new DeviceSearchFilters(
            Category: SensorCategory.Actuator,
            Zone: "zone-3",
            Band: TrustBand.Fault));

        // Faulted, an actuator, and under Zone 3: only the freezer valve is all three.
        Assert.Equal([Mac(0x19)], found);
    }

    [Fact]
    public void Category_band_and_zone_together_return_exactly_the_devices_meeting_all_three()
    {
        var world = World.Create();

        foreach (var suffix in new byte[] { 0x17, 0x1A, 0x19, 0x21, 0x2B })
        {
            world.Fault(suffix);
        }

        var found = world.Find(new DeviceSearchFilters(
            Category: SensorCategory.Environmental,
            Zone: "facility-a",
            Band: TrustBand.Fault));

        // 0x17 and 0x1A are environmental, faulted and in facility A. 0x19 and 0x2B are faulted but actuators,
        // and 0x21 is faulted but in facility B.
        Assert.Equal([Mac(0x17), Mac(0x1A)], found);
    }

    [Fact]
    public void An_actuator_kind_alert_and_text_together_narrow_to_one()
    {
        var world = World.Create();

        world.Mesh.Alerts.Raise(new AlertKey(Mac(0x0B), AlertKind.Disconnected));
        world.Mesh.Alerts.Raise(new AlertKey(Mac(0x20), AlertKind.Disconnected));
        world.Mesh.Alerts.Raise(new AlertKey(Mac(0x06), AlertKind.Disconnected));

        var found = world.Find(new DeviceSearchFilters(
            Q: "conveyor",
            ActuatorKind: ActuatorKind.Relay,
            Alert: AlertKind.Disconnected));

        Assert.Equal([Mac(0x0B)], found);
    }

    [Fact]
    public void Filters_that_exclude_each_other_find_nothing()
    {
        var world = World.Create();

        Assert.Empty(world.Find(new DeviceSearchFilters(Category: SensorCategory.Environmental, ActuatorKind: ActuatorKind.Valve)));
    }

    [Fact]
    public void A_filter_on_an_empty_set_finds_nothing_whatever_else_was_asked()
    {
        var world = World.Create();

        // Nothing is faulted, so the answer is empty after the first set, whatever the other filters allow.
        Assert.Empty(world.Find(new DeviceSearchFilters(Band: TrustBand.Fault, Zone: "facility-a", Category: SensorCategory.Actuator)));
    }

    // ---- registering ----------------------------------------------------------------------------------------

    [Fact]
    public void Registering_a_device_twice_changes_nothing()
    {
        var world = World.Create();

        Assert.False(world.Index.Register(world.Sensors[0]));
        Assert.Equal(DemoDevices.All.Count, world.Index.Count);
        Assert.Empty(world.Index.FindInconsistencies());
    }

    [Fact]
    public void A_band_change_that_arrives_before_the_device_is_indexed_is_applied_when_it_is()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var mesh = MeshTestHost.Create(clock);
        Assert.True(DemoDevices.All[0].Registration.TryCreateRegistration(out var sensor, out _));

        // The first reading can be processed in the gap between the registry accepting a sensor and the endpoint
        // indexing it. That change must not be lost.
        mesh.Index.SetBand(sensor.MacAddress, TrustBand.Nominal);
        mesh.Index.Register(sensor);

        Assert.Equal(TrustBand.Nominal, mesh.Index.BandOf(sensor.MacAddress));
        Assert.Empty(mesh.Index.FindInconsistencies());
    }

    [Fact]
    public void An_alert_for_a_device_that_is_not_indexed_is_tracked_but_there_is_nothing_to_find()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var mesh = MeshTestHost.Create(clock);

        mesh.Alerts.Raise(new AlertKey(Mac(0x01), AlertKind.Fault));

        Assert.Single(mesh.Alerts.Active());
        Assert.Empty(mesh.Index.Search(new DeviceSearchFilters(Alert: AlertKind.Fault), Catalogue));
    }

    // ---- consistency ----------------------------------------------------------------------------------------

    /// <summary>
    /// The independent truth: a plain list of what each device is, updated by hand alongside the index and
    /// searched by the slowest, most obviously correct method — look at every device.
    /// </summary>
    private sealed class Model(IReadOnlyList<SensorRegistration> sensors)
    {
        public Dictionary<string, TrustBand> Bands { get; } = sensors.ToDictionary(sensor => sensor.MacAddress, _ => TrustBand.Unproven);

        public Dictionary<string, HashSet<AlertKind>> Alerts { get; } = sensors.ToDictionary(sensor => sensor.MacAddress, _ => new HashSet<AlertKind>());

        public List<string> BruteForce(DeviceSearchFilters filters)
        {
            var zones = filters.Zone is null ? null : Catalogue.ZoneIdsBeneath(filters.Zone).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> matches = [];

            // Deliberately a loop over everything with every condition checked in turn: what the index replaces.
            foreach (var sensor in sensors)
            {
                if ((filters.Category is null || sensor.Category == filters.Category)
                    && (filters.ActuatorKind is null || sensor.ActuatorKind == filters.ActuatorKind)
                    && (filters.Band is null || Bands[sensor.MacAddress] == filters.Band)
                    && (filters.Alert is null || Alerts[sensor.MacAddress].Contains(filters.Alert.Value))
                    && (zones is null || zones.Contains(sensor.Location.ZoneId))
                    && (filters.Q is null
                        || sensor.MacAddress.Contains(filters.Q, StringComparison.OrdinalIgnoreCase)
                        || sensor.Location.Room.Contains(filters.Q, StringComparison.OrdinalIgnoreCase)
                        || sensor.Location.NodeId.Contains(filters.Q, StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Add(sensor.MacAddress);
                }
            }

            matches.Sort(StringComparer.Ordinal);

            return matches;
        }
    }

    private static readonly string[] Zones =
        ["facility-a", "zone-1", "sub-zone-b", "zone-3", "sub-zone-f", "facility-b", "zone-4", "zone-5", "sub-zone-j"];

    private static readonly string?[] Texts = [null, null, null, "pump", "valve", "gw-a5", "room", "10:00:2"];

    private static DeviceSearchFilters RandomFilters(Random random) => new(
        Q: Texts[random.Next(Texts.Length)],
        Category: random.Next(3) == 0 ? Enum.GetValues<SensorCategory>()[random.Next(3)] : null,
        ActuatorKind: random.Next(5) == 0 ? Enum.GetValues<ActuatorKind>()[random.Next(4)] : null,
        Zone: random.Next(3) == 0 ? Zones[random.Next(Zones.Length)] : null,
        Band: random.Next(3) == 0 ? Enum.GetValues<TrustBand>()[random.Next(4)] : null,
        Alert: random.Next(3) == 0 ? Enum.GetValues<AlertKind>()[random.Next(5)] : null);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20260301)]
    [InlineData(987654321)]
    public void After_random_state_changes_the_indexes_match_a_brute_force_scan(int seed)
    {
        var random = new Random(seed);
        var world = World.Create();
        var model = new Model(world.Sensors);
        var bands = Enum.GetValues<TrustBand>();
        var kinds = Enum.GetValues<AlertKind>();

        for (var step = 0; step < 3000; step++)
        {
            var sensor = world.Sensors[random.Next(world.Sensors.Count)];
            var mac = sensor.MacAddress;

            switch (random.Next(3))
            {
                case 0:
                    var band = bands[random.Next(bands.Length)];
                    world.Index.SetBand(mac, band);
                    model.Bands[mac] = band;
                    break;

                case 1:
                    var raised = kinds[random.Next(kinds.Length)];
                    world.Mesh.Alerts.Raise(new AlertKey(mac, raised));
                    model.Alerts[mac].Add(raised);
                    break;

                default:
                    var cleared = kinds[random.Next(kinds.Length)];
                    world.Mesh.Alerts.Clear(new AlertKey(mac, cleared));
                    model.Alerts[mac].Remove(cleared);
                    break;
            }

            if (step % 100 != 0)
            {
                continue;
            }

            // Every few steps: the indexes are exactly what they should be, and a handful of random searches
            // agree with the scan.
            Assert.Empty(world.Index.FindInconsistencies());

            for (var probe = 0; probe < 10; probe++)
            {
                var filters = RandomFilters(random);

                Assert.True(
                    model.BruteForce(filters).SequenceEqual(world.Find(filters)),
                    $"Seed {seed}, step {step}: the index and the scan disagree for {filters}.");
            }
        }

        Assert.Empty(world.Index.FindInconsistencies());
    }

    [Fact]
    public async Task Concurrent_changes_and_searches_leave_the_indexes_consistent()
    {
        var world = World.Create();
        var bands = Enum.GetValues<TrustBand>();
        var kinds = Enum.GetValues<AlertKind>();
        using var stop = new CancellationTokenSource();

        var searcher = Task.Run(() =>
        {
            var random = new Random(7);

            while (!stop.IsCancellationRequested)
            {
                world.Find(RandomFilters(random));
            }
        });

        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            var random = new Random(writer);

            for (var step = 0; step < 4000; step++)
            {
                var mac = world.Sensors[random.Next(world.Sensors.Count)].MacAddress;

                if (random.Next(2) == 0)
                {
                    world.Index.SetBand(mac, bands[random.Next(bands.Length)]);
                }
                else if (random.Next(2) == 0)
                {
                    world.Mesh.Alerts.Raise(new AlertKey(mac, kinds[random.Next(kinds.Length)]));
                }
                else
                {
                    world.Mesh.Alerts.Clear(new AlertKey(mac, kinds[random.Next(kinds.Length)]));
                }
            }
        })).ToArray();

        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await searcher;

        Assert.Empty(world.Index.FindInconsistencies());
    }

    [Fact]
    public void The_consistency_check_actually_notices_a_broken_index()
    {
        // A check that cannot fail proves nothing. Corrupting an index by hand shows this one does.
        var world = World.Create();
        var band = typeof(DeviceIndex).GetField("_byBand", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var index = (Dictionary<TrustBand, HashSet<string>>)band.GetValue(world.Index)!;

        index[TrustBand.Fault].Add(Mac(0x01));

        var problems = world.Index.FindInconsistencies();

        Assert.NotEmpty(problems);
        Assert.Contains(problems, problem => problem.Contains("band", StringComparison.Ordinal));
    }

    // ---- scale ----------------------------------------------------------------------------------------------

    /// <summary>Twenty thousand devices across the demo zones, five of the power meters faulted.</summary>
    private static (MeshTestHost Mesh, List<string> FaultedPowerMeters) BuildLargeIndex()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var mesh = MeshTestHost.Create(clock);

        const int devices = 20_000;
        var rare = new List<string>();

        for (var i = 0; i < devices; i++)
        {
            var mac = $"B8:27:{(i >> 16) & 0xFF:X2}:{(i >> 12) & 0xFF:X2}:{(i >> 8) & 0xFF:X2}:{i & 0xFF:X2}";
            var sensor = new SensorRegistration(
                mac,
                new DeploymentLocation("Room", Zones[i % Zones.Length], "N1"),
                i % 2 == 0 ? SensorCategory.Environmental : SensorCategory.PowerConsumption,
                "u",
                new ValueRange(0, 1),
                TelemetryPayloadKind.Real);

            mesh.Index.Register(sensor);

            // Five faulted power meters (odd numbers are power meters), and five faulted environmental sensors
            // that a power-meter search must not return.
            if (i % 4000 is 7 or 8)
            {
                mesh.Index.SetBand(mac, TrustBand.Fault);

                if (i % 2 == 1)
                {
                    rare.Add(mac);
                }
            }
        }

        return (mesh, rare);
    }

    [Fact]
    public void A_search_for_a_rare_condition_among_many_devices_finds_exactly_those_devices()
    {
        var (mesh, rare) = BuildLargeIndex();

        var found = mesh.Index.Search(new DeviceSearchFilters(Category: SensorCategory.PowerConsumption, Band: TrustBand.Fault), Catalogue);

        // Correctness only. How fast it is belongs to the benchmark below, because a stopwatch threshold in the
        // default run is a test that can fail on a slow or busy machine for no fault of the code.
        Assert.Equal(5, rare.Count);
        Assert.Equal(rare.Order(StringComparer.Ordinal), found.Select(hit => hit.MacAddress));
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_a_search_for_a_rare_condition_costs_about_the_size_of_the_rare_set()
    {
        var (mesh, _) = BuildLargeIndex();
        var filters = new DeviceSearchFilters(Category: SensorCategory.PowerConsumption, Band: TrustBand.Fault);

        mesh.Index.Search(filters, Catalogue);

        const int repeats = 200;
        var started = Stopwatch.GetTimestamp();

        for (var i = 0; i < repeats; i++)
        {
            mesh.Index.Search(filters, Catalogue);
        }

        var perSearch = Stopwatch.GetElapsedTime(started) / repeats;

        // The work is bounded by the rare set (five devices), not by the twenty thousand.
        output.WriteLine($"Devices indexed          : 20,000");
        output.WriteLine($"Category + band search   : {perSearch.TotalMicroseconds:N1} us per search");

        Assert.True(perSearch < TimeSpan.FromMilliseconds(250), $"A search took {perSearch.TotalMilliseconds:N1} ms.");
    }
}
