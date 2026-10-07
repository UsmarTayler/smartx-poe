using SmartX.Api.Alerts;
using Microsoft.Extensions.Options;
using SmartX.Api.History;
using SmartX.Api.Patterns;
using SmartX.Shared;

namespace SmartX.Api.Search;

/// <summary>
/// Records what an operator does, with the state of the mesh when they did it.
/// </summary>
/// <remarks>
/// <b>Why this exists now.</b> The pattern analysis later in the project learns from what operators actually do:
/// which searches they run, and what they then command, in which situations. That can only be learned from
/// history that was recorded at the time. A search made last week cannot be reconstructed this week, and neither
/// can the set of alerts that was active when it was made, so recording starts now even though nothing reads it
/// yet.
/// <para>
/// <b>Assumption: the client debounces.</b> One search is recorded per request the server receives. The client
/// is expected to wait for a pause in typing before it asks, so that "pump" is one search and not four
/// requests for "p", "pu", "pum" and "pump". If it did not, the record would fill with half-typed words that
/// carry no intent and the analysis would learn from keystrokes. The server cannot tell the difference between a
/// deliberate search and a keystroke, so it records what it receives and relies on the client to send only the
/// former.
/// </para>
/// <para>
/// Searches belong to the mesh and not to a node, so they are stored under <see cref="HistoryEntry.MeshScope"/>.
/// </para>
/// </remarks>
internal sealed class ActivityRecorder(
    IHistoryStore history,
    ArrivalSequence arrival,
    AlertTracker alerts,
    TimeProvider time,
    IPatternEngine patterns,
    IOptions<PatternOptions> patternOptions)
{
    /// <summary>Records a search that ran, and tells the pattern engine when it was narrow enough to be about particular devices.</summary>
    /// <param name="filters">What was asked for.</param>
    /// <param name="matches">The devices that matched.</param>
    /// <remarks>
    /// A search that finds a few devices is read as interest in them, at a lower weight than opening one: the operator
    /// looked for it and did not open it. A search that finds many is not, since crediting all of them would teach the
    /// engine that the operator wants everything.
    /// </remarks>
    public void RecordSearch(DeviceSearchFilters filters, IReadOnlyList<DeviceSearchHit> matches)
    {
        var now = time.GetUtcNow();

        history.Append(HistoryEntry.FromEvent(
            arrival.Next(),
            now,
            HistoryEntry.MeshScope,
            HistoryKind.Search,
            new HistoryEventDetails(
                Describe(filters, matches.Count),
                Filters: filters,
                ResultCount: matches.Count,
                ActiveAlerts: alerts.ActiveKeys())));

        if (!filters.IsEmpty && matches.Count >= 1 && matches.Count <= patternOptions.Value.MaxSearchResultsForAction)
        {
            foreach (var match in matches)
            {
                patterns.RecordAction(new OperatorAction(OperatorActionKind.Searched, match.MacAddress, string.Empty, now));
            }
        }
    }

    /// <summary>Records an operator opening a device, which is the strongest sign of what they were interested in.</summary>
    /// <param name="macAddress">The device.</param>
    /// <param name="room">Where it is, for the summary.</param>
    /// <remarks>
    /// Stored under the device's own address so the activity record says which device, but kept out of its timeline
    /// markers and the mesh event stream, like a search: it is what the operator did and not something that happened to
    /// the device.
    /// </remarks>
    public void RecordSelection(string macAddress, string room)
    {
        var now = time.GetUtcNow();

        history.Append(HistoryEntry.FromEvent(
            arrival.Next(),
            now,
            macAddress,
            HistoryKind.Selection,
            new HistoryEventDetails($"Opened {room}", ActiveAlerts: alerts.ActiveKeys())));

        patterns.RecordAction(new OperatorAction(OperatorActionKind.Viewed, macAddress, string.Empty, now));
    }

    private static string Describe(DeviceSearchFilters filters, int resultCount)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(filters.Q))
        {
            parts.Add($"text '{filters.Q}'");
        }

        if (filters.Category is { } category)
        {
            parts.Add($"category {category}");
        }

        if (filters.ActuatorKind is { } kind)
        {
            parts.Add($"actuator {kind}");
        }

        if (!string.IsNullOrWhiteSpace(filters.Zone))
        {
            parts.Add($"zone {filters.Zone}");
        }

        if (filters.Band is { } band)
        {
            parts.Add($"band {band}");
        }

        if (filters.Alert is { } alert)
        {
            parts.Add($"alert {alert}");
        }

        var asked = parts.Count == 0 ? "all devices" : string.Join(", ", parts);

        return $"Searched {asked}: {resultCount} found";
    }
}
