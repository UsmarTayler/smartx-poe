using SmartX.Api.Alerts;
using SmartX.Api.History;
using SmartX.Shared;

namespace SmartX.Api.Commands;

/// <summary>
/// The record of everything that has happened to every command.
/// </summary>
/// <remarks>
/// Behind an interface so the code that writes to the log did not have to change when the log moved. It began as
/// a list that only ever grew; it is now a view over the history store, so command events share the
/// retention window with everything else and the log can no longer grow without bound.
/// </remarks>
internal interface ICommandLog
{
    /// <summary>Appends an event. Entries are only ever added, never changed or removed.</summary>
    void Append(Guid commandId, string macAddress, CommandEvent commandEvent, DateTimeOffset timestamp, TrustBand? trustBand, string? detail);

    /// <summary>Entries, newest first, optionally narrowed.</summary>
    IReadOnlyList<CommandLogEntry> Query(string? macAddress, Guid? commandId, int limit);
}

/// <summary>
/// Stores command events in the history store, each with the alerts that were active when it happened.
/// </summary>
/// <remarks>
/// <b>The alert snapshot.</b> Whether an operator acted while something was already wrong is context that
/// cannot be reconstructed afterwards, because alerts come and go. So the set of active alert keys is copied
/// onto the entry at the moment it is written. The pattern analysis later reads these to learn what operators
/// do in what situations.
/// <para>
/// Reading goes through the mesh-wide event stream rather than a node's own history, because that stream holds
/// only events. A node's own history is dominated by its readings, and finding a command in it would mean
/// walking all of them.
/// </para>
/// </remarks>
internal sealed class HistoryCommandLog(IHistoryStore history, ArrivalSequence arrival, AlertTracker alerts) : ICommandLog
{
    public void Append(Guid commandId, string macAddress, CommandEvent commandEvent, DateTimeOffset timestamp, TrustBand? trustBand, string? detail) =>
        history.Append(HistoryEntry.FromEvent(
            arrival.Next(),
            timestamp,
            macAddress,
            HistoryKind.Command,
            new HistoryEventDetails(
                $"Command {commandEvent}",
                detail,
                CommandId: commandId,
                CommandEvent: commandEvent,
                Band: trustBand,
                ActiveAlerts: alerts.ActiveKeys())));

    public IReadOnlyList<CommandLogEntry> Query(string? macAddress, Guid? commandId, int limit)
    {
        var events = history.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        List<CommandLogEntry> found = [];

        // The stream is oldest first and the log answers newest first, so it is read from the back.
        for (var i = events.Count - 1; i >= 0 && found.Count < limit; i--)
        {
            var entry = events[i];

            if (entry.Kind != HistoryKind.Command
                || entry.Event is not { CommandId: { } id, CommandEvent: { } commandEvent } details
                || (macAddress is not null && !string.Equals(entry.MacAddress, macAddress, StringComparison.Ordinal))
                || (commandId is not null && id != commandId))
            {
                continue;
            }

            found.Add(new CommandLogEntry(
                entry.Sequence,
                id,
                entry.MacAddress,
                commandEvent,
                entry.Timestamp,
                details.Band,
                details.Detail,
                details.ActiveAlerts));
        }

        return found;
    }
}
