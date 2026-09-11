using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// Which side of the trust thresholds a node currently sits on — or that there is nothing to judge.
/// </summary>
/// <remarks>
/// Declared best-first, in the order a node moves through them as it is proven and then degrades.
/// That is deliberately <i>not</i> the order the dashboard sorts by: see
/// <see cref="TrustFormula.Severity"/>, which states the urgency order explicitly so that reordering
/// this enum can never silently reorder the dashboard.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TrustBand>))]
public enum TrustBand
{
    /// <summary>Behaving as expected.</summary>
    Nominal,

    /// <summary>No evidence yet, so no judgement either way.</summary>
    /// <remarks>
    /// Separate from <see cref="Degrading"/> because "nobody has seen this node work" and "this
    /// node has started to fail" call for different responses. A newly registered sensor scores the
    /// 0.5 Laplace prior, which numerically falls in the degrading range; classifying it that way
    /// would put every new device into the degraded set, and onto an operator's watch list, before
    /// it had done anything wrong.
    /// </remarks>
    Unproven,

    /// <summary>Slipping. Worth watching, not yet worth an alert.</summary>
    Degrading,

    /// <summary>Most likely at fault. The band that escalation will act on.</summary>
    Fault
}
