using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>
/// The words the suggestion, insight and investigation panels put on the screen.
/// </summary>
/// <remarks>
/// Kept out of the components so they are a function of their inputs and can be tested without drawing anything. The rule for
/// everything here is the one the trust bands set: status is never colour alone, so each state has a word, and the words here
/// are those words.
/// </remarks>
public static class SuggestionPresentation
{
    /// <summary>
    /// The longest gap between a trigger and a suggestion arriving that is worth saying. Past it the suggestion did not arrive because
    /// of the trigger: it was ranked below the top few until a place opened, or the clocks disagree, and "arrived 15 s after the
    /// trigger" would be a statement about the network that is not true.
    /// </summary>
    private static readonly TimeSpan LongestLatency = TimeSpan.FromSeconds(5);

    /// <summary>How long a suggestion took to reach this page after its trigger, in words, or null when it cannot be said honestly.</summary>
    /// <remarks>
    /// Measured with this browser's clock against the server's, so a clock that is behind would give a negative number, and the
    /// measurement is not shown at all in that case rather than shown as zero.
    /// </remarks>
    public static string? Latency(Suggestion suggestion, DateTimeOffset arrivedAt)
    {
        var took = arrivedAt - suggestion.TriggeredAt;

        if (took < TimeSpan.Zero || took > LongestLatency)
        {
            return null;
        }

        return took < TimeSpan.FromSeconds(1)
            ? $"Arrived {took.TotalMilliseconds:0} ms after the trigger."
            : $"Arrived {took.TotalSeconds:0.0} s after the trigger.";
    }

    /// <summary>The time left to a projected fault, as the page shows it: counted down from the moment the server projected, not from when it was sent.</summary>
    public static string Countdown(Insight insight, DateTimeOffset now)
    {
        if (insight.ProjectedFaultAt is not { } at)
        {
            return insight.Detail;
        }

        var left = at - now;

        if (left <= TimeSpan.Zero)
        {
            return "Projected to reach Fault about now.";
        }

        if (left < TimeSpan.FromMinutes(1))
        {
            return "Projected to reach Fault in under a minute.";
        }

        var minutes = (int)Math.Round(left.TotalMinutes);

        return $"Projected to reach Fault in about {minutes} {(minutes == 1 ? "minute" : "minutes")}.";
    }

    /// <summary>Whether a suggestion or insight has arrived recently enough to be highlighted.</summary>
    public static bool IsNew(IReadOnlyDictionary<string, DateTimeOffset> arrivals, string id, DateTimeOffset now, TimeSpan highlight) =>
        arrivals.TryGetValue(id, out var arrived) && now - arrived < highlight;

    /// <summary>How much longer the newest highlight has to run, or null when none is running. For setting the timer that takes it off.</summary>
    public static TimeSpan? HighlightEnds(IReadOnlyDictionary<string, DateTimeOffset> arrivals, DateTimeOffset now, TimeSpan highlight)
    {
        TimeSpan? longest = null;

        foreach (var arrived in arrivals.Values)
        {
            var remaining = arrived + highlight - now;

            if (remaining > TimeSpan.Zero && (longest is null || remaining > longest))
            {
                longest = remaining;
            }
        }

        return longest;
    }

    /// <summary>What the operator's verdict was, in words.</summary>
    public static string Verdict(InvestigationDetails investigation) => investigation.Verdict switch
    {
        Shared.Verdict.Confirm => "Confirmed as a real fault",
        Shared.Verdict.Dismiss => "Dismissed as a false alarm",
        Shared.Verdict.Relabel => $"Relabelled: the cause was {Cause(investigation.Cause)}",
        _ => "No verdict yet",
    };

    /// <summary>What a relabelled cause means.</summary>
    public static string Cause(FaultCause? cause) => cause switch
    {
        FaultCause.Hardware => "hardware",
        FaultCause.Network => "the network",
        FaultCause.Power => "power",
        FaultCause.Environmental => "the environment",
        _ => "not stated",
    };

    /// <summary>What the trust engine blamed, in words.</summary>
    public static string Blamed(TrustOutcome? outcome) => outcome switch
    {
        TrustOutcome.RangeViolation => "readings outside the registered range",
        TrustOutcome.SchemaFailure => "readings of the wrong type",
        TrustOutcome.MissedHeartbeat => "missed heartbeats",
        TrustOutcome.Success => "nothing in particular",
        null => "it could not tell",
        _ => outcome.ToString()!,
    };

    /// <summary>The span an investigation's decay window covers, in words.</summary>
    public static string Window(InvestigationDetails investigation)
    {
        var length = investigation.WindowEnd - investigation.WindowStart;

        return $"{ReadingFormatter.Time(investigation.WindowStart)} to {ReadingFormatter.Time(investigation.WindowEnd)} ({Length(length)})";
    }

    private static string Length(TimeSpan span) => span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} min" : $"{span.TotalSeconds:0} s";

    /// <summary>What operators did the last time this node, or one like it, faulted, in a sentence. Null when nothing has been learned.</summary>
    public static string? Precedent(PatternAnswer precedent)
    {
        if (precedent.AnsweredBy is not { } level || precedent.Associations.Count == 0)
        {
            return null;
        }

        var best = precedent.Associations[0];
        var who = level == PatternLevel.Specific ? "this node" : "nodes like it";
        var did = best.Action.Kind switch
        {
            OperatorActionKind.Command => $"{best.Action.Command} on {best.Action.TargetLabel}",
            OperatorActionKind.Verdict => $"gave the verdict {best.Action.Command}",
            _ => $"opened {best.Action.TargetLabel}",
        };

        return $"Last time {who} faulted, operators {did}, {best.Confidence * 100:0}% of the time.";
    }

    /// <summary>What a dismissal did to detection, in words. Null when it changed nothing.</summary>
    public static string? SensitivityNote(InvestigationDetails investigation) =>
        investigation.SensitivityChange is { } change
            ? $"This node's weight for {Blamed(change.Outcome)} went from {change.Before:0.00} to {change.After:0.00}, so it needs a little more evidence to be called a fault. Changing the verdict gives that back."
            : null;
}
