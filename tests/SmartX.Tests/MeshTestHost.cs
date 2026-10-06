using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.History;
using SmartX.Api.Search;

namespace SmartX.Tests;

/// <summary>
/// Builds the history, the device index and the alert set the way the API wires them, against a clock the test controls.
/// </summary>
internal sealed class MeshTestHost
{
    public required DeviceIndex Index { get; init; }

    public required IHistoryStore History { get; init; }

    public required ArrivalSequence Sequence { get; init; }

    public required AlertTracker Alerts { get; init; }

    public required ICommandLog CommandLog { get; init; }

    public static MeshTestHost Create(TimeProvider clock, Action<AlertOptions>? configure = null)
    {
        var options = new AlertOptions();
        configure?.Invoke(options);

        var index = new DeviceIndex();
        IHistoryStore history = new SortedDictionaryHistoryStore();
        var sequence = new ArrivalSequence();
        var alerts = new AlertTracker(index, history, sequence, Options.Create(options), clock);

        return new MeshTestHost
        {
            Index = index,
            History = history,
            Sequence = sequence,
            Alerts = alerts,
            CommandLog = new HistoryCommandLog(history, sequence, alerts),
        };
    }
}
