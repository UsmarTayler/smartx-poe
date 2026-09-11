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
/// <remarks>
/// <paramref name="Reason"/> is what turns this from a report into information. A dashboard showing
/// "0.41" tells an operator that something is wrong; "no packets for 4m 20s" tells them what to go
/// and look at.
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
    int RecentFailures);
