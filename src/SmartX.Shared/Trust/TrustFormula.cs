namespace SmartX.Shared;

/// <summary>
/// The pure arithmetic of trust: the score, the band, the recency weight, and the urgency order.
/// </summary>
/// <remarks>
/// Kept free of state, clocks and configuration so the same formula can be reused wherever a score
/// is needed — the predictive engine and routing weights in later parts, and the client when it
/// orders bands — without dragging the engine that maintains per-node history along with it.
/// </remarks>
public static class TrustFormula
{
    /// <summary>The score of a node with no evidence either way.</summary>
    /// <remarks>Not configuration: with s = 0 and f = 0, (0 + 1) / (0 + 0 + 2) = 0.5.</remarks>
    public const double PriorScore = 0.5;

    /// <summary>The total weighted evidence below which a node counts as having none.</summary>
    /// <remarks>
    /// A numerical guard rather than a tunable, which is why it is a constant and not configuration:
    /// no operator choice makes the arithmetic more correct. Decay never reaches zero, but a double
    /// cannot tell 1 + 1e-17 from 1, so once <c>s</c> and <c>f</c> fall below roughly 1e-16 the score
    /// rounds to exactly 0.5 regardless of which of them was larger — and a faulted node would appear
    /// to recover with nothing having happened. Treating anything under 1e-9 as no evidence triggers
    /// about thirty half-lives after the last outcome, some twenty half-lives before that rounding can
    /// occur, while sitting far below the weight of anything recent: a single outcome from twenty
    /// half-lives ago still weighs about 1e-6.
    /// </remarks>
    public const double MinimumEvidence = 1e-9;

    /// <summary>Computes trust as <c>(s + 1) / (s + f + 2)</c>.</summary>
    /// <param name="successes">Weighted successful packets, <c>s</c>.</param>
    /// <param name="failures">Weighted failures, <c>f</c>.</param>
    /// <returns>A score strictly between 0 and 1.</returns>
    /// <remarks>
    /// <b>Why the +1 and +2 — Laplace smoothing.</b> The plain ratio <c>s / (s + f)</c> breaks at
    /// exactly the moment a node matters most, when it is new. With no packets it is 0 / 0, which is
    /// undefined. After one good packet it is 1 / 1 = 1.0, a claim of perfect reliability from a
    /// single observation; after one bad packet it is 0.0, total condemnation on the same evidence.
    /// <para>
    /// Adding one imaginary success and one imaginary failure — +1 on top, +2 underneath — fixes all
    /// three. A node with no history scores (0 + 1) / (0 + 2) = 0.5: not trusted, not condemned,
    /// simply unknown. One good packet moves it to 2 / 3 ≈ 0.67 rather than 1.0, one bad packet to
    /// 1 / 3 ≈ 0.33 rather than 0. As real evidence accumulates the imaginary observations are
    /// outweighed and the score converges on the true ratio, so the smoothing only matters while
    /// there is too little data to say anything confident.
    /// </para>
    /// <para>
    /// It also guarantees the score never reaches exactly 0 or 1, so no finite history can make a
    /// node permanently perfect or permanently beyond recovery.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Either input is negative or NaN.</exception>
    public static double Score(double successes, double failures)
    {
        if (!(successes >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(successes), successes, "Must be zero or more.");
        }

        if (!(failures >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(failures), failures, "Must be zero or more.");
        }

        return (successes + 1) / (successes + failures + 2);
    }

    /// <summary>Places a score in its band.</summary>
    /// <param name="score">The trust score.</param>
    /// <param name="nominalThreshold">Lowest score still counted as nominal.</param>
    /// <param name="faultThreshold">Lowest score not yet counted as a fault.</param>
    /// <remarks>
    /// Both boundaries are inclusive on the upper band. This judges a score and nothing else: whether
    /// there is any evidence behind the score is the caller's question, and a node with none is
    /// <see cref="TrustBand.Unproven"/> rather than whatever band 0.5 happens to fall in.
    /// </remarks>
    public static TrustBand Classify(double score, double nominalThreshold, double faultThreshold) =>
        score >= nominalThreshold ? TrustBand.Nominal
        : score >= faultThreshold ? TrustBand.Degrading
        : TrustBand.Fault;

    /// <summary>How urgently a band calls for attention. Lower sorts first.</summary>
    /// <remarks>
    /// Fault, then degrading, then unproven, then nominal. Unproven sits above nominal because an
    /// unverified node is worth a glance, and below degrading because nothing has actually gone
    /// wrong with it. Stated explicitly rather than taken from the enum's numeric values, so the
    /// dashboard's ordering is a decision written down in one place, not a side effect of the order
    /// the members happen to be declared in.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The band is not a declared member.</exception>
    public static int Severity(TrustBand band) => band switch
    {
        TrustBand.Fault => 0,
        TrustBand.Degrading => 1,
        TrustBand.Unproven => 2,
        TrustBand.Nominal => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Unknown trust band.")
    };

    /// <summary>How much an observation of a given age still counts, between 0 and 1.</summary>
    /// <param name="age">How long ago the observation was made.</param>
    /// <param name="halfLife">The age at which an observation counts half as much as a fresh one.</param>
    /// <remarks>
    /// Exponential decay, <c>0.5 ^ (age / halfLife)</c>. Every half-life an observation's influence
    /// halves, smoothly, with no cliff edge where evidence suddenly stops counting. A negative age —
    /// an observation stamped fractionally in the future by clock jitter — counts as fresh rather than
    /// being allowed to count for more than one.
    /// </remarks>
    public static double DecayWeight(TimeSpan age, TimeSpan halfLife)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(halfLife, TimeSpan.Zero);

        return age <= TimeSpan.Zero ? 1.0 : Math.Pow(0.5, age / halfLife);
    }
}
