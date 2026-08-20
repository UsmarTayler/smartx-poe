using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Hierarchies and sensors for the zone tests.
/// </summary>
/// <remarks>
/// The three-level fixture mirrors the example in the brief — Sub-Zone B inside Zone 1 inside
/// Facility A — and spreads its constraints across levels on purpose, so that a test can tell
/// which level rejected a sensor.
/// </remarks>
internal static class TestZones
{
    internal const string Facility = "facility-a";
    internal const string Zone1 = "zone-1";
    internal const string SubZoneB = "sub-zone-b";
    internal const string Celsius = "°C";

    /// <summary>Builds a zone with no constraints of its own.</summary>
    internal static DeploymentZone Zone(string id, string? parentId, int depth) =>
        new(id, id, parentId, depth, ZoneConstraints.Unrestricted);

    /// <summary>
    /// Facility A (no actuators, -40 to 85 °C) → Zone 1 (0 to 50 °C) → Sub-Zone B (unrestricted).
    /// </summary>
    /// <remarks>
    /// The category restriction sits only at the facility and the tighter range only at Zone 1, so
    /// a sensor deployed into Sub-Zone B can be rejected by a grandparent, by a parent, or by both,
    /// and the violations name which.
    /// </remarks>
    internal static ZoneCatalogue ThreeLevel() =>
        new(
        [
            new DeploymentZone(
                Facility, "Facility A", null, 0,
                new ZoneConstraints(
                    [SensorCategory.Environmental, SensorCategory.PowerConsumption],
                    new Dictionary<string, ValueRange> { [Celsius] = new(-40, 85) })),

            new DeploymentZone(
                Zone1, "Zone 1", Facility, 1,
                new ZoneConstraints(
                    permittedRanges: new Dictionary<string, ValueRange> { [Celsius] = new(0, 50) })),

            new DeploymentZone(SubZoneB, "Sub-Zone B", Zone1, 2, ZoneConstraints.Unrestricted)
        ]);

    /// <summary>A chain of zones, each the child of the one before it.</summary>
    internal static ZoneCatalogue Chain(int levels) =>
        new([.. Enumerable.Range(0, levels).Select(index =>
            Zone($"level-{index}", index == 0 ? null : $"level-{index - 1}", index))]);

    /// <summary>Two zones that are each other's parent.</summary>
    internal static ZoneCatalogue Cyclic() =>
        new([Zone("ring-a", "ring-b", 0), Zone("ring-b", "ring-a", 1)]);

    /// <summary>Builds a sensor, defaulting to a valid thermometer in Sub-Zone B.</summary>
    internal static SensorRegistration Sensor(
        string mac = "AA:BB:CC:DD:EE:01",
        string zoneId = SubZoneB,
        SensorCategory category = SensorCategory.Environmental,
        string unit = Celsius,
        double minimum = 10,
        double maximum = 30,
        TelemetryPayloadKind payloadKind = TelemetryPayloadKind.Real,
        string room = "Server Room 2",
        string nodeId = "NODE-1") =>
        new(mac,
            new DeploymentLocation(room, zoneId, nodeId),
            category,
            unit,
            new ValueRange(minimum, maximum),
            payloadKind);
}
