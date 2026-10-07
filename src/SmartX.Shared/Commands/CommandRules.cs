namespace SmartX.Shared;

/// <summary>
/// What can and cannot be undone, and how a group of commands is going. One definition for the server that does it and
/// the client that explains it.
/// </summary>
/// <remarks>
/// The undo panel tells an operator, before they press anything, that a timed-out command cannot be undone and why. That
/// reason is the server's, word for word: if the client wrote its own, the page could promise an undo the server then
/// refuses, or explain a refusal in different words from the one it gives. So the rules live here and both sides call them.
/// </remarks>
public static class CommandRules
{
    /// <summary>Why a command cannot be undone, or <see langword="null"/> when it can.</summary>
    /// <remarks>
    /// A pending command can be withdrawn, and a delivered or acknowledged one has a restore queued behind it, provided the
    /// state it replaced was observed. A timed-out or failed command leaves the actuator in a state the gateway does not
    /// know, and restoring an unknown state could do harm. A cancelled command was already undone.
    /// </remarks>
    public static string? WhyNotUndoable(Command command) => command.State switch
    {
        CommandState.Delivered or CommandState.Acknowledged when !command.PreviousObserved =>
            "The state this command replaced was never observed, so there is nothing recorded to return to. Restoring a guess could do harm.",

        CommandState.TimedOut =>
            "The command timed out, so the gateway does not know whether the device applied it. The actuator's real state is unknown, and restoring an unknown state could do harm.",

        CommandState.Failed =>
            "The device reported that it could not apply the command, so the state it is in is unknown. Restoring an unknown state could do harm.",

        CommandState.Cancelled =>
            "It had already been cancelled, so there was nothing to undo.",

        _ => null,
    };

    /// <summary>How a group of commands from one zone-wide action is going, from the states of its members.</summary>
    public static CompositeStatus StatusOf(IReadOnlyCollection<Command> commands)
    {
        if (commands.Any(command => !command.IsFinal))
        {
            return CompositeStatus.InProgress;
        }

        var acknowledged = commands.Count(command => command.State == CommandState.Acknowledged);

        return acknowledged == commands.Count ? CompositeStatus.Applied
            : acknowledged == 0 ? CompositeStatus.NotApplied
            : CompositeStatus.PartiallyApplied;
    }
}
