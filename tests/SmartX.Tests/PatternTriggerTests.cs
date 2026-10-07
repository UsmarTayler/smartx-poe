using Microsoft.Extensions.Options;
using SmartX.Api.Patterns;
using SmartX.Api.Search;
using SmartX.Api.Storage;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>
/// Turning readings into triggers: watch levels derived from the registered range, hysteresis so a hovering reading fires once,
/// and a critical episode counting as one trigger.
/// </summary>
public class ReadingTriggerDetectorTests
{
    private static ReadingTriggerDetector Detector(Action<PatternOptions>? configure = null)
    {
        var options = new PatternOptions();
        configure?.Invoke(options);

        return new ReadingTriggerDetector(Directory(), Options.Create(options));
    }

    /// <summary>Feeds readings in order and returns the sides of the watch levels that fired.</summary>
    private static List<string> Fired(ReadingTriggerDetector detector, string mac, params double[] values)
    {
        List<string> fired = [];

        for (var index = 0; index < values.Length; index++)
        {
            if (detector.ObserveReading(mac, values[index], T0 + TimeSpan.FromSeconds(index)) is { } trigger)
            {
                Assert.Equal(TriggerKind.ThresholdCrossing, trigger.Kind);
                fired.Add(trigger.Detail);
            }
        }

        return fired;
    }

    [Fact]
    public void A_reading_that_drops_below_twenty_percent_of_the_range_fires_once()
    {
        Assert.Equal(["Low"], Fired(Detector(), MoistB, 40, 30, 19, 15, 10));
    }

    [Fact]
    public void A_reading_hovering_around_the_level_fires_once_and_not_on_every_packet()
    {
        // 19, 21, 19, 22, 18, 24: it keeps touching 20 and never gets back above the re-arm level of 25.
        Assert.Equal(["Low"], Fired(Detector(), MoistB, 30, 19, 21, 19, 22, 18, 24, 19));
    }

    [Fact]
    public void The_level_is_re_armed_only_above_the_re_arm_level()
    {
        // Back to 24 is not enough; back to 26 is. So the first dip fires, and the third does not until after the 26.
        Assert.Equal(["Low", "Low"], Fired(Detector(), MoistB, 19, 24, 19, 26, 19));
        Assert.Equal(["Low"], Fired(Detector(), MoistB, 19, 25, 19));
    }

    [Fact]
    public void The_high_side_has_its_own_level_and_its_own_hysteresis()
    {
        // 80 fires, 76 is not back far enough (the level is re-armed below 75), 74 is.
        Assert.Equal(["High", "High"], Fired(Detector(), MoistB, 50, 81, 76, 82, 74, 85));
    }

    [Fact]
    public void The_levels_follow_the_registered_range_and_nothing_has_to_be_configured_per_sensor()
    {
        // The temperature sensor is registered for 0 to 50, so its low level is 10 and its high level 40.
        Assert.Equal(["Low"], Fired(Detector(), Temperature, 12, 11, 9));
        Assert.Empty(Fired(Detector(), Temperature, 12, 15, 30, 39));
        Assert.Equal(["High"], Fired(Detector(), Temperature, 30, 41));
    }

    [Fact]
    public void The_fractions_and_the_margin_are_configurable()
    {
        var detector = Detector(options =>
        {
            options.WatchLowFraction = 0.4;
            options.RearmMargin = 0.1;
        });

        // The low level is now 40 and it is re-armed above 50.
        Assert.Equal(["Low", "Low"], Fired(detector, MoistB, 39, 45, 39, 51, 39));
    }

    [Fact]
    public void Each_sensor_keeps_its_own_state()
    {
        var detector = Detector();

        Assert.Equal(["Low"], Fired(detector, MoistB, 10));
        Assert.Equal(["Low"], Fired(detector, MoistC, 10));
        Assert.Empty(Fired(detector, MoistB, 10));
    }

    [Fact]
    public void An_on_off_sensor_and_an_unknown_one_have_no_level_to_cross()
    {
        var detector = Detector();

        Assert.Empty(Fired(detector, ValveState, 0, 1, 0));
        Assert.Empty(Fired(detector, "B8:27:EB:99:99:99", 0, 5));
    }

