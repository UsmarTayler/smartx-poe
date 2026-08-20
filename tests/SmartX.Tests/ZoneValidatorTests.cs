using SmartX.Shared;
using static SmartX.Tests.TestZones;

namespace SmartX.Tests;

/// <summary>
/// Covers the upward recursive walk: constraint inheritance, and the guards that keep a malformed
/// hierarchy from taking the process down with it.
/// </summary>
public class ZoneValidatorTests
{
    [Fact]
    public void A_sensor_that_satisfies_every_level_of_a_three_level_tree_is_valid()
    {
        var validator = new ZoneValidator(ThreeLevel());

        var result = validator.Validate(Sensor());

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
        Assert.Equal(2, result.DepthFromRoot);
    }

    [Fact]
    public void A_root_zone_with_no_parent_is_the_base_case()
    {
        // The single-node hierarchy. The walk checks one level, finds no parent, and stops — no
        // recursion happens at all, which is the case a recursive method most often gets wrong.
        var validator = new ZoneValidator(new ZoneCatalogue([Zone("solo", null, 0)]));

        var result = validator.Validate(Sensor(zoneId: "solo"));

        Assert.True(result.IsValid);
        Assert.Equal(0, result.DepthFromRoot);
    }

    [Fact]
    public void A_constraint_on_a_grandparent_still_rejects_the_sensor()
    {
        // Only Facility A restricts categories. Sub-Zone B and Zone 1 permit anything, so nothing
        // about the sensor's immediate surroundings explains the rejection — the reason is two
        // levels up, and this is the case the recursion exists for.
        var validator = new ZoneValidator(ThreeLevel());

        var result = validator.Validate(Sensor(category: SensorCategory.Actuator));

        var violation = Assert.Single(result.Violations);
        Assert.Equal(ZoneViolationKind.CategoryNotPermitted, violation.Kind);
        Assert.Equal(Facility, violation.ZoneId);
    }

    [Fact]
    public void Every_level_that_objects_is_reported_not_only_the_first()
    {
        // -50 to 100 °C breaches Zone 1's 0–50 and Facility A's -40–85. An operator fixing only the
        // first would be told about the second on the next attempt, which is why the walk collects
        // both.
        var validator = new ZoneValidator(ThreeLevel());

        var result = validator.Validate(Sensor(minimum: -50, maximum: 100));

        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Violations, v => Assert.Equal(ZoneViolationKind.ValueRangeNotPermitted, v.Kind));

        // Ordered from the sensor's own zone upwards, so the nearest objection comes first.
        Assert.Equal([Zone1, Facility], result.Violations.Select(v => v.ZoneId));
    }

    [Fact]
    public void Inherited_constraints_are_narrowed_level_by_level()
    {
        // Facility A permits -40 to 85 °C and Zone 1 permits 0 to 50. The effective range beneath
        // both is the intersection, which appears in no single zone.
        var validator = new ZoneValidator(ThreeLevel());

        var result = validator.Validate(Sensor());

        Assert.Equal(new ValueRange(0, 50), result.EffectiveConstraints.PermittedRanges[Celsius]);
        Assert.Equal(
            [SensorCategory.Environmental, SensorCategory.PowerConsumption],
            result.EffectiveConstraints.PermittedCategories);
    }

    [Fact]
    public void A_cycle_is_reported_rather_than_overflowing_the_stack()
    {
        // ring-a's parent is ring-b, whose parent is ring-a. Without the visited set this call
        // never returns.
        var validator = new ZoneValidator(Cyclic());

        var result = validator.Validate(Sensor(zoneId: "ring-a"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Kind == ZoneViolationKind.CycleDetected);
        Assert.Equal(ZoneValidationResult.UnknownDepth, result.DepthFromRoot);
    }

    [Fact]
    public void A_tree_deeper_than_the_limit_fails_cleanly()
    {
        // Ten acyclic levels against a ceiling of three. The visited set cannot help here — nothing
        // is revisited — so this is the guard that has to fire.
        var validator = new ZoneValidator(Chain(10), maximumDepth: 3);

        var result = validator.Validate(Sensor(zoneId: "level-9"));

        Assert.Contains(result.Violations, v => v.Kind == ZoneViolationKind.MaximumDepthExceeded);
        Assert.Equal(ZoneValidationResult.UnknownDepth, result.DepthFromRoot);
    }

    [Fact]
    public void A_tree_within_the_limit_is_walked_to_the_root()
    {
        var validator = new ZoneValidator(Chain(10), maximumDepth: 32);

        var result = validator.Validate(Sensor(zoneId: "level-9"));

        Assert.True(result.IsValid);
        Assert.Equal(9, result.DepthFromRoot);
    }

    [Fact]
    public void Registering_into_a_zone_that_does_not_exist_is_reported()
    {
        var validator = new ZoneValidator(ThreeLevel());

        var result = validator.Validate(Sensor(zoneId: "nowhere"));

        var violation = Assert.Single(result.Violations);
        Assert.Equal(ZoneViolationKind.ZoneNotFound, violation.Kind);
    }

    [Fact]
    public void A_zone_naming_a_parent_that_is_missing_is_reported()
    {
        var validator = new ZoneValidator(new ZoneCatalogue([Zone("orphan", "ghost", 1)]));

        var result = validator.Validate(Sensor(zoneId: "orphan"));

        Assert.Contains(result.Violations, v => v.Kind == ZoneViolationKind.ParentNotFound);
    }

    [Fact]
    public void A_zone_whose_declared_depth_is_wrong_is_reported()
    {
        // The zone claims to sit three levels down while actually hanging directly off a root.
        var validator = new ZoneValidator(
            new ZoneCatalogue([Zone("root", null, 0), Zone("liar", "root", 3)]));

        var result = validator.Validate(Sensor(zoneId: "liar"));

        var violation = Assert.Single(result.Violations);
        Assert.Equal(ZoneViolationKind.DepthMismatch, violation.Kind);
    }

    [Fact]
    public void Ancestors_permitting_ranges_that_share_no_values_are_reported()
    {
        var catalogue = new ZoneCatalogue(
        [
            new DeploymentZone("top", "Top", null, 0, new ZoneConstraints(
                permittedRanges: new Dictionary<string, ValueRange> { [Celsius] = new(0, 10) })),
            new DeploymentZone("bottom", "Bottom", "top", 1, new ZoneConstraints(
                permittedRanges: new Dictionary<string, ValueRange> { [Celsius] = new(50, 60) }))
        ]);

        var result = new ZoneValidator(catalogue).Validate(Sensor(zoneId: "bottom", minimum: 1, maximum: 5));

        Assert.Contains(result.Violations, v => v.Kind == ZoneViolationKind.ConflictingConstraints);
    }

    [Fact]
    public void A_state_sensor_is_not_range_checked()
    {
        // A bool has no magnitude, so the °C range at Zone 1 has nothing to say about it. The
        // category check still applies, which is why this sensor sits in a zone that allows
        // actuators.
        var catalogue = new ZoneCatalogue([Zone("open", null, 0)]);

        var result = new ZoneValidator(catalogue).Validate(
            Sensor(zoneId: "open", category: SensorCategory.Actuator,
                   unit: "state", payloadKind: TelemetryPayloadKind.State));

        Assert.True(result.IsValid);
    }
}
