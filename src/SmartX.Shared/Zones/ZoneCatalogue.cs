using System.Diagnostics.CodeAnalysis;

namespace SmartX.Shared;

/// <summary>
/// An in-memory <see cref="IZoneCatalogue"/> built from a flat list of zones.
/// </summary>
/// <remarks>
/// Indexes the list once on construction — by id for the upward walk, and by parent id for the
/// downward one — so neither recursion pays a linear scan per level. Building the indexes eagerly
/// also means a duplicate id is caught here, at the point the configuration is loaded, rather than
/// surfacing later as a zone that mysteriously resolves to the wrong parent.
/// <para>
/// It deliberately does <i>not</i> reject cycles or dangling parents. Detecting those is the
/// validator's job, and a catalogue that refused to hold a malformed tree would leave no way to
/// report what is wrong with one — the API could only answer "failed to load", which tells an
/// operator nothing about which zone to fix.
/// </para>
/// </remarks>
public sealed class ZoneCatalogue : IZoneCatalogue
{
    private static readonly DeploymentZone[] None = [];

    private readonly Dictionary<string, DeploymentZone> _byId;
    private readonly Dictionary<string, DeploymentZone[]> _byParentId;

    /// <summary>Indexes a flat list of zones.</summary>
    /// <exception cref="ArgumentException">Two zones share an id.</exception>
    public ZoneCatalogue(IEnumerable<DeploymentZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);

        All = [.. zones];

        _byId = new Dictionary<string, DeploymentZone>(All.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var zone in All)
        {
            if (!_byId.TryAdd(zone.Id, zone))
            {
                throw new ArgumentException(
                    $"Duplicate zone id '{zone.Id}'. Zone ids must be unique.", nameof(zones));
            }
        }

        _byParentId = All
            .Where(zone => zone.ParentId is not null)
            .GroupBy(zone => zone.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        Roots = [.. All.Where(zone => zone.IsRoot)];
    }

    /// <inheritdoc />
    public IReadOnlyList<DeploymentZone> All { get; }

    /// <inheritdoc />
    public IReadOnlyList<DeploymentZone> Roots { get; }

    /// <inheritdoc />
    public bool TryFind(string zoneId, [NotNullWhen(true)] out DeploymentZone? zone) =>
        _byId.TryGetValue(zoneId, out zone);

    /// <inheritdoc />
    public IReadOnlyList<DeploymentZone> ChildrenOf(string zoneId) =>
        _byParentId.TryGetValue(zoneId, out var children) ? children : None;
}
