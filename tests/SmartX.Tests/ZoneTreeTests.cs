using SmartX.Shared;
using static SmartX.Tests.TestZones;

namespace SmartX.Tests;

/// <summary>
/// Covers the downward recursive walks that the topology view will be built on.
/// </summary>
public class ZoneTreeTests
{
    [Fact]
    public void Nodes_beneath_a_zone_include_every_level_under_it()
    {
        var catalogue = ThreeLevel();
        var sensors = new[]
        {
            Sensor("AA:BB:CC:DD:EE:01", zoneId: Facility),
            Sensor("AA:BB:CC:DD:EE:02", zoneId: Zone1),
            Sensor("AA:BB:CC:DD:EE:03", zoneId: SubZoneB)
        };

        var beneath = catalogue.NodesBeneath(Facility, sensors);

        Assert.Equal(3, beneath.Count);
    }

    [Fact]
    public void Nodes_beneath_a_zone_exclude_anything_above_it()
    {
        var catalogue = ThreeLevel();
        var sensors = new[]
        {
            Sensor("AA:BB:CC:DD:EE:01", zoneId: Facility),
            Sensor("AA:BB:CC:DD:EE:03", zoneId: SubZoneB)
        };

        var beneath = catalogue.NodesBeneath(Zone1, sensors);

        Assert.Equal("AA:BB:CC:DD:EE:03", Assert.Single(beneath).MacAddress);
    }

    [Fact]
    public void A_leaf_zone_returns_only_its_own_sensors()
    {
        // The descent's base case: ChildrenOf gives nothing back, so the loop body never runs.
        var catalogue = ThreeLevel();
        var sensors = new[] { Sensor("AA:BB:CC:DD:EE:03", zoneId: SubZoneB) };

        var beneath = catalogue.NodesBeneath(SubZoneB, sensors);

        Assert.Single(beneath);
    }

    [Fact]
    public void An_empty_subtree_returns_nothing()
    {
        var beneath = ThreeLevel().NodesBeneath(SubZoneB, []);

        Assert.Empty(beneath);
    }

    [Fact]
    public void A_descent_through_a_cycle_terminates_without_duplicating_sensors()
    {
        // ring-a and ring-b are each other's parent, so each is also the other's child. Without the
        // visited set the descent alternates between them forever, collecting the same sensor on
        // every lap.
        var sensors = new[] { Sensor("AA:BB:CC:DD:EE:01", zoneId: "ring-a") };

        var beneath = Cyclic().NodesBeneath("ring-a", sensors);

        Assert.Single(beneath);
    }

    [Fact]
    public void The_tree_is_nested_under_its_root()
    {
        var tree = ThreeLevel().BuildTree();

        var facility = Assert.Single(tree);
        Assert.Equal(Facility, facility.Zone.Id);

        var zone1 = Assert.Single(facility.Children);
        Assert.Equal(Zone1, zone1.Zone.Id);

        var subZone = Assert.Single(zone1.Children);
        Assert.Equal(SubZoneB, subZone.Zone.Id);
        Assert.Empty(subZone.Children);
    }

    [Fact]
    public void A_hierarchy_with_no_root_produces_no_tree()
    {
        // Both zones in a two-zone cycle have a parent, so neither is a root and neither appears.
        // The validator is what reports why; the tree simply has nowhere to hang them.
        Assert.Empty(Cyclic().BuildTree());
    }

    [Fact]
    public void Every_root_gets_its_own_tree()
    {
        var catalogue = new ZoneCatalogue(
            [Zone("north", null, 0), Zone("south", null, 0), Zone("south-wing", "south", 1)]);

        var tree = catalogue.BuildTree();

        Assert.Equal(2, tree.Count);
        Assert.Equal(["north", "south"], tree.Select(node => node.Zone.Id));
    }

    [Fact]
    public void A_duplicate_zone_id_is_rejected_when_the_catalogue_is_built()
    {
        // Caught at load time, because a duplicate id makes every later lookup ambiguous.
        Assert.Throws<ArgumentException>(() =>
            new ZoneCatalogue([Zone("same", null, 0), Zone("same", null, 0)]));
    }

    [Fact]
    public void Zone_ids_beneath_start_with_the_zone_and_include_every_level_under_it()
    {
        var ids = ThreeLevel().ZoneIdsBeneath(Facility);

        Assert.Equal([Facility, Zone1, SubZoneB], ids);
    }

    [Fact]
    public void Zone_ids_beneath_a_leaf_are_just_the_leaf()
    {
        Assert.Equal([SubZoneB], ThreeLevel().ZoneIdsBeneath(SubZoneB));
    }

    [Fact]
    public void Zone_ids_beneath_exclude_anything_above()
    {
        Assert.Equal([Zone1, SubZoneB], ThreeLevel().ZoneIdsBeneath(Zone1));
    }

    [Fact]
    public void Zone_ids_beneath_a_cycle_terminate_without_repeating_a_zone()
    {
        var ids = Cyclic().ZoneIdsBeneath("ring-a");

        Assert.Equal(ids.Distinct(StringComparer.OrdinalIgnoreCase), ids);
        Assert.Contains("ring-a", ids);
    }

    [Fact]
    public void Zone_ids_beneath_stop_at_the_depth_ceiling()
    {
        var ids = Chain(10).ZoneIdsBeneath(Chain(10).Roots[0].Id, maximumDepth: 3);

        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void Zone_ids_and_nodes_beneath_agree_on_which_zones_are_reached()
    {
        // One definition of "beneath": the sensors found by NodesBeneath are exactly those deployed in the
        // zones ZoneIdsBeneath names.
        var catalogue = ThreeLevel();
        var sensors = new[]
        {
            Sensor("AA:BB:CC:DD:EE:01", zoneId: Facility),
            Sensor("AA:BB:CC:DD:EE:02", zoneId: Zone1),
            Sensor("AA:BB:CC:DD:EE:03", zoneId: SubZoneB),
        };

        var ids = catalogue.ZoneIdsBeneath(Zone1).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            sensors.Where(sensor => ids.Contains(sensor.Location.ZoneId)).Select(sensor => sensor.MacAddress).Order(),
            catalogue.NodesBeneath(Zone1, sensors).Select(sensor => sensor.MacAddress).Order());
    }
}
