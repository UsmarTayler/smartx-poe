namespace SmartX.Shared;

/// <summary>
/// What the API returns when it accepts a reading for processing.
/// </summary>
/// <remarks>
/// A reading is validated while the caller waits and processed after it has gone, so the response can no
/// longer be the stored packet: at the moment it is written, the packet does not exist yet. The receipt is
/// what a caller gets instead — proof the reading was accepted, which lane it went into, and how many
/// readings were ahead of it.
/// </remarks>
/// <param name="ReceiptId">Identifies this reading, so a caller can refer to it in a log or a support call.</param>
/// <param name="MacAddress">The sensor the reading came from.</param>
/// <param name="Lane">Which queue took it: "standard" or "critical".</param>
/// <param name="QueuePosition">How many readings were in that lane once this one joined it, itself included.</param>
/// <param name="AcceptedAt">When the API accepted it.</param>
public sealed record TelemetryReceipt(
    Guid ReceiptId,
    string MacAddress,
    string Lane,
    int QueuePosition,
    DateTimeOffset AcceptedAt);
