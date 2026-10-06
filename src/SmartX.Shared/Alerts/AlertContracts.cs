namespace SmartX.Shared;

/// <summary>An alert that is on right now.</summary>
/// <param name="Key">Which node, and what kind of trouble.</param>
/// <param name="DeviceName">The node's name in its deployment, when it is registered.</param>
/// <param name="RaisedAt">When the condition first fired.</param>
/// <param name="SuppressedCount">
/// How many times the condition fired again while the alert was already active. This is the duplicate
/// prevention made visible: the operator sees one alert saying it has been repeated fourteen times, not
/// fourteen alerts.
/// </param>
/// <param name="Acknowledged">Whether an operator has seen it. Acknowledging does not clear the condition.</param>
/// <param name="Detail">What the most recent occurrence said, such as the reading that was out of range.</param>
public sealed record ActiveAlert(
    AlertKey Key,
    string? DeviceName,
    DateTimeOffset RaisedAt,
    long SuppressedCount,
    bool Acknowledged,
    string? Detail);

/// <summary>Running totals for the alert set since the gateway started.</summary>
/// <param name="Raised">Alerts that went from not active to active.</param>
/// <param name="Suppressed">Occurrences that found the alert already active and were counted, not raised.</param>
/// <param name="Cleared">Alerts whose condition ended.</param>
/// <param name="Active">Alerts active now.</param>
/// <param name="Acknowledged">Active alerts an operator has acknowledged.</param>
public sealed record AlertStats(long Raised, long Suppressed, long Cleared, int Active, int Acknowledged);
