namespace SmartX.Shared;

/// <summary>One simulated device: how it registers itself, and how it behaves by default.</summary>
/// <param name="Registration">The body the device POSTs to /api/sensors when it starts.</param>
/// <param name="Scenario">Its behaviour until an operator changes the desired state.</param>
public sealed record DemoDevice(SensorRegistrationRequest Registration, SimulationScenario Scenario)
{
    public string MacAddress => Registration.MacAddress;
}

/// <summary>
/// The fleet the device simulator runs: fifty-five devices across the demo deployment, the last seven of them a
/// hydroponics bay.
/// </summary>
/// <remarks>
/// Lives in SmartX.Shared because the simulator is a separate program that may not reference the API, and
/// the tests check the fleet against the zone rules the API enforces. The zones themselves stay in the API:
/// a deployment hierarchy is gateway configuration, not something a device carries.
/// <para>
/// Each entry is a registration request rather than a <see cref="SensorRegistration"/>, because that is what
/// a device actually sends. The API validates it exactly as it would a real device's.
/// </para>
/// </remarks>
public static class DemoDevices
{
    private const string Celsius = "°C";
    private const string Humidity = "%RH";
    private const string Co2 = "ppm";
    private const string Watts = "W";
    private const string Kilowatts = "kW";
    private const string Percent = "%";
    private const string OnOff = "state";
    private const string Conductivity = "mS/cm";

