using SmartX.Shared;

namespace SmartX.Api.Trust;

/// <summary>
/// Turns the evidence behind a score into a short sentence an operator can act on.
/// </summary>
/// <remarks>
/// The rules, in priority order: a node that is currently silent past its heartbeat is described
/// by its silence, because nothing else about it is current; otherwise by its heaviest one or two
/// kinds of recent failure; otherwise as healthy. Failure kinds are ranked by their decayed weight,
/// not their raw count, so one fresh type failure outranks three range violations from twenty
/// minutes ago — the reason names whatever is actually holding the score down.
/// </remarks>
internal static class TrustReasons
{
    /// <summary>Describes a node's current standing.</summary>
    /// <param name="counts">Recent outcomes per kind, indexed by <see cref="TrustOutcome"/>.</param>
    /// <param name="weights">Decayed failure weight per kind, indexed the same way.</param>
    /// <param name="retainedOutcomes">Every outcome still in the node's window, recent or not.</param>
    /// <param name="silence">How long the node has been silent past its heartbeat, if it is.</param>
    internal static string Describe(
        ReadOnlySpan<int> counts,
        ReadOnlySpan<double> weights,
        int retainedOutcomes,
        TimeSpan? silence)
    {
        if (silence is { } quiet)
        {
            return $"no packets for {FormatDuration(quiet)}";
        }

        var heaviest = HeaviestFailure(counts, weights, excluding: -1);

        if (heaviest < 0)
        {
            var successes = counts[(int)TrustOutcome.Success];

            return successes > 0 ? $"{Count(successes, "recent packet", "recent packets")} valid"
                : retainedOutcomes > 0 ? "no recent telemetry"
                : "no telemetry yet";
        }

        var first = Label(heaviest, counts[heaviest]);
        var next = HeaviestFailure(counts, weights, excluding: heaviest);

        return next < 0 ? first : $"{first}, {Label(next, counts[next])}";
    }

    /// <summary>Formats a duration the way a person would say it: "4m 20s", "2h 5m", "45s".</summary>
    /// <remarks>Two units at most. "1h 4m 20s" is precision nobody reading a dashboard needs.</remarks>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalDays >= 1 ? $"{(int)duration.TotalDays}d {duration.Hours}h"
            : duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
            : $"{duration.Seconds}s";
    }

    /// <summary>The failure kind with the most decayed weight, or -1 if there is none.</summary>
    /// <remarks>
    /// A manual scan over three entries rather than a LINQ OrderBy: this runs on every packet, and a
    /// sort would allocate to rank a list whose length is fixed at three.
    /// </remarks>
    private static int HeaviestFailure(ReadOnlySpan<int> counts, ReadOnlySpan<double> weights, int excluding)
    {
        var best = -1;

        for (var kind = (int)TrustOutcome.RangeViolation; kind <= (int)TrustOutcome.MissedHeartbeat; kind++)
        {
            if (kind != excluding && counts[kind] > 0 && (best < 0 || weights[kind] > weights[best]))
            {
                best = kind;
            }
        }

        return best;
    }

    private static string Label(int kind, int count) => (TrustOutcome)kind switch
    {
        TrustOutcome.RangeViolation => Count(count, "range violation", "range violations"),
        TrustOutcome.SchemaFailure => Count(count, "type failure", "type failures"),
        _ => Count(count, "missed heartbeat", "missed heartbeats")
    };

    private static string Count(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";
}
