using SmartX.Shared;

namespace SmartX.Api.Commands;

/// <summary>
/// Holds every command the gateway has issued.
/// </summary>
/// <remarks>
/// Every change of state goes through <see cref="TryTransition"/>, which checks the state it expects and
/// changes it in one step. That is what stops a command being both cancelled by an operator and delivered to
/// its device in the same instant: whichever gets there first wins, and the other is told it lost.
/// </remarks>
internal interface ICommandStore
{
    /// <summary>Records a newly issued command.</summary>
    void Add(Command command);

    /// <summary>One command by id.</summary>
    Command? Find(Guid id);

    /// <summary>
    /// Changes a command in one step, if it is in the state the caller expects.
    /// </summary>
    /// <param name="id">The command.</param>
    /// <param name="expected">The state it must be in. Anything else and nothing changes.</param>
    /// <param name="change">Produces the new value from the current one.</param>
    /// <param name="updated">The command after the change, or as it currently stands if the change was refused.</param>
    /// <returns>False if the command does not exist or was not in the expected state.</returns>
    bool TryTransition(Guid id, CommandState expected, Func<Command, Command> change, out Command? updated);

    /// <summary>
    /// Hands over every pending command for a device, marking each delivered as it goes.
    /// </summary>
    /// <remarks>
    /// The whole move happens under one lock. Two check-ins arriving together therefore cannot both be given
    /// the same command: the first takes it, and the second finds it already delivered and gets nothing.
    /// This is one half of making at-least-once delivery safe.
    /// </remarks>
    IReadOnlyList<Command> Collect(string macAddress, DateTimeOffset now);

    /// <summary>
    /// The state the device is on its way to: the target of its most recently issued command that has not
    /// yet been resolved.
    /// </summary>
    Command? LatestLive(string macAddress);

    /// <summary>Commands, newest first, optionally narrowed to one device and one state.</summary>
    IReadOnlyList<Command> List(string? macAddress, CommandState? state, int limit);

    /// <summary>Commands that share a composite id, in issue order.</summary>
    IReadOnlyList<Command> ForComposite(Guid compositeId);

    /// <summary>Pending commands issued at or before a cutoff, and delivered ones delivered at or before another.</summary>
    IReadOnlyList<Command> Stale(DateTimeOffset pendingCutoff, DateTimeOffset deliveredCutoff);
}

internal sealed class InMemoryCommandStore : ICommandStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Command> _byId = [];

    // Issue order, per device, so commands are handed over in the order they were sent. An actuator told to
    // open and then to close must be told in that order, or it ends up open.
    private readonly Dictionary<string, List<Guid>> _byDevice = new(StringComparer.Ordinal);

    private readonly List<Guid> _issueOrder = [];

    public void Add(Command command)
    {
        lock (_gate)
        {
            _byId.Add(command.Id, command);
            _issueOrder.Add(command.Id);

            if (!_byDevice.TryGetValue(command.MacAddress, out var ids))
            {
                _byDevice[command.MacAddress] = ids = [];
            }

            ids.Add(command.Id);
        }
    }

    public Command? Find(Guid id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    public bool TryTransition(Guid id, CommandState expected, Func<Command, Command> change, out Command? updated)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var current))
            {
                updated = null;

                return false;
            }

            if (current.State != expected)
            {
                updated = current;

                return false;
            }

            updated = _byId[id] = change(current);

            return true;
        }
    }

    public IReadOnlyList<Command> Collect(string macAddress, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_byDevice.TryGetValue(macAddress, out var ids))
            {
                return [];
            }

            List<Command> delivered = [];

            foreach (var id in ids)
            {
                if (_byId[id].State != CommandState.Pending)
                {
                    continue;
                }

                var command = _byId[id] with { State = CommandState.Delivered, DeliveredAt = now };
                _byId[id] = command;
                delivered.Add(command);
            }

            return delivered;
        }
    }

    public Command? LatestLive(string macAddress)
    {
        lock (_gate)
        {
            if (!_byDevice.TryGetValue(macAddress, out var ids))
            {
                return null;
            }

            for (var i = ids.Count - 1; i >= 0; i--)
            {
                var command = _byId[ids[i]];

                if (command.State is CommandState.Pending or CommandState.Delivered)
                {
                    return command;
                }
            }

            return null;
        }
    }

    public IReadOnlyList<Command> List(string? macAddress, CommandState? state, int limit)
    {
        lock (_gate)
        {
            List<Command> found = [];

            for (var i = _issueOrder.Count - 1; i >= 0 && found.Count < limit; i--)
            {
                var command = _byId[_issueOrder[i]];

                if ((macAddress is null || string.Equals(command.MacAddress, macAddress, StringComparison.Ordinal))
                    && (state is null || command.State == state))
                {
                    found.Add(command);
                }
            }

            return found;
        }
    }

    public IReadOnlyList<Command> ForComposite(Guid compositeId)
    {
        lock (_gate)
        {
            return [.. _issueOrder.Select(id => _byId[id]).Where(command => command.CompositeId == compositeId)];
        }
    }

    public IReadOnlyList<Command> Stale(DateTimeOffset pendingCutoff, DateTimeOffset deliveredCutoff)
    {
        lock (_gate)
        {
            return
            [
                .. _byId.Values.Where(command =>
                    (command.State == CommandState.Pending && command.IssuedAt <= pendingCutoff)
                    || (command.State == CommandState.Delivered && command.DeliveredAt <= deliveredCutoff))
            ];
        }
    }
}
