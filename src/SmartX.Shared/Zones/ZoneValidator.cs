namespace SmartX.Shared;

/// <summary>
/// Checks a sensor against the zone it is being registered into and every zone above it.
/// </summary>
/// <remarks>
/// The walk is recursive because the hierarchy is: a sub-zone is validated exactly as a zone is,
/// against its own constraints plus whatever its parent's validation produces. Expressing that as a
/// loop would work, but it would also mean carrying the accumulated state by hand — and the state
/// that has to be carried, particularly the set of zones already visited, is precisely what the
/// recursion's parameters express.
/// </remarks>
public sealed class ZoneValidator
{
    /// <summary>
    /// How many levels the walk will climb before refusing to continue.
    /// </summary>
    /// <remarks>
    /// Thirty-two is far beyond anything a real deployment needs — facility, building, floor, zone,
    /// sub-zone is five — while still being small enough that hitting the limit costs 32 stack
    /// frames rather than a crash. The number matters less than the fact that there is one: without
    /// a ceiling, a tree that is merely very deep rather than cyclic would still exhaust the stack,
    /// and a StackOverflowException cannot be caught in .NET. The process would simply die.
    /// </remarks>
    public const int DefaultMaximumDepth = 32;

    private readonly IZoneCatalogue _catalogue;

