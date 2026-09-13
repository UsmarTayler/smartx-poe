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

        if (options.HistoryLength < 0 || options.SnapshotHistoryPoints < 0 || options.HistorySampleInterval < TimeSpan.Zero)
        {
            failures.Add("History length, snapshot history points and sample interval cannot be negative.");
        }

        // No rule tying the fault threshold to the 0.5 prior any more. It used to exist because a
        // threshold above 0.5 would have started every new node in the fault band without an event.
        // A node with no evidence is now Unproven regardless of the thresholds, so the risk it
        // guarded against cannot arise.
        if (!(options.FaultThreshold > 0
              && options.FaultThreshold < options.NominalThreshold
              && options.NominalThreshold <= 1))
        {
            failures.Add(
                $"Thresholds must satisfy 0 < {nameof(TrustOptions.FaultThreshold)} < " +
                $"{nameof(TrustOptions.NominalThreshold)} <= 1.");
        }

        if (options.DefaultHeartbeatInterval <= TimeSpan.Zero
            || options.HeartbeatCheckInterval <= TimeSpan.Zero)
        {
            failures.Add("Heartbeat intervals must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
