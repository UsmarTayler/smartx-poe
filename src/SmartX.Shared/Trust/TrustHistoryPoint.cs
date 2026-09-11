namespace SmartX.Shared;

/// <summary>One sample of a node's trust, for drawing a sparkline.</summary>
/// <param name="Timestamp">When the score was sampled.</param>
/// <param name="Score">The score at that moment.</param>
/// <param name="Band">The band at that moment.</param>
/// <remarks>
/// A struct, so a node's history ring buffer holds its samples inline in one array rather than as
/// an array of references to individually allocated objects.
/// </remarks>
public readonly record struct TrustHistoryPoint(DateTimeOffset Timestamp, double Score, TrustBand Band);
