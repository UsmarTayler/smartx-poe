using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>What an operator can ask an actuator to do.</summary>
/// <remarks>
/// Each action belongs to exactly one <see cref="ActuatorKind"/>, so a command can be checked against the
/// device it is aimed at before it is queued. See <see cref="CommandActionRules"/>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CommandAction>))]
public enum CommandAction
{
    /// <summary>Switch a pump on.</summary>
    PumpOn,

    /// <summary>Switch a pump off.</summary>
    PumpOff,

    /// <summary>Close a relay's circuit.</summary>
    RelayOn,

    /// <summary>Open a relay's circuit.</summary>
    RelayOff,

    /// <summary>Open a valve.</summary>
    ValveOpen,

    /// <summary>Close a valve.</summary>
    ValveClose,

    /// <summary>Set a dimmer to a level from 0 to 100.</summary>
    SetLevel
}

/// <summary>Where a command is in its life.</summary>
/// <remarks>
/// <code>
/// Pending ──▶ Delivered ──▶ Acknowledged
///    │            │
///    │            └──▶ Failed
///    ├──▶ Cancelled
///    └──▶ TimedOut          (Delivered can time out too)
/// </code>
/// Every state after <see cref="Pending"/> and <see cref="Delivered"/> is final.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CommandState>))]
public enum CommandState
{
    /// <summary>Issued, and not yet collected by the device.</summary>
    Pending,

    /// <summary>The device has fetched it. The gateway never sends it again.</summary>
    Delivered,

    /// <summary>The device confirmed that it applied the command.</summary>
    Acknowledged,

    /// <summary>The device reported that it could not apply the command.</summary>
    Failed,

    /// <summary>Nobody collected or confirmed it within the configured window.</summary>
    TimedOut,

    /// <summary>Withdrawn before it was delivered.</summary>
    Cancelled
}

/// <summary>
/// One instruction to one actuator, and everything the gateway knows about how it went.
/// </summary>
/// <remarks>
/// An immutable snapshot: a change of state produces a new value, so a caller holding one never sees it change
/// halfway through being read.
/// <para>
/// <b>What a command remembers about the state it replaced.</b> <see cref="PreviousValue"/> is the state the
/// actuator was in, or was about to be in, when this command was issued. Undo returns to that recorded state; it
/// does not do the opposite of the action. See <c>CommandService</c> for why the opposite would be wrong.
/// </para>
/// </remarks>
/// <param name="Id">Identifies the command everywhere: the log, the device's acknowledgement, the undo stack.</param>
/// <param name="MacAddress">The actuator it is aimed at.</param>
/// <param name="Kind">The actuator's kind when the command was issued.</param>
/// <param name="Action">What it asks for.</param>
/// <param name="Level">The level for <see cref="CommandAction.SetLevel"/>, and null for every other action.</param>
/// <param name="TargetValue">
/// The state the command asks for, in the actuator's own terms: 1 for on or open, 0 for off or closed, and the
/// level for a dimmer.
/// </param>
/// <param name="IssuedAt">When the gateway accepted it.</param>
/// <param name="State">Where it is in its life.</param>
/// <param name="PreviousValue">The state it replaced, in the same terms as <paramref name="TargetValue"/>.</param>
/// <param name="PreviousObserved">
/// False when no state had ever been observed for the actuator, so <paramref name="PreviousValue"/> is empty.
/// Undo is refused for such a command rather than guessed.
/// </param>
/// <param name="Note">Anything worth saying about the command, such as why it cannot be undone.</param>
/// <param name="RevertsCommandId">Set on a restore command: the command it undoes.</param>
/// <param name="CompositeId">Set when the command is one of many issued by a single zone-wide action.</param>
/// <param name="DeliveredAt">When the device fetched it.</param>
/// <param name="ResolvedAt">When it reached its final state.</param>
/// <param name="ResultingValue">The state the device reported after applying it.</param>
/// <param name="FailureReason">Why it failed, or timed out, or was cancelled.</param>
public sealed record Command(
    Guid Id,
    string MacAddress,
    ActuatorKind Kind,
    CommandAction Action,
    double? Level,
    double TargetValue,
    DateTimeOffset IssuedAt,
    CommandState State,
    double? PreviousValue,
    bool PreviousObserved,
    string? Note = null,
    Guid? RevertsCommandId = null,
    Guid? CompositeId = null,
    DateTimeOffset? DeliveredAt = null,
    DateTimeOffset? ResolvedAt = null,
    double? ResultingValue = null,
    string? FailureReason = null)
{
    /// <summary>Has it reached a state it will not leave?</summary>
    public bool IsFinal => State is not (CommandState.Pending or CommandState.Delivered);

    /// <summary>Is it a restore command issued by an undo?</summary>
    public bool IsRestore => RevertsCommandId is not null;
}
