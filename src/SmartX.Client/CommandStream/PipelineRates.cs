using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>What one lane is doing now, in the units the strip shows.</summary>
/// <param name="Lane">"standard" or "critical".</param>
/// <param name="Depth">Readings waiting in the lane.</param>
/// <param name="ProcessedPerSecond">How fast the worker is taking from it, or null before there are two samples to compare.</param>
/// <param name="AverageWaitMilliseconds">Mean time from acceptance to processing.</param>
/// <param name="MaximumWaitMilliseconds">The longest any reading waited.</param>
public sealed record LaneView(
    string Lane,
    int Depth,
    double? ProcessedPerSecond,
    double AverageWaitMilliseconds,
    double MaximumWaitMilliseconds);

/// <summary>
/// Turns the pipeline's running totals into rates.
/// </summary>
/// <remarks>
/// The API reports how many readings each lane has processed since it started, a number that only goes up. A rate is the
/// difference between two samples over the time between them, so this keeps the previous sample. If the total goes
/// down the API has restarted, and the sample is treated as the first again rather than showing a negative rate.
/// </remarks>
public sealed class PipelineRates
{
    private PipelineStats? _previous;

    /// <summary>Records a sample and returns each lane as it should be shown.</summary>
    public IReadOnlyList<LaneView> Update(PipelineStats stats)
    {
        var views = new List<LaneView>(stats.Lanes.Count);

        foreach (var lane in stats.Lanes)
        {
            double? rate = null;

            if (_previous?.Lanes.FirstOrDefault(previous => previous.Lane == lane.Lane) is { } before
                && lane.Processed >= before.Processed)
            {
                var seconds = (stats.SampledAt - _previous.SampledAt).TotalSeconds;

                if (seconds > 0)
                {
                    rate = (lane.Processed - before.Processed) / seconds;
                }
            }

            views.Add(new LaneView(lane.Lane, lane.Depth, rate, lane.AverageWaitMilliseconds, lane.MaximumWaitMilliseconds));
        }

        _previous = stats;

        return views;
    }

    /// <summary>
    /// How many times sooner the critical lane processes than the standard lane, from their average waits, or null if
    /// either has none yet.
    /// </summary>
    /// <remarks>The figure is the pipeline's own, as measured; nothing here is estimated.</remarks>
    public static double? Advantage(IReadOnlyList<LaneView> lanes)
    {
        var critical = lanes.FirstOrDefault(lane => lane.Lane == "critical");
        var standard = lanes.FirstOrDefault(lane => lane.Lane == "standard");

        return critical is { AverageWaitMilliseconds: > 0 } && standard is { AverageWaitMilliseconds: > 0 }
            ? standard.AverageWaitMilliseconds / critical.AverageWaitMilliseconds
            : null;
    }
}
