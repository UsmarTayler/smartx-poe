using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// What kind of problem a zone validation found.
/// </summary>
/// <remarks>
/// Split into structural problems with the tree itself (<see cref="ZoneNotFound"/> through
/// <see cref="DepthMismatch"/>) and problems with the sensor being registered
/// (<see cref="CategoryNotPermitted"/>, <see cref="ValueRangeNotPermitted"/>). The distinction
/// matters to the caller: a structural fault means the configuration needs fixing, whereas a
/// constraint fault means this particular sensor does not belong in this particular zone.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ZoneViolationKind>))]
public enum ZoneViolationKind
{
    /// <summary>The zone being registered into does not exist.</summary>
    ZoneNotFound,

    /// <summary>A zone names a parent that is not in the catalogue.</summary>
    ParentNotFound,

    /// <summary>A zone is its own ancestor, so the walk upwards would never terminate.</summary>
    CycleDetected,

    /// <summary>The hierarchy is deeper than the validator is willing to walk.</summary>
    MaximumDepthExceeded,

    /// <summary>A zone's declared depth disagrees with its actual position in the tree.</summary>
    DepthMismatch,

    /// <summary>Two ancestors permit ranges for one unit that share no values.</summary>
    ConflictingConstraints,

    /// <summary>An ancestor does not permit this sensor's category.</summary>
    CategoryNotPermitted,

    /// <summary>The sensor's expected range falls outside what an ancestor permits.</summary>
    ValueRangeNotPermitted
}

/// <summary>
/// One reason a sensor could not be registered into a zone.
/// </summary>
/// <param name="Kind">The category of problem.</param>
/// <param name="ZoneId">The zone the problem was found at, which may be an ancestor.</param>
/// <param name="Message">A description naming the zone and what it objected to.</param>
/// <remarks>
/// Carries the zone id as well as the message because the offending zone is frequently not the one
/// the caller named — a sensor can be rejected by a constraint three levels up, and "your sensor is
/// not permitted here" is not actionable without knowing which "here" is meant.
/// </remarks>
public sealed record ZoneViolation(ZoneViolationKind Kind, string ZoneId, string Message);
