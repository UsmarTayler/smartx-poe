using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>Covers the pure formula, independent of any engine state.</summary>
public class TrustFormulaTests
{
    [Fact]
    public void No_evidence_scores_exactly_one_half()
    {
        // Laplace smoothing: (0 + 1) / (0 + 0 + 2). Undefined without the smoothing, not 0 or 1.
        Assert.Equal(0.5, TrustFormula.Score(0, 0));
        Assert.Equal(TrustFormula.PriorScore, TrustFormula.Score(0, 0));
    }

    [Fact]
    public void A_single_observation_moves_the_score_but_does_not_decide_it()
    {
        // The unsmoothed ratio would jump straight to 1.0 or 0.0 on one packet.
        Assert.Equal(2.0 / 3.0, TrustFormula.Score(1, 0), 12);
        Assert.Equal(1.0 / 3.0, TrustFormula.Score(0, 1), 12);
    }

    [Fact]
    public void With_plenty_of_evidence_the_score_converges_on_the_true_ratio()
    {
        Assert.Equal(0.9, TrustFormula.Score(9000, 1000), 3);
    }

    [Fact]
    public void No_finite_history_reaches_exactly_zero_or_one()
    {
        Assert.True(TrustFormula.Score(1_000_000, 0) < 1.0);
        Assert.True(TrustFormula.Score(0, 1_000_000) > 0.0);
    }

    [Theory]
    [InlineData(0.90, TrustBand.Nominal)]
    [InlineData(0.75, TrustBand.Nominal)]
    [InlineData(0.7499, TrustBand.Degrading)]
    [InlineData(0.50, TrustBand.Degrading)]
    [InlineData(0.4999, TrustBand.Fault)]
    [InlineData(0.05, TrustBand.Fault)]
    public void Bands_are_inclusive_at_their_lower_threshold(double score, TrustBand expected)
    {
        Assert.Equal(expected, TrustFormula.Classify(score, nominalThreshold: 0.75, faultThreshold: 0.50));
    }

    [Fact]
    public void Decay_halves_an_observation_every_half_life()
    {
        var halfLife = TimeSpan.FromMinutes(10);

        Assert.Equal(1.0, TrustFormula.DecayWeight(TimeSpan.Zero, halfLife));
        Assert.Equal(0.5, TrustFormula.DecayWeight(TimeSpan.FromMinutes(10), halfLife), 12);
        Assert.Equal(0.25, TrustFormula.DecayWeight(TimeSpan.FromMinutes(20), halfLife), 12);
        Assert.Equal(1.0 / 64, TrustFormula.DecayWeight(TimeSpan.FromHours(1), halfLife), 12);
    }

    [Fact]
    public void An_observation_stamped_in_the_future_counts_as_fresh_not_extra()
    {
        Assert.Equal(1.0, TrustFormula.DecayWeight(TimeSpan.FromSeconds(-5), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Negative_evidence_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrustFormula.Score(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrustFormula.Score(0, double.NaN));
    }
}
