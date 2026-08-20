using SmartX.Shared;
using static SmartX.Tests.TestZones;

namespace SmartX.Tests;

/// <summary>
/// Covers MAC canonicalisation and the invariants a registration enforces on itself.
/// </summary>
public class SensorRegistrationTests
{
    [Theory]
    [InlineData("aa:bb:cc:dd:ee:01", "AA:BB:CC:DD:EE:01")]
    [InlineData("AA-BB-CC-DD-EE-01", "AA:BB:CC:DD:EE:01")]
    [InlineData("  AA:BB:CC:DD:EE:01  ", "AA:BB:CC:DD:EE:01")]
    public void A_mac_address_is_stored_in_one_canonical_form(string input, string expected)
    {
        // Without this, the same physical device registers twice under two spellings.
        Assert.Equal(expected, Sensor(mac: input).MacAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AA:BB:CC:DD:EE")]           // five pairs
    [InlineData("AA:BB:CC:DD:EE:01:02")]     // seven pairs
    [InlineData("GG:BB:CC:DD:EE:01")]        // not hex
    [InlineData("AABBCCDDEE01")]             // no separators
    [InlineData("AA:BB-CC:DD:EE:01")]        // separators mixed within one address
    public void A_malformed_mac_address_is_rejected(string input)
    {
        Assert.False(MacAddressValidator.IsValid(input));
        Assert.Throws<ArgumentException>(() => Sensor(mac: input));
    }

    [Fact]
    public void The_reserved_aggregate_address_cannot_be_registered()
    {
        // Well-formed, but it is what a combined reading is attributed to. A device claiming it
        // would make an aggregate indistinguishable from a real node.
        Assert.False(MacAddressValidator.IsValid(TelemetryPacket<int>.AggregateNodeId));
    }

    [Fact]
    public void A_state_sensor_must_be_registered_as_an_actuator()
    {
        // A bool reading becomes a StatePacket, which fixes its own category to Actuator. Allowing
        // this would produce packets that contradicted the registration that accepted them.
        var error = Assert.Throws<ArgumentException>(() =>
            Sensor(category: SensorCategory.Environmental,
                   unit: "state",
                   payloadKind: TelemetryPayloadKind.State));

        Assert.Contains(nameof(SensorCategory.Actuator), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_actuator_may_report_a_numeric_payload()
    {
        // The reverse is deliberately allowed: a dimmer is an actuator that reports a level.
        var dimmer = Sensor(category: SensorCategory.Actuator, unit: "%", minimum: 0, maximum: 100);

        Assert.Equal(SensorCategory.Actuator, dimmer.Category);
        Assert.Equal(TelemetryPayloadKind.Real, dimmer.PayloadKind);
    }

    [Fact]
    public void A_registration_without_a_unit_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Sensor(unit: "  "));
    }

    [Fact]
    public void A_location_missing_any_field_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DeploymentLocation("", SubZoneB, "NODE-1"));
        Assert.Throws<ArgumentException>(() => new DeploymentLocation("Room", "", "NODE-1"));
        Assert.Throws<ArgumentException>(() => new DeploymentLocation("Room", SubZoneB, ""));
    }

    [Fact]
    public void An_inverted_value_range_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ValueRange(50, 10));
    }

    [Fact]
    public void A_value_range_bounded_by_nan_is_rejected()
    {
        // NaN compares false against everything, so such a range would silently reject every
        // reading rather than failing where the mistake was made.
        Assert.Throws<ArgumentException>(() => new ValueRange(double.NaN, 10));
    }

    [Fact]
    public void A_range_admits_readings_of_any_numeric_type_without_boxing_them()
    {
        // The generic overload converts through INumberBase.CreateChecked, resolved at compile time
        // for the concrete payload type.
        var range = new ValueRange(0, 100);

        Assert.True(range.Contains(42));
        Assert.True(range.Contains(42.5f));
        Assert.False(range.Contains(-1));
    }
}
