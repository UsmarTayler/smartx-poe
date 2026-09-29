using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// The larger demo deployment: two facilities, three levels deep, with constraints at every level.
/// </summary>
/// <remarks>
/// Temperature narrows from the facility (-40 to 85 C) to the cold store (-30 to 15) to the freezer bay
/// (-30 to -10), and category restrictions appear at zone and sub-zone level, so the recursive validator has
/// real work to do. The brief ids - facility-a, zone-1, sub-zone-b, zone-2 - are kept, so anything written
/// against the default hierarchy still resolves.
/// <para>
/// Zones stay in the API even though the devices that live in them now come from the simulator over HTTP. A
/// deployment hierarchy is gateway configuration that an operator maintains, not something a device carries
/// or could tell the gateway about.
/// </para>
/// </remarks>
internal static class DemoZoneHierarchy
{
    private const string Celsius = "°C";
    private const string Humidity = "%RH";
    private const string Co2 = "ppm";
    private const string Watts = "W";
    private const string Kilowatts = "kW";
    private const string Percent = "%";

    private static readonly SensorCategory[] EnvironmentalAndActuators = [SensorCategory.Environmental, SensorCategory.Actuator];

    public static IReadOnlyList<DeploymentZone> Zones { get; } =
    [
        Zone("facility-a", "Facility A – Main Plant", null, 0,
            ranges: [(Celsius, -40, 85), (Humidity, 0, 100)]),

        Zone("zone-1", "Zone 1 – Production", "facility-a", 1,
            ranges: [(Celsius, 0, 50)]),
        Zone("sub-zone-b", "Sub-Zone B – Server Room", "zone-1", 2,
            EnvironmentalAndActuators, [(Celsius, 15, 35), (Humidity, 20, 80)]),
        Zone("sub-zone-c", "Sub-Zone C – Assembly Line", "zone-1", 2,
            [SensorCategory.PowerConsumption, SensorCategory.Actuator], [(Watts, 0, 7500)]),

        Zone("zone-2", "Zone 2 – Energy Centre", "facility-a", 1,
            [SensorCategory.PowerConsumption], [(Kilowatts, 0, 500)]),
        Zone("sub-zone-d", "Sub-Zone D – Switchgear", "zone-2", 2,
            ranges: [(Kilowatts, 0, 250)]),
        Zone("sub-zone-e", "Sub-Zone E – Solar Array", "zone-2", 2,
            ranges: [(Kilowatts, 0, 120)]),

        Zone("zone-3", "Zone 3 – Cold Store", "facility-a", 1,
            EnvironmentalAndActuators, [(Celsius, -30, 15)]),
        Zone("sub-zone-f", "Sub-Zone F – Freezer Bay", "zone-3", 2,
            ranges: [(Celsius, -30, -10)]),
        Zone("sub-zone-g", "Sub-Zone G – Chiller Bay", "zone-3", 2,
            ranges: [(Celsius, -2, 10)]),

        Zone("facility-b", "Facility B – Warehouse", null, 0,
            ranges: [(Celsius, -20, 60), (Co2, 0, 5000)]),

        Zone("zone-4", "Zone 4 – Loading Dock", "facility-b", 1,
            EnvironmentalAndActuators),
        Zone("sub-zone-h", "Sub-Zone H – Dock Doors", "zone-4", 2,
            [SensorCategory.Actuator]),

        Zone("zone-5", "Zone 5 – Offices", "facility-b", 1,
            ranges: [(Celsius, 10, 35), (Co2, 0, 3000)]),
        Zone("sub-zone-j", "Sub-Zone J – Meeting Rooms", "zone-5", 2,
            ranges: [(Co2, 400, 2000), (Humidity, 30, 70)]),
        Zone("sub-zone-k", "Sub-Zone K – Lighting", "zone-5", 2,
            [SensorCategory.Actuator, SensorCategory.PowerConsumption], [(Percent, 0, 100)]),
    ];

    private static DeploymentZone Zone(
        string id,
        string name,
        string? parentId,
        int depth,
        IReadOnlyList<SensorCategory>? categories = null,
        (string Unit, double Minimum, double Maximum)[]? ranges = null) =>
        new(id, name, parentId, depth, new ZoneConstraints(
            categories,
            ranges?.ToDictionary(range => range.Unit, range => new ValueRange(range.Minimum, range.Maximum), StringComparer.Ordinal)));
}
