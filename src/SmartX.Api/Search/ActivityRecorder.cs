using SmartX.Api.Alerts;
using SmartX.Api.History;
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
    TimeProvider time)
{
    /// <summary>Records a search that ran.</summary>
    /// <param name="filters">What was asked for.</param>
    /// <param name="resultCount">How many devices matched.</param>
    public void RecordSearch(DeviceSearchFilters filters, int resultCount) =>
        history.Append(HistoryEntry.FromEvent(
            arrival.Next(),
            time.GetUtcNow(),
            HistoryEntry.MeshScope,
            HistoryKind.Search,
            new HistoryEventDetails(
                Describe(filters, resultCount),
                Filters: filters,
                ResultCount: resultCount,
                ActiveAlerts: alerts.ActiveKeys())));

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
