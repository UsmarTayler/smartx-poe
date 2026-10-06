namespace SmartX.Shared;

/// <summary>
/// Recursive walks down the deployment hierarchy.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="ZoneValidator"/>, which walks up. Both carry the same two guards —
/// a visited set and a depth ceiling — because a cyclic configuration is just as capable of
/// trapping a descent as an ascent, and neither walk can assume the tree it was handed is
/// well-formed.
/// <para>
/// Extension methods on <see cref="IZoneCatalogue"/> rather than members of it: these are
/// derivable from <see cref="IZoneCatalogue.ChildrenOf"/>, and making every future catalogue
/// implementation reimplement them would invite two implementations that walk differently.
/// </para>
/// </remarks>
public static class ZoneTree
{
    /// <summary>
    /// Collects every sensor deployed in a zone or anywhere beneath it.
    /// </summary>
    /// <param name="catalogue">The hierarchy to walk.</param>
    /// <param name="zoneId">The zone to start from.</param>
    /// <param name="sensors">The sensors to draw from.</param>
    /// <param name="maximumDepth">How many levels to descend before stopping.</param>
    /// <returns>
    /// Sensors in the named zone and in every zone below it. Empty when the zone has no sensors and
    /// no populated descendants.
    /// </returns>
    /// <remarks>
    /// Includes sensors in the named zone itself, not only in its children: a sensor sitting
    /// directly in Zone 1 is part of Zone 1's topology.
    /// <para>
    /// The sensors are indexed by zone once up front rather than being re-scanned at each level.
    /// Walking the list per zone would turn a subtree of n zones over m sensors into n×m
    /// comparisons; indexing first makes it n+m.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SensorRegistration> NodesBeneath(
        this IZoneCatalogue catalogue,
        string zoneId,
        IEnumerable<SensorRegistration> sensors,
        int maximumDepth = ZoneValidator.DefaultMaximumDepth)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentNullException.ThrowIfNull(sensors);

        var byZone = new Dictionary<string, List<SensorRegistration>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sensor in sensors)
        {
            if (!byZone.TryGetValue(sensor.Location.ZoneId, out var inZone))
            {
                inZone = [];
                byZone[sensor.Location.ZoneId] = inZone;
            }

            inZone.Add(sensor);
        }

        var collected = new List<SensorRegistration>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Descend(
            catalogue,
            zoneId,
            visit: id =>
            {
                if (byZone.TryGetValue(id, out var here))
                {
                    collected.AddRange(here);
                }
            },
            visited,
            depth: 0,
            maximumDepth);

        return collected;
    }

    /// <summary>
    /// Lists a zone and every zone beneath it, by id.
    /// </summary>
    /// <param name="catalogue">The hierarchy to walk.</param>
    /// <param name="zoneId">The zone to start from.</param>
    /// <param name="maximumDepth">How many levels to descend before stopping.</param>
    /// <returns>The named zone first, then its descendants in depth-first order.</returns>
    /// <remarks>
    /// The same walk <see cref="NodesBeneath"/> uses, exposed without the sensors. A caller that keeps its own
    /// index of devices by zone — the device search does — needs the zones beneath a zone, not a scan over every
    /// sensor to find the ones that happen to be in them. Both go through one private descent, so there is
    /// still one definition of "beneath" and one place its cycle and depth guards live.
    /// </remarks>
    public static IReadOnlyList<string> ZoneIdsBeneath(
        this IZoneCatalogue catalogue,
        string zoneId,
        int maximumDepth = ZoneValidator.DefaultMaximumDepth)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);

        var found = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Descend(catalogue, zoneId, found.Add, visited, depth: 0, maximumDepth);

        return found;
    }

    /// <summary>
    /// Reassembles the flat catalogue into nested trees, one per root.
    /// </summary>
    /// <param name="catalogue">The hierarchy to walk.</param>
    /// <param name="maximumDepth">How many levels to descend before stopping.</param>
    /// <remarks>
    /// Zones caught in a cycle are unreachable from any root and so do not appear in the result.
    /// That is the intended behaviour rather than an oversight: a cyclic fragment has no position
    /// in a tree, and inventing one for it would misrepresent the configuration.
    /// <see cref="ZoneValidator"/> is what reports the cycle itself.
    /// </remarks>
    public static IReadOnlyList<ZoneTreeNode> BuildTree(
        this IZoneCatalogue catalogue,
        int maximumDepth = ZoneValidator.DefaultMaximumDepth)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return [.. catalogue.Roots.Select(root => BuildNode(catalogue, root, visited, 0, maximumDepth))];
    }

    /// <summary>Adds the sensors at one zone, then descends into its children.</summary>
    private static void Descend(
        IZoneCatalogue catalogue,
        string zoneId,
        Action<string> visit,
        HashSet<string> visited,
        int depth,
        int maximumDepth)
    {
        // Cycle guard, the mirror of the one in ZoneValidator. A descent through a cycle would
        // revisit a zone and collect its sensors repeatedly, so this both terminates the walk and
        // keeps duplicates out of the result.
        if (!visited.Add(zoneId) || depth >= maximumDepth)
        {
            return;
        }

        visit(zoneId);

        // BASE CASE: a leaf zone has no children, so the loop body never runs and this frame
        // returns. The recursion needs no explicit leaf test — the absence of children is the
        // absence of further calls.
        foreach (var child in catalogue.ChildrenOf(zoneId))
        {
            Descend(catalogue, child.Id, visit, visited, depth + 1, maximumDepth);
        }
    }

    /// <summary>Builds one nested node, then its children.</summary>
    private static ZoneTreeNode BuildNode(
        IZoneCatalogue catalogue,
        DeploymentZone zone,
        HashSet<string> visited,
        int depth,
        int maximumDepth)
    {
        if (!visited.Add(zone.Id) || depth >= maximumDepth)
        {
            return new ZoneTreeNode(zone, []);
        }

        // BASE CASE: a leaf produces a node with an empty child list, because ChildrenOf returns
        // nothing to project over.
        return new ZoneTreeNode(
            zone,
            [.. catalogue.ChildrenOf(zone.Id)
                .Select(child => BuildNode(catalogue, child, visited, depth + 1, maximumDepth))]);
    }
}