    /// <summary>Creates a validator over a catalogue.</summary>
    /// <param name="catalogue">The hierarchy to validate against.</param>
    /// <param name="maximumDepth">How many levels to climb before giving up.</param>
    public ZoneValidator(IZoneCatalogue catalogue, int maximumDepth = DefaultMaximumDepth)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, 1);

        _catalogue = catalogue;
        MaximumDepth = maximumDepth;
    }

    /// <summary>How many levels this validator will climb.</summary>
    public int MaximumDepth { get; }

    /// <summary>Validates a sensor against the zone named in its own location.</summary>
    public ZoneValidationResult Validate(SensorRegistration sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        return Validate(sensor, sensor.Location.ZoneId);
    }

    /// <summary>Validates a sensor against a zone and every ancestor of that zone.</summary>
    /// <param name="sensor">The sensor being registered.</param>
    /// <param name="zoneId">The zone it is being registered into.</param>
    /// <returns>Every violation found, never just the first.</returns>
    public ZoneValidationResult Validate(SensorRegistration sensor, string zoneId)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);

        var violations = new List<ZoneViolation>();

        if (!_catalogue.TryFind(zoneId, out var zone))
        {
            violations.Add(new ZoneViolation(
                ZoneViolationKind.ZoneNotFound,
                zoneId,
                $"No zone is registered with id '{zoneId}'."));

            return new ZoneValidationResult(
                violations, ZoneValidationResult.UnknownDepth, ZoneConstraints.Unrestricted);
        }

        var accumulator = new ConstraintAccumulator();

        // Ordinal-ignore-case to match how the catalogue indexes ids, so a cycle written with
        // inconsistent casing is still recognised as a cycle.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var depth = Ascend(sensor, zone, stepsTaken: 0, visited, violations, accumulator);

        foreach (var unit in accumulator.ConflictingUnits)
        {
            violations.Add(new ZoneViolation(
                ZoneViolationKind.ConflictingConstraints,
                zoneId,
                $"Ancestors of zone '{zoneId}' permit ranges for '{unit}' that share no values, " +
                "so no reading in that unit could ever be accepted here."));
        }

        return new ZoneValidationResult(violations, depth, accumulator.ToConstraints());
    }

    /// <summary>
    /// Validates one level, then recurses into its parent.
    /// </summary>
    /// <returns>
    /// The zone's distance from a root, or <see cref="ZoneValidationResult.UnknownDepth"/> when the
    /// walk could not reach one.
    /// </returns>
    private int Ascend(
        SensorRegistration sensor,
        DeploymentZone zone,
        int stepsTaken,
        HashSet<string> visited,
        List<ZoneViolation> violations,
        ConstraintAccumulator accumulator)
    {
        // ---- Guard: cycle ----------------------------------------------------------------
        // The set holds every zone already on the path from the starting zone. Add returns false
        // when the id is already present, which means this zone is reachable from itself and the
        // walk has come back round to where it has been. Checking before doing any work at this
        // level is what stops a cyclic configuration recursing forever.
        //
        // Note this is a path, not a general graph traversal: an id is never removed on the way
        // back out, because a well-formed ancestry visits each zone exactly once.
        if (!visited.Add(zone.Id))
        {
            violations.Add(new ZoneViolation(
                ZoneViolationKind.CycleDetected,
                zone.Id,
                $"Zone '{zone.Id}' is its own ancestor. The deployment hierarchy contains a cycle."));

            return ZoneValidationResult.UnknownDepth;
        }

        // ---- Guard: runaway depth --------------------------------------------------------
        // A cycle is always caught above, because revisiting an id happens within one lap. This
        // guard is for the other failure: a tree that is legitimately acyclic but pathologically
        // deep, where the recursion would still exhaust the stack.
        if (stepsTaken >= MaximumDepth)
        {
            violations.Add(new ZoneViolation(
                ZoneViolationKind.MaximumDepthExceeded,
                zone.Id,
                $"Stopped at zone '{zone.Id}': the hierarchy is deeper than the {MaximumDepth} " +
                "levels this validator will walk."));

            return ZoneValidationResult.UnknownDepth;
        }

        // ---- Work at this level ----------------------------------------------------------
        CheckAgainst(sensor, zone, violations);
        accumulator.Absorb(zone.Constraints);

        int depthFromRoot;

        // ---- BASE CASE -------------------------------------------------------------------
        // A zone with no parent is a root: there is nothing above it to inherit from, so the walk
        // stops and reports a distance of zero. Every other branch below moves strictly one level
        // closer to a root, which is what makes the recursion terminate on a well-formed tree. The
        // two guards above cover the ill-formed ones.
        if (zone.IsRoot)
        {
            depthFromRoot = 0;
        }
        else if (!_catalogue.TryFind(zone.ParentId!, out var parent))
        {
            // A dangling parent reference. The tree is broken above this point, so the sensor
            // cannot be shown to satisfy its ancestry, but everything found below still stands.
            violations.Add(new ZoneViolation(
                ZoneViolationKind.ParentNotFound,
                zone.Id,
                $"Zone '{zone.Id}' names parent '{zone.ParentId}', which is not in the catalogue."));

            depthFromRoot = ZoneValidationResult.UnknownDepth;
        }
        else
        {
            var parentDepth = Ascend(sensor, parent, stepsTaken + 1, visited, violations, accumulator);

            depthFromRoot = parentDepth == ZoneValidationResult.UnknownDepth
                ? ZoneValidationResult.UnknownDepth
                : parentDepth + 1;
        }

        // Checked on the way back down, because a zone's true depth is only known once the walk
        // above it has found a root.
        if (depthFromRoot != ZoneValidationResult.UnknownDepth && zone.Depth != depthFromRoot)
        {
            violations.Add(new ZoneViolation(
                ZoneViolationKind.DepthMismatch,
                zone.Id,
                $"Zone '{zone.Id}' declares depth {zone.Depth} but sits at depth {depthFromRoot}."));
        }

        return depthFromRoot;
    }

    /// <summary>Checks the sensor against one level's constraints.</summary>
    private static void CheckAgainst(
        SensorRegistration sensor,
        DeploymentZone zone,
        List<ZoneViolation> violations)
    {
        var constraints = zone.Constraints;

        if (!constraints.Permits(sensor.Category))
        {
            violations.Add(new ZoneViolation(
                ZoneViolationKind.CategoryNotPermitted,
                zone.Id,
                $"Zone '{zone.Name}' ({zone.Id}) permits only " +
                $"{string.Join(", ", constraints.PermittedCategories)}, so sensor " +
                $"{sensor.MacAddress} ({sensor.Category}) cannot be deployed there."));
        }

        // A State sensor reports engaged or not. A numeric range says nothing about that, so the
        // range check is skipped rather than applied to a value it does not describe.
        if (sensor.PayloadKind == TelemetryPayloadKind.State)
        {
            return;
        }

        if (constraints.PermittedRanges.TryGetValue(sensor.Unit, out var permitted)
            && !permitted.Encloses(sensor.ExpectedRange))
        {
            violations.Add(new ZoneViolation(
                ZoneViolationKind.ValueRangeNotPermitted,
                zone.Id,
                $"Zone '{zone.Name}' ({zone.Id}) permits {sensor.Unit} readings in " +
                $"{permitted}, but sensor {sensor.MacAddress} expects {sensor.ExpectedRange}."));
        }
    }

    /// <summary>
    /// Narrows inherited constraints level by level as the walk climbs.
    /// </summary>
    /// <remarks>
    /// Mutable and private, because it exists only for the duration of one walk. The immutable
    /// <see cref="ZoneConstraints"/> it produces at the end is what escapes.
    /// </remarks>
    private sealed class ConstraintAccumulator
    {
        private readonly Dictionary<string, ValueRange> _ranges = new(StringComparer.Ordinal);
        private readonly List<string> _conflictingUnits = [];

        // Null means no level has restricted categories yet, which is not the same as a level
        // having restricted them to nothing. An empty list here would wrongly reject everything.
        private List<SensorCategory>? _categories;

        internal IReadOnlyList<string> ConflictingUnits => _conflictingUnits;

        internal void Absorb(ZoneConstraints constraints)
        {
            if (constraints.PermittedCategories.Count > 0)
            {
                _categories = _categories is null
                    ? [.. constraints.PermittedCategories]
                    : [.. _categories.Where(constraints.PermittedCategories.Contains)];
            }

            foreach (var (unit, range) in constraints.PermittedRanges)
            {
                if (!_ranges.TryGetValue(unit, out var existing))
                {
                    _ranges[unit] = range;
                }
                else if (existing.TryIntersect(range, out var narrowed))
                {
                    _ranges[unit] = narrowed;
                }
                else if (!_conflictingUnits.Contains(unit))
                {
                    _conflictingUnits.Add(unit);
                }
            }
        }

        internal ZoneConstraints ToConstraints() =>
            new(_categories ?? [], new Dictionary<string, ValueRange>(_ranges, StringComparer.Ordinal));
    }
}
