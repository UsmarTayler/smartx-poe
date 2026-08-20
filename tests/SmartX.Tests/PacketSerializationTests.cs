using System.Text.Json;
using SmartX.Shared;
using static SmartX.Tests.TestPackets;

namespace SmartX.Tests;

/// <summary>
/// Confirms packets survive the trip across the API boundary intact.
/// </summary>
/// <remarks>
/// These types exist to be sent from the API to the client, so serialisation is part of their
/// contract rather than an implementation detail. The round-trip assertions lean on the value
/// equality defined on <see cref="TelemetryPacket{T}"/>: if any field is dropped or altered in
/// transit, the rebuilt packet stops being equal to the original.
/// <para>
/// Uses <see cref="JsonSerializerDefaults.Web"/> because that is what ASP.NET Core and the Blazor
/// client both use. Testing against the default options would verify a configuration neither side
/// actually runs.
/// </para>
/// </remarks>
public class PacketSerializationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_float_reading_round_trips_unchanged()
    {
        var original = Numeric(21.5f, unit: "°C", category: SensorCategory.Environmental);

        var json = JsonSerializer.Serialize(original, WebOptions);
        var restored = JsonSerializer.Deserialize<NumericPacket<float>>(json, WebOptions);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void An_int_reading_round_trips_unchanged()
    {
        var original = Numeric(1200);

        var json = JsonSerializer.Serialize(original, WebOptions);
        var restored = JsonSerializer.Deserialize<NumericPacket<int>>(json, WebOptions);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void An_actuator_reading_round_trips_unchanged()
    {
        var original = State(true);

        var json = JsonSerializer.Serialize(original, WebOptions);
        var restored = JsonSerializer.Deserialize<StatePacket>(json, WebOptions);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void A_restored_reading_can_still_be_aggregated()
    {
        // Round-tripping has to preserve the unit as well as the number, or the operators start
        // rejecting packets that arrived over the wire.
        var meter1 = Numeric(1200);
        var meter2 = Numeric(850, nodeId: NodeB);

        var restored1 = JsonSerializer.Deserialize<NumericPacket<int>>(
            JsonSerializer.Serialize(meter1, WebOptions), WebOptions)!;
        var restored2 = JsonSerializer.Deserialize<NumericPacket<int>>(
            JsonSerializer.Serialize(meter2, WebOptions), WebOptions)!;

        Assert.Equal(2050, (restored1 + restored2).Value);
    }

    [Fact]
    public void The_category_travels_as_a_name_rather_than_a_number()
    {
        // Guards the JsonStringEnumConverter on SensorCategory. If it were ever removed, the wire
        // format would silently become ordinal-dependent and inserting a new enum member would
        // change the meaning of already-stored telemetry.
        var original = Numeric(1200);

        var json = JsonSerializer.Serialize(original, WebOptions);

        Assert.Contains("\"PowerConsumption\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_timestamp_keeps_its_offset()
    {
        // A reading that loses its offset in transit is a reading that can no longer be ordered
        // against one from a node in another time zone.
        var original = Numeric(1200, timestamp: new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(2)));

        var json = JsonSerializer.Serialize(original, WebOptions);
        var restored = JsonSerializer.Deserialize<NumericPacket<int>>(json, WebOptions)!;

        Assert.Equal(TimeSpan.FromHours(2), restored.Timestamp.Offset);
    }
}