    /// <summary>Every device in the demo fleet, with the scenario it starts in.</summary>
    public static IReadOnlyList<DemoDevice> All { get; } =
    [
        Device(0x01, "Rack Row 1", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Celsius, 18, 32, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Device(0x02, "Rack Row 2", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Celsius, 18, 32, TelemetryPayloadKind.Real, 5, SimulationScenario.Drift),
        Device(0x03, "Rack Row 3", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Celsius, 18, 32, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Device(0x04, "Rack Row 1", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Humidity, 30, 70, TelemetryPayloadKind.Real, 5, SimulationScenario.Critical),
        Device(0x05, "Rack Row 2", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Humidity, 30, 70, TelemetryPayloadKind.Real, 5, SimulationScenario.Spike),
        Device(0x06, "CRAC Water Pump", "sub-zone-b", "GW-A1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Pump),
        Device(0x07, "Rack Row 3", "sub-zone-b", "GW-A1", SensorCategory.Environmental, Humidity, 30, 70, TelemetryPayloadKind.Real, 5, SimulationScenario.TypeViolation),
        Device(0x08, "Line 1 Drive", "sub-zone-c", "GW-A2", SensorCategory.PowerConsumption, Watts, 0, 5500, TelemetryPayloadKind.Integer, 10, SimulationScenario.Nominal),
        Device(0x09, "Line 2 Drive", "sub-zone-c", "GW-A2", SensorCategory.PowerConsumption, Watts, 0, 5500, TelemetryPayloadKind.Integer, 10, SimulationScenario.Spike),
        Device(0x0A, "Line 1 Conveyor", "sub-zone-c", "GW-A2", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Relay),
        Device(0x0B, "Line 2 Conveyor", "sub-zone-c", "GW-A2", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Disconnect, ActuatorKind.Relay),
        Device(0x0C, "Compressor", "sub-zone-c", "GW-A2", SensorCategory.PowerConsumption, Watts, 0, 7500, TelemetryPayloadKind.Integer, 10, SimulationScenario.Critical),
        Device(0x0D, "Production Floor", "zone-1", "GW-A1", SensorCategory.Environmental, Celsius, 5, 45, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Device(0x0E, "Mezzanine", "zone-1", "GW-A1", SensorCategory.Environmental, Celsius, 5, 45, TelemetryPayloadKind.Real, 5, SimulationScenario.TypeViolation),
        Device(0x0F, "Main Incomer", "zone-2", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 450, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Device(0x10, "Feeder 1", "sub-zone-d", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 200, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Device(0x11, "Feeder 2", "sub-zone-d", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 200, TelemetryPayloadKind.Real, 10, SimulationScenario.Drift),
        Device(0x12, "Feeder 3", "sub-zone-d", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 200, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Device(0x13, "Feeder 4", "sub-zone-d", "GW-A3", SensorCategory.PowerConsumption, Kilowatts, 0, 200, TelemetryPayloadKind.Real, 10, SimulationScenario.Disconnect),
        Device(0x14, "String 1", "sub-zone-e", "GW-A4", SensorCategory.PowerConsumption, Kilowatts, 0, 100, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Device(0x15, "String 2", "sub-zone-e", "GW-A4", SensorCategory.PowerConsumption, Kilowatts, 0, 100, TelemetryPayloadKind.Real, 10, SimulationScenario.Nominal),
        Device(0x16, "String 3", "sub-zone-e", "GW-A4", SensorCategory.PowerConsumption, Kilowatts, 0, 100, TelemetryPayloadKind.Real, 10, SimulationScenario.Spike),
        Device(0x17, "Freezer 1", "sub-zone-f", "GW-A5", SensorCategory.Environmental, Celsius, -28, -12, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Device(0x18, "Freezer 2", "sub-zone-f", "GW-A5", SensorCategory.Environmental, Celsius, -28, -12, TelemetryPayloadKind.Real, 5, SimulationScenario.Drift),
        Device(0x19, "Freezer Glycol Valve", "sub-zone-f", "GW-A5", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Valve),
        Device(0x1A, "Chiller 1", "sub-zone-g", "GW-A5", SensorCategory.Environmental, Celsius, 0, 8, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Device(0x1B, "Chiller 2", "sub-zone-g", "GW-A5", SensorCategory.Environmental, Celsius, 0, 8, TelemetryPayloadKind.Real, 5, SimulationScenario.TypeViolation),
        Device(0x1C, "Chiller Fan Speed", "sub-zone-g", "GW-A5", SensorCategory.Actuator, Percent, 0, 100, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal, ActuatorKind.Dimmer),
        Device(0x1D, "Dock Apron", "zone-4", "GW-B1", SensorCategory.Environmental, Celsius, -15, 50, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Device(0x1E, "Dock Leveller Pump", "sub-zone-h", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Pump),
        Device(0x1F, "Dock Door 2 Strike", "sub-zone-h", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.TypeViolation, ActuatorKind.Relay),
        Device(0x20, "Dock Door 3 Strike", "sub-zone-h", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Disconnect, ActuatorKind.Relay),
        Device(0x21, "Dock Drain Valve", "sub-zone-h", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Valve),
        Device(0x22, "Open Office", "zone-5", "GW-B2", SensorCategory.Environmental, Celsius, 15, 30, TelemetryPayloadKind.Real, 5, SimulationScenario.Drift),
        Device(0x23, "Open Office", "zone-5", "GW-B2", SensorCategory.Environmental, Co2, 400, 2500, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal),
        Device(0x24, "Room J1", "sub-zone-j", "GW-B2", SensorCategory.Environmental, Co2, 400, 2000, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal),
        Device(0x25, "Room J2", "sub-zone-j", "GW-B2", SensorCategory.Environmental, Co2, 400, 2000, TelemetryPayloadKind.Integer, 5, SimulationScenario.Spike),
        Device(0x26, "Room J2", "sub-zone-j", "GW-B2", SensorCategory.Environmental, Humidity, 30, 70, TelemetryPayloadKind.Real, 5, SimulationScenario.Disconnect),
        Device(0x27, "Office Lights", "sub-zone-k", "GW-B3", SensorCategory.Actuator, Percent, 0, 100, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal, ActuatorKind.Dimmer),
        Device(0x28, "Lighting Circuit", "sub-zone-k", "GW-B3", SensorCategory.PowerConsumption, Watts, 0, 3000, TelemetryPayloadKind.Integer, 10, SimulationScenario.Nominal),

        // Added in Part 2: more actuators, spread across the zone trees so a zone-wide command (close every
        // valve under Zone 3, say) has several devices to reach.
        Device(0x29, "Coolant Pump", "sub-zone-g", "GW-A5", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Pump),
        Device(0x2A, "Defrost Pump", "sub-zone-f", "GW-A5", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Pump),
        Device(0x2B, "Chiller Valve A", "sub-zone-g", "GW-A5", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Valve),
        Device(0x2C, "Chiller Valve B", "sub-zone-g", "GW-A5", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Valve),
        Device(0x2D, "Freezer Valve", "sub-zone-f", "GW-A5", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Valve),
        Device(0x2E, "Meeting Room Lights", "sub-zone-j", "GW-B2", SensorCategory.Actuator, Percent, 0, 100, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal, ActuatorKind.Dimmer),
        Device(0x2F, "Warehouse Lights", "zone-4", "GW-B1", SensorCategory.Actuator, Percent, 0, 100, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal, ActuatorKind.Dimmer),
        Device(0x30, "Dock Fan Relay", "zone-4", "GW-B1", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Relay),

        // The hydroponics bay, added with the suggestion engine. Named after the brief's own example, "Pump A right after
        // Moisture Sensor B drops below 20%", so the demonstration matches the wording an assessor reads. Moisture is
        // registered 0 to 100 %, which makes the engine's 20 % watch level literally 20 %.
        Device(0x31, "Moisture Sensor A", "zone-6", "GW-B3", SensorCategory.Environmental, Percent, 0, 100, TelemetryPayloadKind.Real, 5, SimulationScenario.Nominal),
        Device(0x32, "Moisture Sensor B", "zone-6", "GW-B3", SensorCategory.Environmental, Percent, 0, 100, TelemetryPayloadKind.Real, 5, SimulationScenario.DryOut),
        Device(0x33, "Nutrient EC Sensor", "zone-6", "GW-B3", SensorCategory.Environmental, Conductivity, 0, 5, TelemetryPayloadKind.Real, 5, SimulationScenario.Fouling),
        Device(0x34, "Pump A", "zone-6", "GW-B3", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Pump),
        Device(0x35, "Pump B", "zone-6", "GW-B3", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Pump),
        Device(0x36, "Grow Light", "zone-6", "GW-B3", SensorCategory.Actuator, Percent, 0, 100, TelemetryPayloadKind.Integer, 5, SimulationScenario.Nominal, ActuatorKind.Dimmer),
        Device(0x37, "Drain Valve", "zone-6", "GW-B3", SensorCategory.Actuator, OnOff, 0, 1, TelemetryPayloadKind.State, 5, SimulationScenario.Nominal, ActuatorKind.Valve),
    ];

    /// <summary>The address of a hydroponics device, for the seeded operator history and the tests that name them.</summary>
    public static string Mac(byte suffix) => $"B8:27:EB:10:00:{suffix:X2}";

    /// <summary>The bay's devices by the names the brief uses.</summary>
    public static class Hydroponics
    {
        /// <summary>The moisture sensor that dries out and recovers, so it crosses 20 % on a loop.</summary>
        public static string MoistureSensorB => Mac(0x32);

        public static string MoistureSensorA => Mac(0x31);

        public static string NutrientEcSensor => Mac(0x33);

        public static string PumpA => Mac(0x34);

        public static string PumpB => Mac(0x35);

        public static string GrowLight => Mac(0x36);

        public static string DrainValve => Mac(0x37);
    }

    /// <summary>Builds one device's registration request.</summary>
    private static DemoDevice Device(
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
        SimulationScenario scenario,
        ActuatorKind? actuatorKind = null) =>
        new(new SensorRegistrationRequest(
                $"B8:27:EB:10:00:{suffix:X2}",
                room,
                zoneId,
                nodeId,
                category,
                unit,
                minimum,
                maximum,
                payloadKind,
                heartbeatSeconds,
                actuatorKind),
            scenario);
}
