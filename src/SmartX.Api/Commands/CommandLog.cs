using SmartX.Shared;

namespace SmartX.Api.Commands;

/// <summary>
/// The record of everything that has happened to every command.
/// </summary>
/// <remarks>
/// Behind an interface because it is about to move. The in-memory list below is the simplest thing that
/// accumulates the record from now; the next stage stores it in a sorted history store, and the pattern
/// analysis after that reads from it. Neither should need the code that writes to the log to change.
/// </remarks>
internal interface ICommandLog
{
    /// <summary>Appends an event. Entries are only ever added, never changed or removed.</summary>
    void Append(Guid commandId, string macAddress, CommandEvent commandEvent, DateTimeOffset timestamp, TrustBand? trustBand, string? detail);

    /// <summary>Entries, newest first, optionally narrowed.</summary>
    IReadOnlyList<CommandLogEntry> Query(string? macAddress, Guid? commandId, int limit);
}

internal sealed class InMemoryCommandLog : ICommandLog
{
    private readonly Lock _gate = new();
    private readonly List<CommandLogEntry> _entries = [];

    public void Append(Guid commandId, string macAddress, CommandEvent commandEvent, DateTimeOffset timestamp, TrustBand? trustBand, string? detail)
    {
        lock (_gate)
        {
            _entries.Add(new CommandLogEntry(_entries.Count + 1, commandId, macAddress, commandEvent, timestamp, trustBand, detail));
        }
    }

    public IReadOnlyList<CommandLogEntry> Query(string? macAddress, Guid? commandId, int limit)
    {
        lock (_gate)
        {
            List<CommandLogEntry> found = [];

            for (var i = _entries.Count - 1; i >= 0 && found.Count < limit; i--)
            {
                var entry = _entries[i];

                if ((macAddress is null || string.Equals(entry.MacAddress, macAddress, StringComparison.Ordinal))
                    && (commandId is null || entry.CommandId == commandId))
                {
                    found.Add(entry);
                }
            }

            return found;
        }
    }
}
