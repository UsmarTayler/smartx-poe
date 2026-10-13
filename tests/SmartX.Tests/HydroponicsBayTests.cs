using Microsoft.Extensions.Options;
using SmartX.Api.Patterns;
using SmartX.DeviceSimulator;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>The hydroponics bay added to the demo fleet for the suggestion engine, and the scenario that makes it cross 20 %.</summary>
public class HydroponicsBayTests
{
    private static DemoDevice Device(string name) =>
        DemoDevices.All.Single(device => device.Registration.Room == name);

    [Theory]
    [InlineData("Moisture Sensor A")]
    [InlineData("Moisture Sensor B")]
    [InlineData("Nutrient EC Sensor")]
    [InlineData("Pump A")]
    [InlineData("Pump B")]
    [InlineData("Grow Light")]
    [InlineData("Drain Valve")]
    public void The_bay_has_the_devices_the_brief_names_in_one_zone(string name)
    {
        Assert.Equal("zone-6", Device(name).Registration.ZoneId);
    }

    [Fact]
    public void Moisture_is_registered_from_0_to_100_percent_so_the_low_watch_level_is_literally_20()
    {
        var moisture = Device("Moisture Sensor B").Registration;

        Assert.Equal("%", moisture.Unit);
        Assert.Equal(0, moisture.Minimum);
        Assert.Equal(100, moisture.Maximum);
        Assert.Equal(20, new PatternOptions().WatchLowFraction * 100, precision: 6);
    }

    [Fact]
    public void The_two_pumps_are_pumps_and_the_valve_is_a_valve()
    {
        Assert.Equal(ActuatorKind.Pump, Device("Pump A").Registration.ActuatorKind);
        Assert.Equal(ActuatorKind.Pump, Device("Pump B").Registration.ActuatorKind);
        Assert.Equal(ActuatorKind.Valve, Device("Drain Valve").Registration.ActuatorKind);
        Assert.Equal(ActuatorKind.Dimmer, Device("Grow Light").Registration.ActuatorKind);
    }

    [Fact]
    public void Moisture_Sensor_B_starts_in_the_dry_out_cycle_and_the_others_do_not()
    {
        Assert.Equal(SimulationScenario.DryOut, Device("Moisture Sensor B").Scenario);
        Assert.Equal(SimulationScenario.Nominal, Device("Moisture Sensor A").Scenario);
    }

    [Fact]
    public void The_dry_out_scenario_crosses_20_percent_once_a_cycle_and_never_leaves_the_range()
    {
        var definition = Device("Moisture Sensor B");
        var device = new SimulatedDevice(definition);
        var random = new Random(7);

        var directory = new FakeDirectory().Add(
            definition.MacAddress, "Moisture Sensor B", "Environmental/%", "% sensor", "zone-6", "zone-6", "Zone 6");
        var detector = new ReadingTriggerDetector(directory, Options.Create(new PatternOptions()));

        var crossings = 0;
        var lowest = double.MaxValue;
        var highest = double.MinValue;

        // Three cycles of eighty readings, after the thirty it starts in.
        for (var index = 0; index < 240; index++)
        {
            var value = device.NextReading(SimulationScenario.DryOut, random).GetDouble();

            lowest = Math.Min(lowest, value);
            highest = Math.Max(highest, value);

            if (detector.ObserveReading(definition.MacAddress, value, T0.AddSeconds(index * 4)) is { Detail: "Low" })
            {
                crossings++;
            }
        }

        Assert.Equal(3, crossings);
        Assert.InRange(lowest, 0, 20);
        Assert.InRange(highest, 20, 100);
    }

    [Fact]
    public void An_actuator_cannot_be_given_the_dry_out_scenario()
    {
        Assert.False(new SimulatedDevice(Device("Pump A")).Supports(SimulationScenario.DryOut));
        Assert.True(new SimulatedDevice(Device("Moisture Sensor A")).Supports(SimulationScenario.DryOut));
    }
}
