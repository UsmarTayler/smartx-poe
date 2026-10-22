using SmartX.Shared;
using static SmartX.Tests.TestPackets;

namespace SmartX.Tests;

/// <summary>
/// Covers the arithmetic and comparison operators on <see cref="NumericPacket{T}"/>.
/// </summary>
/// <remarks>
/// The float cases use values that are exact in binary floating point (21.5, 3.25, 24.75), so the
/// assertions can compare exactly. A test that needed a tolerance would be testing IEEE 754 rather
/// than the operator.
/// </remarks>
public class NumericPacketTests
{
    [Fact]
    public void Adding_float_readings_sums_the_values()
    {
        var meter1 = Numeric(21.5f);
        var meter2 = Numeric(3.25f);

        var meter3 = meter1 + meter2;

        Assert.Equal(24.75f, meter3.Value);
    }

    [Fact]
    public void Adding_int_readings_sums_the_values()
    {
        // The same operator over a different payload type. This is the pay-off of the generic
        // design: one implementation, specialised by the JIT for each T, no duplicated class.
        var meter1 = Numeric(1200);
        var meter2 = Numeric(850);

        var meter3 = meter1 + meter2;

        Assert.Equal(2050, meter3.Value);
    }

    [Fact]
    public void Aggregating_across_nodes_attributes_the_result_to_no_single_node()
    {
        var meter1 = Numeric(1200, nodeId: NodeA);
        var meter2 = Numeric(850, nodeId: NodeB);

        var total = meter1 + meter2;

        Assert.Equal(TelemetryPacket<int>.AggregateNodeId, total.NodeId);
    }

    [Fact]
    public void Combining_readings_from_one_node_keeps_that_node()
    {
        var earlier = Numeric(1200, nodeId: NodeA);
        var later = Numeric(1350, nodeId: NodeA);

        var delta = later - earlier;

        Assert.Equal(NodeA, delta.NodeId);
    }

    [Fact]
    public void Combined_reading_carries_the_later_timestamp()
    {
        var earlier = Numeric(1200, timestamp: Noon);
        var later = Numeric(1350, timestamp: Noon.AddMinutes(15));

        // Operand order is reversed deliberately: the later timestamp should win regardless of
        // which side it appears on.
        var total = earlier + later;

        Assert.Equal(Noon.AddMinutes(15), total.Timestamp);
    }

    [Fact]
    public void Subtracting_produces_the_delta()
    {
        var later = Numeric(24.75f);
        var earlier = Numeric(21.5f);

        var delta = later - earlier;

        Assert.Equal(3.25f, delta.Value);
    }

    [Fact]
    public void Subtracting_a_larger_reading_produces_a_negative_delta()
    {
        // A falling reading is a real observation, not an error to be clamped away.
        var later = Numeric(21.5f);
        var earlier = Numeric(24.75f);

        var delta = later - earlier;

        Assert.Equal(-3.25f, delta.Value);
    }

    [Fact]
    public void Greater_than_compares_the_magnitudes()
    {
        var high = Numeric(30.0f);
        var low = Numeric(18.0f);

        Assert.True(high > low);
        Assert.False(low > high);
    }

    [Fact]
    public void Less_than_compares_the_magnitudes()
    {
        var high = Numeric(30.0f);
        var low = Numeric(18.0f);

        Assert.True(low < high);
        Assert.False(high < low);
    }

    [Fact]
    public void Equal_magnitudes_are_neither_greater_nor_less()
    {
        var left = Numeric(21.5f);
        var right = Numeric(21.5f);

        Assert.False(left > right);
        Assert.False(left < right);
    }

    [Fact]
    public void Adding_subtracting_or_ordering_readings_in_different_units_throws()
    {
        var temperature = Numeric(21.5f, unit: "°C", category: SensorCategory.Environmental);
        var humidity = Numeric(48.0f, unit: "%", category: SensorCategory.Environmental);

        var error = Assert.Throws<InvalidOperationException>(() => temperature + humidity);
        Assert.Contains("°C", error.Message, StringComparison.Ordinal);
        Assert.Contains("%", error.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => temperature - humidity);
        Assert.Throws<InvalidOperationException>(() => temperature > humidity);
        Assert.Throws<InvalidOperationException>(() => temperature < humidity);
    }

    [Fact]
    public void Adding_subtracting_or_ordering_readings_from_different_categories_throws()
    {
        var humidity = Numeric(48.0f, unit: "%", category: SensorCategory.Environmental);
        var battery = Numeric(80.0f, unit: "%", category: SensorCategory.PowerConsumption);

        // The message names both categories, so a caller can see what was mixed.
        var error = Assert.Throws<InvalidOperationException>(() => humidity + battery);
        Assert.Contains(nameof(SensorCategory.Environmental), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SensorCategory.PowerConsumption), error.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => humidity - battery);
        Assert.Throws<InvalidOperationException>(() => humidity > battery);
        Assert.Throws<InvalidOperationException>(() => humidity < battery);
    }

    [Fact]
    public void Equality_across_different_units_is_false_rather_than_an_exception()
    {
        // Deliberately asymmetric with ordering above. Asking whether two packets are the same is
        // always a valid question, and equality is called from places the caller never wrote —
        // dictionary lookups, collection searches — where a throw would be a trap.
        var temperature = Numeric(21.5f, unit: "°C", category: SensorCategory.Environmental);
        var humidity = Numeric(21.5f, unit: "%", category: SensorCategory.Environmental);

        Assert.False(temperature == humidity);
        Assert.True(temperature != humidity);
    }

    [Fact]
    public void Equality_across_different_categories_is_false_rather_than_an_exception()
    {
        var humidity = Numeric(48.0f, unit: "%", category: SensorCategory.Environmental);
        var battery = Numeric(48.0f, unit: "%", category: SensorCategory.PowerConsumption);

        Assert.False(humidity == battery);
        Assert.True(humidity != battery);
    }


    [Fact]
    public void Packets_with_identical_fields_are_equal()
    {
        var left = Numeric(21.5f);
        var right = Numeric(21.5f);

        Assert.True(left == right);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void The_same_magnitude_from_two_nodes_is_not_the_same_packet()
    {
        // Equality covers the metadata, not just the number. Two meters both reading 1200 kWh have
        // not reported the same packet.
        var fromA = Numeric(1200, nodeId: NodeA);
        var fromB = Numeric(1200, nodeId: NodeB);

        Assert.False(fromA == fromB);
    }

    [Fact]
    public void A_null_operand_is_rejected()
    {
        var reading = Numeric(1200);

        Assert.Throws<ArgumentNullException>(() => reading + null!);
        Assert.Throws<ArgumentNullException>(() => null! + reading);
    }

    [Fact]
    public void Equality_tolerates_null_without_throwing()
    {
        var reading = Numeric(1200);
        NumericPacket<int>? absent = null;

        Assert.False(reading == absent);
        Assert.False(absent == reading);
        Assert.True(absent == null);
    }

    [Fact]
    public void A_reading_without_a_unit_is_rejected_at_construction()
    {
        // Rejecting here rather than inside an operator means a packet that exists is always safe
        // to combine.
        Assert.Throws<ArgumentException>(
            () => new NumericPacket<int>(NodeA, 1200, Noon, SensorCategory.PowerConsumption, "  "));
    }
}
