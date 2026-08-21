namespace SmartX.Shared;

/// <summary>
/// One level of the deployment hierarchy — a facility, a zone within it, or a sub-zone.
/// </summary>
/// <remarks>
/// Zones reference their parent by id rather than by object reference, and the tree is reassembled
/// by <see cref="ZoneCatalogue"/>. That is deliberate: configuration arrives as a flat list from a
/// file or a table, and a flat list is exactly the shape that can express a malformed tree — a zone
/// whose parent does not exist, or a pair of zones that are each other's ancestor. Modelling this
/// with direct object references would make those states unrepresentable in memory while doing
/// nothing to stop them arriving in the input, which would only move the failure into whatever code
/// built the references.
/// </remarks>
/// <param name="Id">Stable identifier, unique across the catalogue.</param>
/// <param name="Name">Human-readable name, such as "Sub-Zone B".</param>
/// <param name="ParentId">
/// The zone this one sits inside, or <see langword="null"/> for a root. A null parent is the
/// recursion's base case.
/// </param>
/// <param name="Depth">
/// Declared distance from the root: 0 for a facility, 1 for a zone inside it, and so on.
/// <para>
/// This is derivable from the tree, which is exactly why it is worth carrying and checking. It
/// arrives from configuration as an assertion about where the zone sits, and
/// <see cref="ZoneValidator"/> compares it against the position actually walked. A mismatch means
/// the configuration disagrees with itself, and reporting that is better than trusting either half.
/// </para>
/// </param>
/// <param name="Constraints">Rules this level imposes on sensors at or beneath it.</param>
public sealed record DeploymentZone(
    string Id,
    string Name,
    string? ParentId,
    int Depth,
    ZoneConstraints Constraints)
{
    /// <summary>Is this a root of the hierarchy?</summary>
    public bool IsRoot => ParentId is null;
}
