using System.Globalization;
using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Commands;

/// <summary>Why a command could not be issued.</summary>
internal enum IssueFailure
{
    None,
    NotAnActuator,
    Invalid,
    UnknownZone,
    NoMatchingActuators,
}

/// <summary>The result of issuing one command.</summary>
internal sealed record CommandIssue(Command? Command, IssueFailure Failure, string? Error);

/// <summary>What a zone-wide action would do, or why it cannot.</summary>
internal sealed record ZonePreviewResult(ZonePreview? Preview, IssueFailure Failure, string? Error);

/// <summary>The result of issuing a zone-wide command.</summary>
internal sealed record ZoneIssue(CompositeCommandResult? Result, IssueFailure Failure, string? Error);

/// <summary>How a device's acknowledgement was received.</summary>
internal enum AckOutcome
{
    /// <summary>It moved the command to its final state.</summary>
    Applied,

    /// <summary>The command was already in that final state, so nothing changed. Safe to repeat.</summary>
    Repeated,

    NotFound,

    /// <summary>The command is in a state that cannot accept this acknowledgement.</summary>
    Conflict,
}

internal sealed record AckResult(AckOutcome Outcome, Command? Command, string? Message);

/// <summary>
/// Issues commands, tracks their lifecycle, and keeps the operator's undo stack.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a command is, and what it is not.</b> An IoT command is not a change to a value in this process's
/// memory. It is an instruction sent to a device that may or may not receive it, may or may not apply it, and
/// may or may not say so. So a command has a life: it is <i>pending</i> until the device collects it,
/// <i>delivered</i> once it has, and finally <i>acknowledged</i>, <i>failed</i>, <i>cancelled</i> or <i>timed
/// out</i>. The gateway only ever knows what the device tells it.
/// </para>
/// <para>
/// <b>At-least-once delivery, made safe by idempotency.</b> A device can check in twice before its
/// acknowledgement lands, and networks repeat messages. The gateway never re-sends a delivered command
/// (<see cref="ICommandStore.Collect"/> moves it out of the pending set under a lock), and the device
/// remembers the ids it has already applied and will not apply one twice. Either half alone would be enough on a
/// good day; both together make a repeat harmless whichever side it comes from.
/// </para>
/// <para>
/// <b>Why undo restores the previous state instead of doing the opposite.</b> The tempting design gives every
/// command an opposite: <c>ValveOpen</c> is undone by <c>ValveClose</c>. It is wrong, and easy to see why. If a
/// valve is already open and an operator sends <c>ValveOpen</c>, nothing changes. Undoing that with the
/// "opposite" closes the valve, a state it was never in and that the operator never chose. The opposite of an
/// action is not the state before it.
/// </para>
/// <para>
/// So every command records the state it <i>replaced</i>, taken from the actuator's latest observed state at the
/// moment the command is issued, and undo returns to that recorded state. Open an already-open valve, undo, and
/// it is left open. For a dimmer the recorded state is the previous level. If no state was ever observed the
/// command says so, and its undo is refused rather than guessed: restoring a state the gateway made up could
/// open a valve nobody meant to open.
/// </para>
/// <para>
/// The recorded state is the one the actuator was <i>about to be in</i>, not merely the last one it reported.
/// If a command is still in flight, the next command records that one's target as its previous state.
/// Otherwise two quick commands would both record the old reading, and undoing the second would skip past the
/// first.
/// </para>
/// </remarks>
internal sealed class CommandService(
    ICommandStore store,
    ICommandLog log,
    IActuatorStateStore states,
    ITrustEngine trust,
    AlertTracker alerts,
    IOptions<CommandOptions> options,
    TimeProvider time)
{
    private readonly CommandOptions _options = options.Value;

    /// <summary>
    /// Raised whenever something happens to a command: issued, delivered, acknowledged, failed, timed out,
    /// cancelled or reverted.
    /// </summary>
    /// <remarks>
    /// A domain event, so whatever shows commands live can follow them without this class knowing it exists.
    /// Raised from the same place as the log entry, so the two never disagree about what happened. A handler
    /// that throws is ignored: a faulty subscriber must not be able to fail a command.
    /// </remarks>
    public event Action<Command, CommandEvent>? Changed;

    // A plain Stack<T> behind a lock, not ConcurrentStack<T>. Each ConcurrentStack call is safe on its own,
    // but the operations here are compound: undo pops an entry and must not race a push, and a push that would
    // exceed the depth limit has to drop the oldest entry, which is several steps on any stack. A lock makes
    // the compound operation atomic in one place, and keeps the last-in, first-out behaviour the brief asks
    // for visible in the code instead of hidden inside a library type.
    private readonly Lock _undoGate = new();
    private readonly Stack<UndoEntry> _undo = new();

    /// <summary>One operator action on the undo stack: a single command, or every child of a zone command.</summary>
    private sealed record UndoEntry(Guid EntryId, DateTimeOffset IssuedAt, string Description, Guid? CompositeId, IReadOnlyList<Guid> CommandIds);

    // ---- issuing ------------------------------------------------------------------------------------------

    /// <summary>Issues one command to one actuator, and puts it on the undo stack.</summary>
    public CommandIssue Issue(SensorRegistration sensor, CommandAction action, double? level)
    {
        if (Refusal(sensor, action, level) is { } refusal)
        {
            return new CommandIssue(null, refusal.Failure, refusal.Message);
        }

        var command = Create(sensor.MacAddress, sensor.ActuatorKind!.Value, action, level, compositeId: null, reverts: null);

        Push(new UndoEntry(
            Guid.NewGuid(),
            command.IssuedAt,
            Describe(command),
            null,
            [command.Id]));

        return new CommandIssue(command, IssueFailure.None, null);
    }

    /// <summary>
    /// Issues one action to every matching actuator in a zone and everywhere beneath it, as one undoable step.
    /// </summary>
    /// <remarks>
    /// The devices are found with <see cref="ZoneTree.NodesBeneath"/>, the same recursive downward walk that
    /// collects sensors for the topology in Part 1. A zone has sub-zones, which have sub-zones of their own,
    /// and "every valve under Zone 3" has to reach all of them. The walk already knows how to descend without
    /// looping on a malformed hierarchy, so this feature reuses it instead of growing a second one.
    /// </remarks>
    public ZoneIssue IssueForZone(
        string zoneId,
        CommandAction action,
        double? level,
        IZoneCatalogue catalogue,
        IEnumerable<SensorRegistration> sensors)
    {
        if (!TrySelectZoneTargets(zoneId, action, level, catalogue, sensors, out var selected, out var failure))
        {
            return failure;
        }

        var (zone, kind, targets) = selected;

        var compositeId = Guid.NewGuid();
        var commands = targets
            .Select(sensor => Create(sensor.MacAddress, kind, action, level, compositeId, reverts: null))
            .ToList();

        Push(new UndoEntry(
            Guid.NewGuid(),
            commands[0].IssuedAt,
            $"{action}{LevelText(level)} on {commands.Count} {kind} device{(commands.Count == 1 ? string.Empty : "s")} under {zone.Name}",
            compositeId,
            [.. commands.Select(command => command.Id)]));

        return new ZoneIssue(
            new CompositeCommandResult(compositeId, zone.Id, action, commands, StatusOf(commands)),
            IssueFailure.None,
            null);
    }

    /// <summary>
    /// What a zone-wide action would reach, without issuing it.
    /// </summary>
    /// <remarks>
    /// Runs exactly the selection <see cref="IssueForZone"/> runs, the same recursive downward walk, the same kind
    /// filter and the same refusals, because it is the same method. A preview that had its own copy of the walk
    /// could show one set of devices and then act on another.
    /// </remarks>
    public ZonePreviewResult PreviewZone(
        string zoneId,
        CommandAction action,
        double? level,
        IZoneCatalogue catalogue,
        IEnumerable<SensorRegistration> sensors)
    {
        if (!TrySelectZoneTargets(zoneId, action, level, catalogue, sensors, out var selected, out var failure))
        {
            return new ZonePreviewResult(null, failure.Failure, failure.Error);
        }

        var devices = selected.Sensors
            .Select(sensor => new ZoneTarget(
                sensor.MacAddress,
                sensor.Location.Room,
                sensor.Location.ZoneId,
                selected.Kind,
                states.TryGet(sensor.MacAddress, out var observed) ? observed.Value : null))
            .ToList();

        return new ZonePreviewResult(new ZonePreview(selected.Zone.Id, selected.Zone.Name, action, level, devices), IssueFailure.None, null);
    }

    /// <summary>The zone, the kind of actuator and the devices a zone-wide action reaches.</summary>
    private readonly record struct ZoneTargets(DeploymentZone Zone, ActuatorKind Kind, IReadOnlyList<SensorRegistration> Sensors);

    /// <summary>Finds the devices a zone-wide action reaches, or says why there are none.</summary>
    private static bool TrySelectZoneTargets(
        string zoneId,
        CommandAction action,
        double? level,
        IZoneCatalogue catalogue,
        IEnumerable<SensorRegistration> sensors,
        out ZoneTargets selected,
        out ZoneIssue failure)
    {
        selected = default;
        failure = default!;

        if (!Enum.IsDefined(action))
        {
            failure = new ZoneIssue(null, IssueFailure.Invalid, CommandActionRules.Validate(null, action, level, default));

            return false;
        }

        if (!catalogue.TryFind(zoneId, out var zone))
        {
            failure = new ZoneIssue(null, IssueFailure.UnknownZone, $"No zone is registered with id '{zoneId}'.");

            return false;
        }

        var kind = CommandActionRules.KindFor(action);

        var targets = catalogue.NodesBeneath(zone.Id, sensors)
            .Where(sensor => sensor.Category == SensorCategory.Actuator && sensor.ActuatorKind == kind)
            .OrderBy(sensor => sensor.MacAddress, StringComparer.Ordinal)
            .ToList();

        if (targets.Count == 0)
        {
            failure = new ZoneIssue(
                null,
                IssueFailure.NoMatchingActuators,
                $"There is no {kind} in zone '{zone.Name}' or anywhere beneath it, so {action} has nothing to act on.");

            return false;
        }

        // Every target is checked before anything is created, so a level outside one device's range refuses the
        // whole action instead of issuing half of it.
        foreach (var sensor in targets)
        {
            if (Refusal(sensor, action, level) is { } refusal)
            {
                failure = new ZoneIssue(null, refusal.Failure, $"{sensor.MacAddress}: {refusal.Message}");

                return false;
            }
        }

        selected = new ZoneTargets(zone, kind, targets);

        return true;
    }

    private static (IssueFailure Failure, string Message)? Refusal(SensorRegistration sensor, CommandAction action, double? level)
    {
        if (sensor.Category != SensorCategory.Actuator)
        {
            return (IssueFailure.NotAnActuator, $"{sensor.MacAddress} is a {sensor.Category} sensor, not an actuator, so it cannot be sent commands.");
        }

        return CommandActionRules.Validate(sensor.ActuatorKind, action, level, sensor.ExpectedRange) is { } message
            ? (IssueFailure.Invalid, message)
            : null;
    }

    private Command Create(string macAddress, ActuatorKind kind, CommandAction action, double? level, Guid? compositeId, Guid? reverts)
    {
        var now = time.GetUtcNow();
        var (previous, observed, note) = StateBefore(macAddress);

        var command = new Command(
            Guid.NewGuid(),
            macAddress,
            kind,
            action,
            action == CommandAction.SetLevel ? level : null,
            CommandActionRules.TargetValue(action, level),
            now,
            CommandState.Pending,
            previous,
            observed,
            note,
            reverts,
            compositeId);

        store.Add(command);
        Log(command, CommandEvent.Issued, note);

        return command;
    }

    /// <summary>The state an actuator is in, or is about to be in, at this moment.</summary>
    private (double? Value, bool Observed, string? Note) StateBefore(string macAddress)
    {
        // A command still in flight is where the actuator is heading, so it is where the next command starts.
        if (store.LatestLive(macAddress) is { } inFlight)
        {
            return (inFlight.TargetValue, true, $"The previous state is the target of command {inFlight.Id}, which was still in flight.");
        }

        return states.TryGet(macAddress, out var observed)
            ? (observed.Value, true, null)
            : (null, false, "The previous state of this actuator was never observed, so this command cannot be undone.");
    }

    // ---- delivery and acknowledgement --------------------------------------------------------------------

    /// <summary>
    /// Hands a device the commands waiting for it. Each command is handed over once, ever.
    /// </summary>
    public IReadOnlyList<Command> Collect(string macAddress)
    {
        var delivered = store.Collect(macAddress, time.GetUtcNow());

        foreach (var command in delivered)
        {
            Log(command, CommandEvent.Delivered, null);
        }

        return delivered;
    }

    /// <summary>Records a device's confirmation, or failure, of a command it fetched.</summary>
    public AckResult Acknowledge(Guid id, CommandAckRequest request)
    {
        var current = store.Find(id);

        if (current is null)
        {
            return new AckResult(AckOutcome.NotFound, null, $"No command has id {id}.");
        }

        switch (current.State)
        {
            case CommandState.Delivered:
                var now = time.GetUtcNow();
                var resulting = request.Success ? request.ResultingValue ?? current.TargetValue : request.ResultingValue;

                var moved = store.TryTransition(id, CommandState.Delivered, command => command with
                {
                    State = request.Success ? CommandState.Acknowledged : CommandState.Failed,
                    ResolvedAt = now,
                    ResultingValue = resulting,
                    FailureReason = request.Success ? null : request.Reason ?? "The device reported that it could not apply the command.",
                }, out var updated);

                if (!moved)
                {
                    // Lost a race to a timeout or a duplicate acknowledgement; answer from what it is now.
                    return Acknowledge(id, request);
                }

                if (request.Success)
                {
                    // The device confirmed the state it is in, which is an observation like any other.
                    states.Observe(updated!.MacAddress, resulting!.Value, now, ObservationSource.Acknowledgement);

                    // The device answered, so an earlier command to it that went unanswered no longer says
                    // anything about whether it is reachable now.
                    alerts.Clear(new AlertKey(updated.MacAddress, AlertKind.CommandTimedOut));
                }

                Log(updated!, request.Success ? CommandEvent.Acknowledged : CommandEvent.Failed, updated!.FailureReason);

                return new AckResult(AckOutcome.Applied, updated, null);

            case CommandState.Acknowledged when request.Success:
            case CommandState.Failed when !request.Success:
                // The same answer again. A device that never saw the reply sends it again, and repeating
                // it changes nothing.
                return new AckResult(AckOutcome.Repeated, current, null);

            case CommandState.Pending:
                return new AckResult(AckOutcome.Conflict, current, "The command has not been delivered yet, so there is nothing to acknowledge.");

            default:
                return new AckResult(
                    AckOutcome.Conflict,
                    current,
                    $"The command is {current.State}, so it cannot be {(request.Success ? "acknowledged" : "failed")} now.");
        }
    }

    /// <summary>Moves every command that has waited too long to <see cref="CommandState.TimedOut"/>.</summary>
    /// <returns>How many were timed out.</returns>
    public int TimeOutStale()
    {
        var now = time.GetUtcNow();
        var timedOut = 0;

        foreach (var stale in store.Stale(now - _options.PendingTimeout, now - _options.DeliveredTimeout))
        {
            var reason = stale.State == CommandState.Pending
                ? $"The device did not collect the command within {_options.PendingTimeout.TotalSeconds:0.#} seconds."
                : $"The device collected the command but did not confirm it within {_options.DeliveredTimeout.TotalSeconds:0.#} seconds.";

            if (store.TryTransition(stale.Id, stale.State, command => command with
            {
                State = CommandState.TimedOut,
                ResolvedAt = now,
                FailureReason = reason,
            }, out var updated))
            {
                Log(updated!, CommandEvent.TimedOut, reason);

                // A command nobody collected or confirmed is a node problem, not only a command problem. A
                // second timeout to the same device while the alert is active is counted, not re-raised.
                alerts.Raise(new AlertKey(updated!.MacAddress, AlertKind.CommandTimedOut), reason);
                timedOut++;
            }
        }

        return timedOut;
    }

    // ---- undo ---------------------------------------------------------------------------------------------

    private void Push(UndoEntry entry)
    {
        lock (_undoGate)
        {
            if (_undo.Count >= _options.MaxUndoDepth)
            {
                // Stack<T> cannot drop its oldest entry, only its newest. Dropping the bottom means taking
                // everything off and putting all but the oldest back. That is O(n), which at a depth of
                // twenty is nothing. A deque would be the right structure for a much larger bound; for this
                // one, keeping the plain stack is worth the small copy.
                var kept = _undo.ToArray();
                _undo.Clear();

                for (var i = _options.MaxUndoDepth - 2; i >= 0; i--)
                {
                    _undo.Push(kept[i]);
                }
            }

            _undo.Push(entry);
        }
    }

    /// <summary>The undo stack as it stands, most recent action first.</summary>
    public IReadOnlyList<UndoStackEntry> UndoStack()
    {
        lock (_undoGate)
        {
            return
            [
                .. _undo.Select(entry =>
                {
                    var commands = entry.CommandIds.Select(id => store.Find(id)!).ToList();

                    return new UndoStackEntry(entry.EntryId, entry.IssuedAt, entry.Description, entry.CompositeId, commands, StatusOf(commands));
                })
            ];
        }
    }

    /// <summary>
    /// Pops the most recent action and reverts it, command by command, according to how far each got.
    /// </summary>
    /// <remarks>
    /// The entry is removed from the stack even when part or all of it is refused. A refusal means the
    /// gateway cannot safely act on that command, and offering it again would only produce the same refusal
    /// and leave the operator unable to reach the actions beneath it.
    /// </remarks>
    public UndoResult Undo()
    {
        UndoEntry entry;

        lock (_undoGate)
        {
            if (_undo.Count == 0)
            {
                return new UndoResult(UndoOutcome.NothingToUndo, "There is nothing to undo.", null, []);
            }

            entry = _undo.Pop();
        }

        var items = entry.CommandIds.Select(UndoOne).ToList();
        var refused = items.Count(item => item.Result == UndoItemResult.Refused);

        var outcome = refused == 0 ? UndoOutcome.Reverted
            : refused == items.Count ? UndoOutcome.Refused
            : UndoOutcome.PartiallyReverted;

        var message = outcome switch
        {
            UndoOutcome.Reverted => $"Undid: {entry.Description}. {Summary(items)}",
            UndoOutcome.PartiallyReverted => $"Partly undid: {entry.Description}. {Summary(items)}",
            _ => $"Could not undo: {entry.Description}. {items[0].Reason}",
        };

        return new UndoResult(outcome, message, entry.EntryId, items);
    }

    private UndoItem UndoOne(Guid id)
    {
        // Two attempts, because a command can move between reading it and acting on it: a pending one can be
        // collected by its device in that gap. The second read sees the new state and takes the right branch.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var command = store.Find(id)!;

            switch (command.State)
            {
                case CommandState.Pending:
                    // Nothing has reached the device, so withdrawing the command undoes it completely.
                    if (store.TryTransition(id, CommandState.Pending, c => c with
                    {
                        State = CommandState.Cancelled,
                        ResolvedAt = time.GetUtcNow(),
                        FailureReason = "Cancelled by an undo before it reached the device.",
                    }, out var cancelled))
                    {
                        Log(cancelled!, CommandEvent.Cancelled, cancelled!.FailureReason);

                        return new UndoItem(id, command.MacAddress, UndoItemResult.Cancelled, null, "Withdrawn before it reached the device.");
                    }

                    continue;

                case CommandState.Delivered:
                case CommandState.Acknowledged:
                    if (!command.PreviousObserved)
                    {
                        return Refused(command, "The state this command replaced was never observed, so there is nothing recorded to return to. Restoring a guess could do harm.");
                    }

                    // A delivered command cannot be recalled from the device. The restore is queued behind it
                    // and reaches the device on its next check-in, after the command it undoes.
                    var restore = IssueRestore(command);
                    Log(command, CommandEvent.Reverted, $"Restore command {restore.Id} issued.");

                    return new UndoItem(
                        id,
                        command.MacAddress,
                        UndoItemResult.RestoreIssued,
                        restore.Id,
                        command.State == CommandState.Delivered
                            ? "The command was already with the device, so a restore was queued behind it."
                            : "A restore command was issued to return it to its recorded previous state.");

                case CommandState.TimedOut:
                    return Refused(command, "The command timed out, so the gateway does not know whether the device applied it. The actuator's real state is unknown, and restoring an unknown state could do harm.");

                case CommandState.Failed:
                    return Refused(command, "The device reported that it could not apply the command, so the state it is in is unknown. Restoring an unknown state could do harm.");

                default:
                    return new UndoItem(id, command.MacAddress, UndoItemResult.AlreadyCancelled, null, "It had already been cancelled, so there was nothing to undo.");
            }
        }

        return Refused(store.Find(id)!, "The command kept changing state while it was being undone. Try again.");
    }

    private static UndoItem Refused(Command command, string reason) =>
        new(command.Id, command.MacAddress, UndoItemResult.Refused, null, reason);

    /// <summary>
    /// Builds the command that returns an actuator to the state a command replaced.
    /// </summary>
    /// <remarks>
    /// A restore is an ordinary command with a link back to the one it undoes, and it is <b>not</b> pushed onto
    /// the undo stack. If it were, undoing it would restore the state it replaced, which is the state the
    /// operator just undid, and repeated undo would bounce an actuator back and forth for ever.
    /// </remarks>
    private Command IssueRestore(Command original)
    {
        var previous = original.PreviousValue!.Value;
        var action = CommandActionRules.ActionToReach(original.Kind, previous);

        return Create(
            original.MacAddress,
            original.Kind,
            action,
            original.Kind == ActuatorKind.Dimmer ? previous : null,
            compositeId: null,
            reverts: original.Id);
    }

    private static string Summary(IReadOnlyList<UndoItem> items)
    {
        var restored = items.Count(item => item.Result == UndoItemResult.RestoreIssued);
        var cancelled = items.Count(item => item.Result == UndoItemResult.Cancelled);
        var refused = items.Where(item => item.Result == UndoItemResult.Refused).ToList();

        List<string> parts = [];

        if (cancelled > 0)
        {
            parts.Add($"{cancelled} withdrawn before reaching the device");
        }

        if (restored > 0)
        {
            parts.Add($"{restored} being restored to its previous state");
        }

        if (refused.Count > 0)
        {
            parts.Add($"{refused.Count} refused ({string.Join(", ", refused.Select(item => item.MacAddress))})");
        }

        return parts.Count == 0 ? string.Empty : string.Join("; ", parts) + ".";
    }

    // ---- status and description --------------------------------------------------------------------------

    /// <summary>How a group of commands is going, from the states of its members.</summary>
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

    /// <summary>The commands of one zone-wide action, in issue order.</summary>
    public IReadOnlyList<Command> Composite(Guid compositeId) => store.ForComposite(compositeId);

    private static string Describe(Command command) =>
        $"{command.Action}{LevelText(command.Level)} on {command.MacAddress}";

    private static string LevelText(double? level) =>
        level is { } value ? $" to {value.ToString("0.##", CultureInfo.InvariantCulture)}" : string.Empty;

    private void Log(Command command, CommandEvent commandEvent, string? detail)
    {
        AppendToLog(command, commandEvent, detail);

        var handlers = Changed;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<Command, CommandEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(command, commandEvent);
            }
            catch
            {
                // Deliberately swallowed. See the remarks on Changed.
            }
        }
    }

    private void AppendToLog(Command command, CommandEvent commandEvent, string? detail) =>
        log.Append(
            command.Id,
            command.MacAddress,
            commandEvent,
            time.GetUtcNow(),
            trust.TryGet(command.MacAddress, out var snapshot) ? snapshot.Band : null,
            detail,
            Describe(command));
}
