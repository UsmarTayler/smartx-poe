using SmartX.Shared;

namespace SmartX.Api.Trust;

/// <summary>
/// Everything the engine knows about one node.
/// </summary>
/// <remarks>
/// Mutable and internal, and never handed out: every read and write happens inside the engine's
/// lock, and what leaves the engine is an immutable <see cref="NodeTrustSnapshot"/>. A caller
/// holding a reference to this could otherwise watch a score change halfway through serialising it.
/// <para>
/// Two ring buffers, for two different questions. <see cref="Outcomes"/> is the evidence — what the
/// node has done recently — and is what the score is computed from. <see cref="History"/> is the
/// score itself sampled over time, which is what a sparkline draws. They are separate because they
/// age at different rates: outcomes arrive per packet, samples at most once per sample interval.
/// </para>
/// </remarks>
internal sealed class NodeTrust
{
    internal NodeTrust(
        string macAddress,
        DateTimeOffset trackedSince,
        TrustBand initialBand,
        int outcomeWindow,
        int historyLength)
    {
        MacAddress = macAddress;
        TrackedSince = trackedSince;
        Outcomes = new TelemetryRingBuffer<OutcomeRecord>(outcomeWindow);
        History = new TelemetryRingBuffer<TrustHistoryPoint>(historyLength);

        Score = TrustFormula.PriorScore;
        Band = initialBand;
        Reason = "no telemetry yet";
        EvaluatedAt = trackedSince;
    }

    internal string MacAddress { get; }

    internal DateTimeOffset TrackedSince { get; }

    /// <summary>Recent outcomes, the evidence the score is computed from.</summary>
    internal TelemetryRingBuffer<OutcomeRecord> Outcomes { get; }

    /// <summary>The score sampled over time, for sparklines.</summary>
    internal TelemetryRingBuffer<TrustHistoryPoint> History { get; }

    /// <summary>When the node last sent anything at all, valid or not.</summary>
    /// <remarks>
    /// Updated by a failed packet as well as a good one. A node sending malformed data is
    /// misbehaving, but it is demonstrably not silent, and conflating the two would double-penalise
    /// it — once for the bad packet and again as a missed heartbeat.
    /// </remarks>
    internal DateTimeOffset? LastSeen { get; set; }

    /// <summary>
    /// Whole heartbeat intervals already penalised since the node was last seen.
    /// </summary>
    /// <remarks>
    /// The reason a penalty is applied once per missed interval and not once per sweep. Reset to
    /// zero whenever the node reports.
    /// </remarks>
    internal long PenalisedIntervals { get; set; }

    internal double Score { get; set; }

    internal TrustBand Band { get; set; }

    internal string Reason { get; set; }

    internal DateTimeOffset EvaluatedAt { get; set; }

    internal int RecentSuccesses { get; set; }

    internal int RecentFailures { get; set; }

    /// <summary>The last evaluation at which the node was nominal. The start of a decay window.</summary>
    internal DateTimeOffset? LastNominalAt { get; set; }

    /// <summary>When the node most recently entered the fault band.</summary>
    internal DateTimeOffset? FaultEnteredAt { get; set; }

    internal NodeTrustSnapshot ToSnapshot() => new(
        MacAddress,
        Score,
        Band,
        Reason,
        TrackedSince,
        LastSeen,
        EvaluatedAt,
        RecentSuccesses,
        RecentFailures);
}
