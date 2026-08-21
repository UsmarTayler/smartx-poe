namespace SmartX.Shared;

/// <summary>
/// The outcome of validating a sensor against a zone and everything above it.
/// </summary>
/// <remarks>
/// A structured result rather than a bool, because a bool can only say that something is wrong. An
/// operator registering a sensor into a mis-declared corner of a large hierarchy needs to know
/// which zone objected and why, and there is usually more than one answer — so the walk collects
/// every violation it finds rather than stopping at the first.
/// </remarks>
public sealed class ZoneValidationResult
{
    /// <summary>Returned as the depth when the walk could not establish one.</summary>
    /// <remarks>
    /// A cycle, a missing parent or a depth overrun all mean the distance to a root is not a
    /// question with an answer. Reporting a sentinel is more honest than reporting the number of
    /// levels that happened to be walked before the walk gave up.
    /// </remarks>
    public const int UnknownDepth = -1;

    internal ZoneValidationResult(
        IReadOnlyList<ZoneViolation> violations,
        int depthFromRoot,
        ZoneConstraints effectiveConstraints)
    {
        Violations = violations;
        DepthFromRoot = depthFromRoot;
        EffectiveConstraints = effectiveConstraints;
    }

    /// <summary>Did the sensor satisfy every constraint from every ancestor?</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>Every problem found, in the order the walk found them.</summary>
    /// <remarks>
    /// Ordered from the sensor's own zone upwards, so the most immediate objection is listed first.
    /// </remarks>
    public IReadOnlyList<ZoneViolation> Violations { get; }

    /// <summary>How far the sensor's zone actually sits from a root, or <see cref="UnknownDepth"/>.</summary>
    public int DepthFromRoot { get; }

    /// <summary>
    /// The constraints accumulated from the whole ancestry, narrowed level by level.
    /// </summary>
    /// <remarks>
    /// The intersection of everything the ancestors impose, so a facility permitting 0–100 and a
    /// sub-zone permitting 10–40 leave an effective range of 10–40. Informational: the sensor is
    /// checked against each level as the walk passes it, which is what lets a violation name the
    /// zone responsible rather than reporting a merged rule that appears in no single zone.
    /// </remarks>
    public ZoneConstraints EffectiveConstraints { get; }

    /// <summary>Every violation message joined into one line, for an error response.</summary>
    public string Describe() => string.Join(" ", Violations.Select(violation => violation.Message));
}
