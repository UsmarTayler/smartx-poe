using Microsoft.Extensions.Options;

namespace SmartX.Api.Trust;

/// <summary>
/// Rejects trust configuration that would make the engine misbehave rather than merely score
/// differently.
/// </summary>
/// <remarks>
/// Registered with ValidateOnStart, so a bad value stops the API at startup with a message naming
/// it. The alternative is discovering a zero half-life as a divide-by-zero on the first packet, or
/// inverted thresholds as a dashboard where no score can ever be nominal.
/// </remarks>
internal sealed class TrustOptionsValidator : IValidateOptions<TrustOptions>
{
    public ValidateOptionsResult Validate(string? name, TrustOptions options)
    {
        List<string> failures = [];

        if (!(options.SuccessWeight > 0))
        {
            failures.Add($"{nameof(TrustOptions.SuccessWeight)} must be greater than zero.");
        }

        // Zero is allowed for failure weights: it is a legitimate way to switch one kind of failure
        // off. Negative would turn a failure into a success.
        if (!(options.RangeViolationWeight >= 0)
            || !(options.SchemaFailureWeight >= 0)
            || !(options.MissedHeartbeatWeight >= 0))
        {
            failures.Add("Failure weights must be zero or greater.");
        }

        if (options.HalfLife <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(TrustOptions.HalfLife)} must be positive.");
        }

        if (options.OutcomeWindow < 1)
        {
            failures.Add($"{nameof(TrustOptions.OutcomeWindow)} must hold at least one outcome.");
        }

        if (options.HistoryLength < 0 || options.HistorySampleInterval < TimeSpan.Zero)
        {
            failures.Add("History length and sample interval cannot be negative.");
        }

        if (!(options.FaultThreshold > 0
              && options.FaultThreshold < options.NominalThreshold
              && options.NominalThreshold <= 1))
        {
            failures.Add(
                $"Thresholds must satisfy 0 < {nameof(TrustOptions.FaultThreshold)} < " +
                $"{nameof(TrustOptions.NominalThreshold)} <= 1.");
        }

        // A node nobody has heard from yet sits at the Laplace prior of 0.5. If the fault threshold
        // were above that, every newly registered sensor would start life in the fault band — and
        // since it never crossed into it, no transition would fire, so escalation would never hear
        // about it. Keeping the threshold at or below the prior means a node can only reach fault
        // on evidence.
        if (options.FaultThreshold > SmartX.Shared.TrustFormula.PriorScore)
        {
            failures.Add(
                $"{nameof(TrustOptions.FaultThreshold)} cannot exceed the prior score of " +
                $"{SmartX.Shared.TrustFormula.PriorScore}, or new nodes would start in the fault band.");
        }

        if (options.DefaultHeartbeatInterval <= TimeSpan.Zero
            || options.HeartbeatCheckInterval <= TimeSpan.Zero)
        {
            failures.Add("Heartbeat intervals must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
