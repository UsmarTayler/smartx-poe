using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>How the alerts panel orders alerts: most severe first, then newest.</summary>
public static class AlertOrdering
{
    /// <summary>How severe a kind of alert is. Lower is more severe.</summary>
    /// <remarks>
    /// A node in the fault band is the most likely to be broken. A node that has gone silent is next, because nothing about it
    /// can be trusted. A command that was never answered says a device is not listening. Out-of-range and wrong-type readings
    /// are a device misbehaving but still talking. Stated here as a decision and not left to the enum's numbering, which
    /// is in the order the kinds were added.
    /// </remarks>
    public static int Severity(AlertKind kind) => kind switch
    {
        AlertKind.Fault => 0,
        AlertKind.Disconnected => 1,
        AlertKind.CommandTimedOut => 2,
        AlertKind.OutOfRange => 3,
        AlertKind.TypeViolation => 4,
        _ => 5,
    };

    /// <summary>The alerts, most severe first and newest first within a severity.</summary>
    public static IReadOnlyList<ActiveAlert> Order(IEnumerable<ActiveAlert> alerts) =>
    [
        .. alerts
            .OrderBy(alert => Severity(alert.Key.Kind))
            .ThenByDescending(alert => alert.RaisedAt)
            .ThenBy(alert => alert.Key.Mac, StringComparer.Ordinal),
    ];
}
