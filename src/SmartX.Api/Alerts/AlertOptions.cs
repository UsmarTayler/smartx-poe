namespace SmartX.Api.Alerts;

/// <summary>Configuration for alerts: when a node counts as disconnected, and when a repeated fault counts as over.</summary>
public sealed class AlertOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Alerts";

    /// <summary>
    /// How many of a node's own heartbeat intervals it may stay silent before it is called disconnected.
    /// </summary>
    /// <remarks>
    /// Three, not one. A single missed interval is more often a slow network than a dead node, and an alert
    /// that fires on every late packet teaches operators to ignore alerts. It is a multiple of the node's own
    /// interval, not a fixed number of seconds, because a node that reports every five seconds and one that
    /// reports every five minutes have very different ideas of "silent".
    /// </remarks>
    public int DisconnectedAfterIntervals { get; set; } = 3;

    /// <summary>
    /// How many consecutive valid readings end an out-of-range or wrong-type alert.
    /// </summary>
    /// <remarks>
    /// More than one, so a device that alternates good and bad readings does not clear and re-raise the same
    /// alert on every other packet. That flapping is exactly the duplicate noise the alert set exists to remove.
    /// </remarks>
    public int ClearAfterValidReadings { get; set; } = 3;
}
