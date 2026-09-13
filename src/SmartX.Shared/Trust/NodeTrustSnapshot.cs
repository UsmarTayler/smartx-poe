namespace SmartX.Shared;

/// <summary>
/// A node's trust as it stood at its most recent evaluation.
/// </summary>
/// <param name="MacAddress">The node.</param>
/// <param name="Score">Trust between 0 and 1.</param>
/// <param name="Band">Which threshold band the score falls in.</param>
/// <param name="Reason">
/// A short plain-language explanation, such as "3 range violations" or "no packets for 4m 20s".
/// </param>
/// <param name="TrackedSince">When the node was first tracked.</param>
/// <param name="LastSeen">When the node last sent anything, valid or not. Null if never.</param>
/// <param name="EvaluatedAt">When this score was computed.</param>
/// <param name="RecentSuccesses">Valid packets counted as recent.</param>
/// <param name="RecentFailures">Failures of any kind counted as recent.</param>
/// <param name="RecentHistory">
/// The node's most recent trust samples, oldest first, enough to draw its sparkline.
/// </param>
/// <remarks>
/// <paramref name="Reason"/> is what turns this from a report into information. A dashboard showing
/// "0.41" tells an operator that something is wrong; "no packets for 4m 20s" tells them what to go
/// and look at.
/// <para>
/// <paramref name="RecentHistory"/> travels with the snapshot so the dashboard can draw every row from
/// one response. Without it the client made one extra history request per node on every refresh —
/// fine with three sensors, and around forty requests every three seconds once dozens are registered.
/// Only the last few samples are included; the full series is still at /api/trust/{mac}/history.
/// </para>
/// </remarks>
public sealed record NodeTrustSnapshot(
    string MacAddress,
    double Score,
    TrustBand Band,
    string Reason,
    DateTimeOffset TrackedSince,
    DateTimeOffset? LastSeen,
    DateTimeOffset EvaluatedAt,
    int RecentSuccesses,
    int RecentFailures,
    IReadOnlyList<TrustHistoryPoint> RecentHistory);
