namespace SmartX.Api.Storage;

/// <summary>Which deployment hierarchy the gateway serves.</summary>
public enum ZoneHierarchy
{
    /// <summary>The four zones from the brief. The default, and what the tests expect.</summary>
    Brief,

    /// <summary>The larger two-facility hierarchy the demo fleet is deployed into.</summary>
    Demo,
}

/// <summary>
/// Configuration for the deployment hierarchy.
/// </summary>
/// <remarks>
/// Part 1 tied the larger hierarchy to a "seed the mesh" switch, because the seeder registered devices into
/// it in-process. Devices now register themselves over HTTP, so the two are no longer one decision: the
/// gateway is configured with the zones it manages, and whichever devices show up are validated against
/// them.
/// </remarks>
public sealed class ZoneOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Zones";

    /// <summary>Which hierarchy to serve.</summary>
    public ZoneHierarchy Hierarchy { get; set; } = ZoneHierarchy.Brief;
}
