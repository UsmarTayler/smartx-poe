using SmartX.Shared;

namespace SmartX.Api.Simulation;

/// <summary>A sensor the seeder registers, and the scenario it starts in.</summary>
internal sealed record SeededSensor(SensorRegistration Registration, SimulationScenario Scenario);

/// <summary>
/// The zones and sensors of the demo deployment.
/// </summary>
/// <remarks>
/// Two facilities, three levels deep, with constraints placed at every level so the recursive validator
/// has real work to do: temperature narrows from the facility (-40 to 85 °C) to the cold store (-30 to 15)
/// to the freezer bay (-30 to -10), and category restrictions appear at zone and sub-zone level. The brief's
/// own ids — facility-a, zone-1, sub-zone-b, zone-2 — are kept, so anything written against the default
/// hierarchy still resolves.
/// <para>
/// Declared in code rather than a JSON file because the seeder validates every sensor against these zones
/// on startup and refuses to run if one fails. A typo here is caught by the build or by that check, not by
/// a demo that quietly seeds half a mesh.
/// </para>
/// </remarks>
internal static class DemoMesh
{
    private const string Celsius = "°C";
    private const string Humidity = "%RH";
    private const string Co2 = "ppm";
    private const string Watts = "W";
    private const string Kilowatts = "kW";
    private const string Percent = "%";
    private const string OnOff = "state";

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

