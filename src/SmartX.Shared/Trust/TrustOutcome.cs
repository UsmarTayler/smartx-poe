using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// One observed piece of evidence about a node's behaviour.
/// </summary>
/// <remarks>
/// Distinguishes kinds of failure rather than recording a bare pass or fail, because they are not
/// equally damning. A reading slightly out of range is a sensor drifting; a payload of the wrong
/// type is a device sending something it was never registered to send; and silence is the failure
/// that hides itself. Each carries its own configurable weight.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TrustOutcome>))]
public enum TrustOutcome
{
    /// <summary>A schema-valid, in-range packet.</summary>
    Success,

    /// <summary>The right shape of reading, outside the registered range.</summary>
    RangeViolation,

    /// <summary>A payload that is not the type the registration declared.</summary>
    SchemaFailure,

    /// <summary>An expected reporting interval passed with no packet at all.</summary>
    MissedHeartbeat
}
