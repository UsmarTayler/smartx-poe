using System.Diagnostics.CodeAnalysis;

namespace SmartX.Shared;

/// <summary>
/// Read access to the deployment hierarchy, in both directions.
/// </summary>
/// <remarks>
/// An interface rather than a concrete type because the two recursions need opposite views of the
/// same tree — <see cref="ZoneValidator"/> walks up through <see cref="TryFind"/>, and the descent
/// in <see cref="ZoneTree"/> walks down through <see cref="ChildrenOf"/>. Naming that pair as a
/// contract also means the eventual database-backed catalogue is a drop-in replacement.
/// </remarks>
public interface IZoneCatalogue
{
    /// <summary>Every zone in the catalogue.</summary>
    IReadOnlyList<DeploymentZone> All { get; }

    /// <summary>Zones with no parent.</summary>
    /// <remarks>
    /// A zone caught in a parent cycle is not a root and is not reachable from one, so a tree whose
    /// roots do not account for every zone in <see cref="All"/> is misconfigured.
    /// </remarks>
    IReadOnlyList<DeploymentZone> Roots { get; }

    /// <summary>Looks a zone up by id.</summary>
    /// <returns><see langword="false"/> when no zone carries that id.</returns>
    bool TryFind(string zoneId, [NotNullWhen(true)] out DeploymentZone? zone);

    /// <summary>The zones whose parent is the given id, or an empty list if there are none.</summary>
    IReadOnlyList<DeploymentZone> ChildrenOf(string zoneId);
}
