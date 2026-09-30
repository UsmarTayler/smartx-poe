using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>The body of a POST to /api/commands.</summary>
/// <param name="MacAddress">The actuator to command.</param>
/// <param name="Action">What to ask of it.</param>
/// <param name="Level">The level, for SetLevel.</param>
public sealed record IssueCommandRequest(string MacAddress, CommandAction Action, double? Level = null);

/// <summary>The body of a POST to /api/commands/zone.</summary>
/// <param name="ZoneId">The zone to act on. Every matching actuator in it, and in every zone beneath it, is commanded.</param>
/// <param name="Action">What to ask of them. Only actuators of the action's kind are reached.</param>
/// <param name="Level">The level, for SetLevel.</param>
public sealed record ZoneCommandRequest(string ZoneId, CommandAction Action, double? Level = null);

/// <summary>What a device sends to confirm, or fail, a command it fetched.</summary>
/// <param name="Success">True if it applied the command.</param>
/// <param name="ResultingValue">The actuator's state afterwards, in the same terms as a command's target.</param>
/// <param name="Reason">Why it could not, when <paramref name="Success"/> is false.</param>
public sealed record CommandAckRequest(bool Success, double? ResultingValue = null, string? Reason = null);

/// <summary>How a group of commands issued together is going.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CompositeStatus>))]
public enum CompositeStatus
{
    /// <summary>At least one command has not reached a final state yet.</summary>
    InProgress,

    /// <summary>Every command was acknowledged.</summary>
    Applied,

    /// <summary>Some were acknowledged and some failed, timed out or were cancelled. Normal in IoT.</summary>
    PartiallyApplied,

    /// <summary>None was acknowledged.</summary>
    NotApplied
}

/// <summary>
/// The result of one operator action that fanned out to many devices.
/// </summary>
/// <param name="CompositeId">Ties the commands together.</param>
/// <param name="ZoneId">The zone that was targeted.</param>
/// <param name="Action">The action that was sent.</param>
/// <param name="Commands">One command per matching actuator.</param>
/// <param name="Status">How the group is going as of now.</param>
public sealed record CompositeCommandResult(
    Guid CompositeId,
    string ZoneId,
    CommandAction Action,
    IReadOnlyList<Command> Commands,
    CompositeStatus Status);

/// <summary>What happened to one command when an undo was applied.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UndoItemResult>))]
public enum UndoItemResult
{
    /// <summary>It had not reached the device, so it was withdrawn.</summary>
    Cancelled,

    /// <summary>A restore command was issued to return the actuator to its recorded state.</summary>
    RestoreIssued,

    /// <summary>It had already been cancelled, so there was nothing left to undo.</summary>
    AlreadyCancelled,

    /// <summary>Undoing it was refused. The reason says why.</summary>
    Refused
}

/// <summary>The outcome of undoing one command.</summary>
/// <param name="CommandId">The command that was undone.</param>
/// <param name="MacAddress">The actuator it was aimed at.</param>
/// <param name="Result">What was done.</param>
/// <param name="RestoreCommandId">The restore command, when one was issued.</param>
/// <param name="Reason">Why it was refused, or a note on what was done.</param>
public sealed record UndoItem(
    Guid CommandId,
    string MacAddress,
    UndoItemResult Result,
    Guid? RestoreCommandId,
    string? Reason);

/// <summary>The overall outcome of an undo.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UndoOutcome>))]
public enum UndoOutcome
{
    /// <summary>Everything in the entry was undone.</summary>
    Reverted,

    /// <summary>Some of it was undone and some was refused.</summary>
    PartiallyReverted,

    /// <summary>All of it was refused.</summary>
    Refused,

    /// <summary>The stack was empty.</summary>
    NothingToUndo
}

/// <summary>What an undo did, exactly.</summary>
/// <param name="Outcome">The overall result.</param>
/// <param name="Message">A sentence saying what happened, for an operator.</param>
/// <param name="EntryId">The stack entry that was popped.</param>
/// <param name="Items">One result per command in the entry.</param>
public sealed record UndoResult(UndoOutcome Outcome, string Message, Guid? EntryId, IReadOnlyList<UndoItem> Items);

/// <summary>One entry on the undo stack: a single command, or every command from one zone-wide action.</summary>
/// <param name="EntryId">Identifies the entry.</param>
/// <param name="IssuedAt">When the operator issued it.</param>
/// <param name="Description">A sentence describing it.</param>
/// <param name="CompositeId">Set when the entry is a zone-wide action.</param>
/// <param name="Commands">The commands as they stand now.</param>
/// <param name="Status">How the entry is going as of now.</param>
public sealed record UndoStackEntry(
    Guid EntryId,
    DateTimeOffset IssuedAt,
    string Description,
    Guid? CompositeId,
    IReadOnlyList<Command> Commands,
    CompositeStatus Status);

/// <summary>What happened to a command, as an event in the log.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandEvent>))]
public enum CommandEvent
{
    /// <summary>The gateway accepted the command.</summary>
    Issued,

    /// <summary>The device fetched it.</summary>
    Delivered,

    /// <summary>The device confirmed it.</summary>
    Acknowledged,

    /// <summary>The device reported failure.</summary>
    Failed,

    /// <summary>Nobody collected or confirmed it in time.</summary>
    TimedOut,

    /// <summary>It was withdrawn before delivery.</summary>
    Cancelled,

    /// <summary>A restore command was issued to undo it.</summary>
    Reverted
}

/// <summary>
/// One line of the command log.
/// </summary>
/// <remarks>
/// Every event carries the target's trust band <i>at that moment</i>, not now. Whether a device was already
/// faulty when it was commanded is exactly the question an operator asks afterwards, and the pattern analysis
/// planned for later reads the log to answer it.
/// </remarks>
/// <param name="Sequence">Increases by one per entry, so the log has a total order even within one timestamp.</param>
/// <param name="CommandId">The command the event is about.</param>
/// <param name="MacAddress">The actuator.</param>
/// <param name="Event">What happened.</param>
/// <param name="Timestamp">When.</param>
/// <param name="TrustBand">The target's trust band at that moment, or null if it was not tracked.</param>
/// <param name="Detail">Anything worth recording, such as a failure reason.</param>
public sealed record CommandLogEntry(
    long Sequence,
    Guid CommandId,
    string MacAddress,
    CommandEvent Event,
    DateTimeOffset Timestamp,
    TrustBand? TrustBand,
    string? Detail);

/// <summary>A command with its history.</summary>
/// <param name="Command">The command as it stands.</param>
/// <param name="Events">Every event in its life, oldest first.</param>
public sealed record CommandDetail(Command Command, IReadOnlyList<CommandLogEntry> Events);
