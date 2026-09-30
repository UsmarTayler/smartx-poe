namespace SmartX.Api.Commands;

/// <summary>
/// Configuration for commands: how long they may wait, and how far back an operator can undo.
/// </summary>
public sealed class CommandOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Commands";

    /// <summary>How long a command may wait for its device to collect it.</summary>
    /// <remarks>
    /// Thirty seconds: several check-ins at the demo's five-second poll, so a device that is merely between
    /// polls is not condemned, and short enough that a command to a silent device visibly times out on camera.
    /// A real fleet that sleeps for minutes between check-ins would set this to a multiple of its sleep.
    /// </remarks>
    public TimeSpan PendingTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a delivered command may wait for its device to confirm it.</summary>
    /// <remarks>
    /// A delivered command is never sent again, so if the device fetched it and then lost power, or the
    /// response was lost on the way, nothing will ever confirm it. This is how that becomes visible instead of
    /// staying "delivered" for ever.
    /// </remarks>
    public TimeSpan DeliveredTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the timeout service looks for stale commands.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The most operator actions the undo stack remembers. Older ones fall off the bottom.
    /// </summary>
    /// <remarks>
    /// <b>Bounded, on purpose.</b> Undo means "return to the state recorded when the command was issued".
    /// That record gets staler every minute: after an hour the device may have been commanded, failed over,
    /// serviced or replaced, and restoring an hour-old state can do harm. Undo is for "I just did the wrong
    /// thing", which is the last few actions. Beyond that, an operator should issue an explicit command and
    /// look at the device first. Twenty is a working session's worth of recent actions. A bound also keeps a
    /// composite entry's many children from growing the stack without limit.
    /// </remarks>
    public int MaxUndoDepth { get; set; } = 20;
}
