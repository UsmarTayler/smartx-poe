using System.Numerics;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Builders that keep the tests readable by naming only the field each test actually cares about.
/// </summary>
/// <remarks>
/// Every packet needs five constructor arguments, and repeating all five in every assertion buries
/// the one value under test. These helpers fix sensible defaults so a test that is about addition
/// reads as being about addition.
/// </remarks>
internal static class TestPackets
{
    internal const string NodeA = "AA:BB:CC:DD:EE:01";
    internal const string NodeB = "AA:BB:CC:DD:EE:02";

    internal static readonly DateTimeOffset Noon =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(2));

    /// <summary>Builds a numeric reading, defaulting to node A at noon in kWh.</summary>
    internal static NumericPacket<T> Numeric<T>(
        T value,
        string nodeId = NodeA,
        string unit = "kWh",
        SensorCategory category = SensorCategory.PowerConsumption,
        DateTimeOffset? timestamp = null)
        where T : struct, INumber<T> =>
        new(nodeId, value, timestamp ?? Noon, category, unit);

    /// <summary>Builds an actuator reading, defaulting to node A at noon.</summary>
    internal static StatePacket State(
        bool value,
        string nodeId = NodeA,
        DateTimeOffset? timestamp = null) =>
        new(nodeId, value, timestamp ?? Noon);
}
