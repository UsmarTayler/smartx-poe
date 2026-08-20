using SmartX.Shared;
using static SmartX.Tests.TestPackets;

namespace SmartX.Tests;

/// <summary>
/// Covers the logical operators on <see cref="StatePacket"/>.
/// </summary>
/// <remarks>
/// The two truth tables are driven as theories because that is exactly what they are: four input
/// combinations against one rule. Writing them as four separate facts each would say the same thing
/// at greater length.
/// </remarks>
public class StatePacketTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Or_reports_engaged_when_any_actuator_is(bool left, bool right, bool expected)
    {
        var combined = State(left) | State(right);

        Assert.Equal(expected, combined.Value);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void And_reports_engaged_only_when_every_actuator_is(bool left, bool right, bool expected)
    {
        var combined = State(left) & State(right);

        Assert.Equal(expected, combined.Value);
    }

    [Fact]
    public void Combining_returns_a_packet_so_a_whole_group_can_be_reduced()
    {
        // The reason | and & return StatePacket rather than bool: the result is itself a reading and
        // can be fed back in, so an arbitrary group collapses to one answer.
        var anyEngaged = State(false) | State(false) | State(true);

        Assert.True(anyEngaged.Value);
    }

    [Fact]
    public void An_interlock_group_is_engaged_only_when_all_of_it_is()
    {
        var allEngaged = State(true) & State(true) & State(false);

        Assert.False(allEngaged.Value);
    }

    [Fact]
    public void Combining_across_nodes_attributes_the_result_to_no_single_node()
    {
        var pump = State(true, nodeId: NodeA);
        var valve = State(false, nodeId: NodeB);

        var combined = pump | valve;

        Assert.Equal(TelemetryPacket<bool>.AggregateNodeId, combined.NodeId);
    }

    [Fact]
    public void Combined_reading_carries_the_later_timestamp()
    {
        var earlier = State(true, timestamp: Noon);
        var later = State(false, timestamp: Noon.AddMinutes(5));

        var combined = earlier & later;

        Assert.Equal(Noon.AddMinutes(5), combined.Timestamp);
    }

    [Fact]
    public void A_state_packet_is_always_an_actuator()
    {
        var packet = State(true);

        Assert.Equal(SensorCategory.Actuator, packet.Category);
        Assert.Equal(StatePacket.DefaultUnit, packet.Unit);
    }

    [Fact]
    public void Combining_actuators_that_mean_different_things_throws()
    {
        var relay = new StatePacket(NodeA, true, Noon, "on/off");
        var valve = new StatePacket(NodeB, true, Noon, "open/closed");

        Assert.Throws<InvalidOperationException>(() => relay | valve);
        Assert.Throws<InvalidOperationException>(() => relay & valve);
    }

    [Fact]
    public void A_null_operand_is_rejected()
    {
        var packet = State(true);

        Assert.Throws<ArgumentNullException>(() => packet | null!);
        Assert.Throws<ArgumentNullException>(() => packet & null!);
    }

    [Fact]
    public void Packets_with_identical_fields_are_equal()
    {
        var left = State(true);
        var right = State(true);

        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
