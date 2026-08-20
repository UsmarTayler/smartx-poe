using System.Net.Http.Json;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the zone tree the client renders.
/// </summary>
public class ZoneEndpointTests
{
    [Fact]
    public async Task The_hierarchy_is_returned_already_nested()
    {
        using var api = New();
        var client = api.CreateClient();

        var tree = await client.GetFromJsonAsync<List<ZoneTreeNode>>("/api/zones");

        var facility = Assert.Single(tree ?? []);
        Assert.Equal("facility-a", facility.Zone.Id);
        Assert.Equal(0, facility.Zone.Depth);

        // Zone 1 and Zone 2 both hang off the facility.
        Assert.Equal(["zone-1", "zone-2"], facility.Children.Select(child => child.Zone.Id).Order());

        var zone1 = facility.Children.Single(child => child.Zone.Id == "zone-1");
        Assert.Equal("sub-zone-b", Assert.Single(zone1.Children).Zone.Id);
    }

    [Fact]
    public async Task Zone_constraints_travel_with_the_tree()
    {
        // The client needs these to explain a rejected registration without a second round trip.
        using var api = New();
        var client = api.CreateClient();

        var tree = await client.GetFromJsonAsync<List<ZoneTreeNode>>("/api/zones");

        var zone1 = tree!.Single().Children.Single(child => child.Zone.Id == "zone-1");
        Assert.Equal(new ValueRange(0, 50), zone1.Zone.Constraints.PermittedRanges["°C"]);

        var zone2 = tree!.Single().Children.Single(child => child.Zone.Id == "zone-2");
        Assert.Equal([SensorCategory.PowerConsumption], zone2.Zone.Constraints.PermittedCategories);
    }
}