    /// <summary>
    /// Forty sensors: 24 nominal and four in each fault scenario.
    /// </summary>
    /// <remarks>
    /// Drift and Spike are only given to numeric sensors, because a State sensor has no range to leave.
    /// Power sensors report every ten seconds and everything else every five, so the simulator carries
    /// roughly seven readings a second.
    /// </remarks>
    public static IReadOnlyList<SeededSensor> Sensors { get; } =
    [
        // Sub-Zone B – Server Room
        Sensor(0x01, "Rack Row 1", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Celsius, 18, 32, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Sensor(0x02, "Rack Row 2", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Celsius, 18, 32, TelemetryPayloadKind.Real, 5, SimulationScenario.Drift),
        Sensor(0x03, "Rack Row 3", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Celsius, 18, 32, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Sensor(0x04, "Rack Row 1", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Humidity, 30, 70, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Sensor(0x05, "Rack Row 2", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Humidity, 30, 70, TelemetryPayloadKind.Real, 5, SimulationScenario.Spike),
        Sensor(0x06, "CRAC Unit", "sub-zone-b", "GW-A1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal),
        Sensor(0x07, "Rack Row 3", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Humidity, 30, 70, TelemetryPayloadKind.Real, 5, SimulationScenario.TypeViolation),

        // Sub-Zone C – Assembly Line
        Sensor(0x08, "Line 1 Drive", "sub-zone-c", "GW-A2", SensorCategory.PowerConsumption, Watts, 0, 5500, TelemetryPayloadKind.Integer, 10, SimulationScenario.Nominal),
        Sensor(0x09, "Line 2 Drive", "sub-zone-c", "GW-A2", SensorCategory.PowerConsumption, Watts, 0, 5500, TelemetryPayloadKind.Integer, 10, SimulationScenario.Spike),
        Sensor(0x0A, "Line 1 Conveyor", "sub-zone-c", "GW-A2", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal),
        Sensor(0x0B, "Line 2 Conveyor", "sub-zone-c", "GW-A2", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Disconnect),
        Sensor(0x0C, "Compressor", "sub-zone-c", "GW-A2", SensorCategory.PowerConsumption, Watts, 0, 7500, TelemetryPayloadKind.Integer, 10, SimulationScenario.Nominal),

        // Zone 1, directly
        Sensor(0x0D, "Production Floor", "zone-1", "GW-A1", SensorCategory.Environmental, Celsius, 5, 45, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Sensor(0x0E, "Mezzanine", "zone-1", "GW-A1", SensorCategory.Environmental, Celsius, 5, 45, TelemetryPayloadKind.Real, 5, SimulationScenario.TypeViolation),

        // Zone 2 – Energy Centre, and its switchgear and solar sub-zones
        Sensor(0x0F, "Main Incomer", "zone-2", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 450, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Sensor(0x10, "Feeder 1", "sub-zone-d", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 200, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Sensor(0x11, "Feeder 2", "sub-zone-d", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 200, TelemetryPayloadKind.Real, 10, SimulationScenario.Drift),
        Sensor(0x12, "Feeder 3", "sub-zone-d", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 200, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Sensor(0x13, "Feeder 4", "sub-zone-d", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 200, TelemetryPayloadKind.Real, 10, SimulationScenario.Disconnect),
        Sensor(0x14, "String 1", "sub-zone-e", "GW-A4", SensorCategory.PowerConsumption, Kilowatts, 0, 100, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Sensor(0x15, "String 2", "sub-zone-e", "GW-A4", SensorCategory.PowerConsumption, Kilowatts, 0, 100, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Sensor(0x16, "String 3", "sub-zone-e", "GW-A4", SensorCategory.PowerConsumption, Kilowatts, 0, 100, TelemetryPayloadKind.Real, 10, SimulationScenario.Spike),

        // Zone 3 – Cold Store
        Sensor(0x17, "Freezer 1", "sub-zone-f", "GW-A5", SensorCategory.Environmental, Celsius, -28, -12, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Sensor(0x18, "Freezer 2", "sub-zone-f", "GW-A5", SensorCategory.Environmental, Celsius, -28, -12, TelemetryPayloadKind.Real, 5, SimulationScenario.Drift),
        Sensor(0x19, "Freezer Door", "sub-zone-f", "GW-A5", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal),
        Sensor(0x1A, "Chiller 1", "sub-zone-g", "GW-A5", SensorCategory.Environmental, Celsius, 0, 8, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Sensor(0x1B, "Chiller 2", "sub-zone-g", "GW-A5", SensorCategory.Environmental, Celsius, 0, 8, TelemetryPayloadKind.Real, 5, SimulationScenario.TypeViolation),
        Sensor(0x1C, "Chiller Valve", "sub-zone-g", "GW-A5", SensorCategory.Actuator, Percent, 0, 100, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal),

        // Zone 4 – Loading Dock
        Sensor(0x1D, "Dock Apron", "zone-4", "GW-B1", SensorCategory.Environmental, Celsius, -15, 50, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Sensor(0x1E, "Door 1", "sub-zone-h", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal),
        Sensor(0x1F, "Door 2", "sub-zone-h", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.TypeViolation),
        Sensor(0x20, "Door 3", "sub-zone-h", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Disconnect),
        Sensor(0x21, "Door 4", "sub-zone-h", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal),

        // Zone 5 – Offices
        Sensor(0x22, "Open Office", "zone-5", "GW-B2", SensorCategory.Environmental, Celsius, 15, 30, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Sensor(0x23, "Open Office", "zone-5", "GW-B2", SensorCategory.Environmental, Co2, 400, 2500, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal),
        Sensor(0x24, "Room J1", "sub-zone-j", "GW-B2", SensorCategory.Environmental, Co2, 400, 2000, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal),
        Sensor(0x25, "Room J2", "sub-zone-j", "GW-B2", SensorCategory.Environmental, Co2, 400, 2000, TelemetryPayloadKind.Integer, 5, SimulationScenario.Spike),
        Sensor(0x26, "Room J2", "sub-zone-j", "GW-B2", SensorCategory.Environmental, Humidity, 30, 70, TelemetryPayloadKind.Real, 5, SimulationScenario.Disconnect),
        Sensor(0x27, "Office Lights", "sub-zone-k", "GW-B3", SensorCategory.Actuator, Percent, 0, 100, TelemetryPayloadKind.Integer, 5, SimulationScenario.Drift),
        Sensor(0x28, "Lighting Circuit", "sub-zone-k", "GW-B3", SensorCategory.PowerConsumption, Watts, 0, 3000, TelemetryPayloadKind.Integer, 10, SimulationScenario.Nominal),
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

    private static SeededSensor Sensor(
        byte suffix,
        string room,
        string zoneId,
        string nodeId,
        SensorCategory category,
        string unit,
        double minimum,
        double maximum,
        TelemetryPayloadKind payloadKind,
        int heartbeatSeconds,
        SimulationScenario scenario) =>
        new(new SensorRegistration(
                $"B8:27:EB:10:00:{suffix:X2}",
                new DeploymentLocation(room, zoneId, nodeId),
                category,
                unit,
                new ValueRange(minimum, maximum),
                payloadKind,
                TimeSpan.FromSeconds(heartbeatSeconds)),
            scenario);
}