    [Fact]
    public void A_critical_episode_is_one_trigger_however_many_critical_readings_it_has()
    {
        var detector = Detector();

        var first = detector.ObserveCritical(MoistB, "moisture crash", T0);

        Assert.Equal(TriggerKind.CriticalReading, first?.Kind);
        Assert.Equal("moisture crash", first?.Detail);

        Assert.Null(detector.ObserveCritical(MoistB, "moisture crash", T0.AddSeconds(5)));
        Assert.Null(detector.ObserveCritical(MoistB, "moisture crash", T0.AddSeconds(10)));

        // A normal reading ends the episode, and the next critical one begins another.
        Assert.Null(detector.ObserveCritical(MoistB, null, T0.AddSeconds(15)));
        Assert.NotNull(detector.ObserveCritical(MoistB, "moisture crash", T0.AddSeconds(20)));
    }
}

/// <summary>The device directory walks the Part 1 zone tree, so that "the same zone" means something.</summary>
public class DeviceDirectoryTests
{
    private static SensorRegistration Sensor(string mac, string zone, SensorCategory category = SensorCategory.Environmental, string unit = "%RH") =>
        new(mac, new DeploymentLocation("Room", zone, "N"), category, unit, new ValueRange(0, 100), TelemetryPayloadKind.Real);

    private static DeviceDirectory Directory(params SensorRegistration[] sensors)
    {
        var index = new DeviceIndex();

        foreach (var sensor in sensors)
        {
            index.Register(sensor);
        }

        return new DeviceDirectory(index, Options.Create(new PatternOptions()))
        {
            // The brief's hierarchy: Facility A, then Zone 1 and Zone 2 beneath it, then Sub-Zone B beneath Zone 1.
            Catalogue = InMemoryZoneStore.BriefHierarchy,
        };
    }

    [Fact]
    public void A_sensor_in_a_sub_zone_is_grouped_with_its_zone()
    {
        var directory = Directory(
            Sensor("B8:27:EB:30:00:01", "sub-zone-b"),
            Sensor("B8:27:EB:30:00:02", "zone-1"),
            Sensor("B8:27:EB:30:00:03", "zone-2"));

        Assert.True(directory.TryDescribe("B8:27:EB:30:00:01", out var inSub));
        Assert.True(directory.TryDescribe("B8:27:EB:30:00:02", out var inZone));
        Assert.True(directory.TryDescribe("B8:27:EB:30:00:03", out var other));

        // Sub-Zone B is beneath Zone 1, so both are "in Zone 1", and Zone 2 is not.
        Assert.Equal("zone-1", inSub.GeneralZoneId);
        Assert.Equal("Zone 1", inSub.GeneralZoneName);
        Assert.Equal(inSub.GeneralZoneId, inZone.GeneralZoneId);
        Assert.Equal("zone-2", other.GeneralZoneId);
    }

    [Fact]
    public void The_depth_the_general_level_groups_at_is_configurable()
    {
        var index = new DeviceIndex();
        index.Register(Sensor("B8:27:EB:30:00:01", "sub-zone-b"));

        var directory = new DeviceDirectory(index, Options.Create(new PatternOptions { GeneralZoneDepth = 0 })) { Catalogue = InMemoryZoneStore.BriefHierarchy };

        Assert.True(directory.TryDescribe("B8:27:EB:30:00:01", out var profile));

        // Depth 0 is the facility, so everything in it is one group.
        Assert.Equal("facility-a", profile.GeneralZoneId);
    }

    [Fact]
    public void A_sensor_is_classed_by_what_it_measures_and_an_actuator_by_what_it_is()
    {
        var pump = new SensorRegistration(
            "B8:27:EB:30:00:04", new DeploymentLocation("Pump", "zone-1", "N"), SensorCategory.Actuator, "state",
            new ValueRange(0, 1), TelemetryPayloadKind.State, null, ActuatorKind.Pump);

        var directory = Directory(Sensor("B8:27:EB:30:00:01", "zone-1"), pump);

        Assert.True(directory.TryDescribe("B8:27:EB:30:00:01", out var moisture));
        Assert.True(directory.TryDescribe("B8:27:EB:30:00:04", out var actuator));

        Assert.Equal("Environmental/%RH", moisture.Class);
        Assert.False(moisture.IsState);
        Assert.Equal("Actuator/Pump", actuator.Class);
        Assert.Equal("pump", actuator.ClassLabel);
        Assert.True(actuator.IsState);
    }

    [Fact]
    public void A_device_that_is_not_registered_is_not_described()
    {
        Assert.False(Directory().TryDescribe("B8:27:EB:99:99:99", out _));
    }
}
