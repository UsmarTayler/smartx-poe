namespace SmartX.Shared;

/// <summary>
/// A node crossing into or out of the fault band.
/// </summary>
/// <param name="MacAddress">The node.</param>
/// <param name="Score">Its score at the moment of crossing.</param>
/// <param name="PreviousBand">The band it left.</param>
/// <param name="Band">The band it entered.</param>
/// <param name="Reason">The plain-language reason for the new score.</param>
/// <param name="WindowStart">
/// For a fall into fault, the last moment the node was nominal — or when tracking began, if it
/// never was. For a recovery, when it entered fault.
/// </param>
/// <param name="WindowEnd">The moment of crossing.</param>
/// <remarks>
/// The payload the escalation feature will act on. It carries the window rather than a single
/// timestamp because "this node failed at 14:07" is less useful than "this node went from healthy
/// to failed between 13:52 and 14:07": the window is what an operator correlates against a power
/// cut, a firmware push, or a door left open.
/// </remarks>
public sealed record TrustTransition(
    string MacAddress,
    double Score,
    TrustBand PreviousBand,
    TrustBand Band,
    string Reason,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd);
