using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// The rules a single zone imposes on any sensor deployed at or beneath it.
/// </summary>
/// <remarks>
/// Constraints are declared per level and inherited downwards, so a sensor must satisfy its own
/// zone's rules and every ancestor's as well. A facility can forbid actuators outright and every
/// sub-zone beneath it inherits that prohibition without restating it.
/// <para>
/// <b>An empty collection means "this level adds no restriction", not "nothing is permitted".</b>
/// That convention is what lets an intermediate zone exist purely for structure. The alternative —
/// null for unrestricted, empty for nothing-permitted — draws a distinction that reads identically
/// at most call sites and is a reliable source of bugs.
/// </para>
/// </remarks>
public sealed class ZoneConstraints
{
    /// <summary>A zone that imposes nothing of its own.</summary>
    public static readonly ZoneConstraints Unrestricted = new();

    /// <summary>Creates a constraint set.</summary>
    /// <param name="permittedCategories">
    /// Sensor categories allowed at this level. Empty permits all of them.
    /// </param>
    /// <param name="permittedRanges">
    /// Permitted value ranges keyed by unit. A unit with no entry is unconstrained at this level;
    /// restricting which sensors may be deployed is what
    /// <paramref name="permittedCategories"/> is for.
    /// </param>
    [JsonConstructor]
    public ZoneConstraints(
        IReadOnlyList<SensorCategory>? permittedCategories = null,
        IReadOnlyDictionary<string, ValueRange>? permittedRanges = null)
    {
        PermittedCategories = permittedCategories ?? [];
        PermittedRanges = permittedRanges ?? new Dictionary<string, ValueRange>(StringComparer.Ordinal);
    }

    /// <summary>Sensor categories allowed at this level. Empty permits all of them.</summary>
    /// <remarks>
    /// A list rather than a set, deliberately. <see cref="SensorCategory"/> has three members, so
    /// this collection is never longer than three items — a linear scan over three enum values beats
    /// hashing one, and a list serialises to a JSON array without needing a converter.
    /// </remarks>
    public IReadOnlyList<SensorCategory> PermittedCategories { get; }

    /// <summary>Permitted value ranges, keyed by unit.</summary>
    public IReadOnlyDictionary<string, ValueRange> PermittedRanges { get; }

    /// <summary>Does this level permit the given category?</summary>
    public bool Permits(SensorCategory category) =>
        PermittedCategories.Count == 0 || PermittedCategories.Contains(category);
}
