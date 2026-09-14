using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// Serves a fixed deployment hierarchy held in memory.
/// </summary>
/// <remarks>
/// The hierarchy is handed in rather than built here, so the same store serves either the brief's
/// hierarchy or the larger demo mesh the seeder uses, chosen once at startup.
/// <para>
/// The catalogue is built once and reused. Indexing it per request would be wasted work on data
/// that never changes, and the catalogue is immutable so sharing it across requests is safe.
/// </para>
/// </remarks>
internal sealed class InMemoryZoneStore(IZoneCatalogue catalogue) : IZoneStore
{
    private const string Celsius = "°C";

    /// <summary>The hierarchy used when the demo mesh is not seeded.</summary>
    /// <remarks>
    /// The example from the brief — Sub-Zone B inside Zone 1 inside Facility A — plus a second zone
    /// alongside, so the tree has a branch rather than being a single chain. The constraints are spread
    /// deliberately across levels: the tightened temperature range sits at Zone 1 and the category
    /// restriction at Zone 2, which makes inheritance observable from the client without needing to
    /// edit configuration.
    /// </remarks>
    public static IZoneCatalogue BriefHierarchy { get; } = new ZoneCatalogue(
    [
        new DeploymentZone(
            "facility-a", "Facility A", null, 0,
            new ZoneConstraints(
                permittedRanges: new Dictionary<string, ValueRange> { [Celsius] = new(-40, 85) })),

        new DeploymentZone(
            "zone-1", "Zone 1", "facility-a", 1,
            new ZoneConstraints(
                permittedRanges: new Dictionary<string, ValueRange> { [Celsius] = new(0, 50) })),

        new DeploymentZone(
            "sub-zone-b", "Sub-Zone B", "zone-1", 2, ZoneConstraints.Unrestricted),

        new DeploymentZone(
            "zone-2", "Zone 2", "facility-a", 1,
            new ZoneConstraints([SensorCategory.PowerConsumption]))
    ]);

    /// <inheritdoc />
    public Task<IZoneCatalogue> GetCatalogueAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(catalogue);
}
