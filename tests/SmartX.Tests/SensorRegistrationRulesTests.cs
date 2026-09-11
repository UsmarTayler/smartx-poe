using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Covers the per-field rules the client form and the API share.
/// </summary>
/// <remarks>
/// The last test is the one that matters most: it proves the API reports exactly the messages the
/// rules produce, which is the guarantee that the form's as-you-type validation and the server's
/// parsing cannot disagree.
/// </remarks>
public class SensorRegistrationRulesTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("aa-bb-cc-dd-ee-01")]
    public void A_well_formed_mac_address_passes(string value)
    {
        Assert.Null(SensorRegistrationRules.MacAddress(value));
    }

    [Theory]
    [InlineData("AA:BB:CC")]
    [InlineData("AA:BB-CC:DD:EE:01")]
    [InlineData("00:00:00:00:00:00")]
    public void A_malformed_or_reserved_mac_address_is_explained(string value)
    {
        Assert.Contains("six hex pairs", SensorRegistrationRules.MacAddress(value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_mac_address_says_it_is_required_rather_than_malformed(string? value)
    {
        Assert.Equal("A MAC address is required.", SensorRegistrationRules.MacAddress(value));
    }

    [Fact]
    public void An_inverted_range_names_both_bounds()
    {
        Assert.Equal("Minimum 40 exceeds maximum 10.", SensorRegistrationRules.Range(40, 10));
        Assert.Null(SensorRegistrationRules.Range(10, 40));
        Assert.Null(SensorRegistrationRules.Range(10, 10));
    }

    [Fact]
    public void An_empty_bound_is_required_rather_than_treated_as_zero()
    {
        Assert.Equal("An expected minimum is required.", SensorRegistrationRules.RangeBound(null, "minimum"));
        Assert.Null(SensorRegistrationRules.RangeBound(0, "minimum"));
    }

    [Fact]
    public void A_state_payload_is_only_allowed_for_an_actuator()
    {
        Assert.NotNull(SensorRegistrationRules.PayloadKind(TelemetryPayloadKind.State, SensorCategory.Environmental));
        Assert.Null(SensorRegistrationRules.PayloadKind(TelemetryPayloadKind.State, SensorCategory.Actuator));
        Assert.Null(SensorRegistrationRules.PayloadKind(TelemetryPayloadKind.Real, SensorCategory.Actuator));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1, true)]
    [InlineData(null, true)]
    public void A_heartbeat_interval_if_given_must_be_positive(int? seconds, bool valid)
    {
        Assert.Equal(valid, SensorRegistrationRules.HeartbeatIntervalSeconds(seconds) is null);
    }

    [Fact]
    public void The_api_request_reports_exactly_the_messages_the_rules_produce()
    {
        var request = new SensorRegistrationRequest(
            "not-a-mac", "", "zone-1", "NODE-1", SensorCategory.Environmental, "°C", 40, 10,
            TelemetryPayloadKind.Real, HeartbeatIntervalSeconds: 0);

        Assert.False(request.TryCreateRegistration(out _, out var errors));

        Assert.Equal(SensorRegistrationRules.MacAddress("not-a-mac"), Assert.Single(errors[nameof(request.MacAddress)]));
        Assert.Equal(SensorRegistrationRules.Room(""), Assert.Single(errors[nameof(request.Room)]));
        Assert.Equal(SensorRegistrationRules.Range(40, 10), Assert.Single(errors[nameof(request.Minimum)]));
        Assert.Equal(SensorRegistrationRules.HeartbeatIntervalSeconds(0), Assert.Single(errors[nameof(request.HeartbeatIntervalSeconds)]));
    }
}
