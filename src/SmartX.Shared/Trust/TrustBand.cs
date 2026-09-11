using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// Which side of the trust thresholds a node currently sits on.
/// </summary>
/// <remarks>
/// Declared worst-last so the members read in the order a node degrades through them. The
/// thresholds that separate the bands are configuration, not part of this type.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TrustBand>))]
public enum TrustBand
{
    /// <summary>Behaving as expected.</summary>
    Nominal,

    /// <summary>Unproven or slipping. Worth watching, not yet worth an alert.</summary>
    Degrading,

    /// <summary>Most likely at fault. The band that escalation will act on.</summary>
    Fault
}
