using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>Settings for the trust forecast: how far ahead, from how much, and how sure it has to be.</summary>
/// <remarks>Bound from the "Forecast" section. The forecast is a line fitted to a few minutes of trust samples; these are what stop it crying wolf.</remarks>
public sealed class ForecastOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Forecast";

    /// <summary>Whether the forecast runs at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far ahead a projected fault is worth announcing.</summary>
    /// <remarks>
    /// Fifteen minutes: far enough that an operator can walk to the device, near enough that a straight line is a fair guess
    /// at what a node's trust will do. Past that, a trend is a hope.
    /// </remarks>
    public TimeSpan Horizon { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How much recent history the line is fitted to.</summary>
    /// <remarks>
    /// Four minutes of samples. Shorter and one bad reading is the whole trend; longer and a node that has just started to
    /// fall is averaged with the time it was fine, and the slope is too shallow to alarm.
    /// </remarks>
    public TimeSpan Lookback { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>The fewest samples a line is fitted to.</summary>
    /// <remarks>
    /// Eight. A line through two points is perfect and means nothing, and through four it is a guess that fits by luck.
    /// Eight samples at the default ten-second spacing is eighty seconds of evidence, which a trend has to survive.
    /// </remarks>
    public int MinSamples { get; set; } = 8;

    /// <summary>
    /// How much of the samples' movement the line must explain (R², from 0 to 1) before the forecast is believed.
    /// </summary>
    /// <remarks>
    /// <b>0.8.</b> R² is the share of the variation in trust that is accounted for by the straight line: 1 is every
    /// sample on it, 0 is a line no better than the average. A noisy series that wanders up and down fits a line badly and
    /// scores low, so it raises no alarm; a steady fall fits well and scores near 1. Eight tenths means the line describes
    /// most of what the node has been doing, and is the bar between "falling" and "jittery".
    /// </remarks>
    public double MinRSquared { get; set; } = 0.8;

    /// <summary>How steep the fall must be, in trust per minute, to count as falling at all.</summary>
    /// <remarks>
    /// A fit can be perfect and still say nothing: a node losing a thousandth of a point an hour is on a line, and it is
    /// not going anywhere. This is the slope below which a trend is too slow to matter within the horizon.
    /// </remarks>
    public double MinFallPerMinute { get; set; } = 0.005;

    /// <summary>
    /// How much of the whole window's rate of fall the most recent third of it must still show, for the fall to count as continuing.
    /// </summary>
    /// <remarks>
    /// <b>Half.</b> A node's trust does not fall in a straight line. After a burst of failures it drops fast and then levels off
    /// as the failures age and the score settles, so a line fitted to the drop keeps projecting a fall that has stopped. Comparing
    /// the fall over the last third of the window with the fall over all of it catches that: a fall that is steady or speeding up
    /// has a recent rate at or above the whole, and one that is flattening has a recent rate well below it. Below this share,
    /// there is no forecast, and one already raised is withdrawn.
    /// </remarks>
    public double MinRecentFallRatio { get; set; } = 0.5;

    /// <summary>How often a trust score is stored for a node.</summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the forecast runs over every node.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How far a projected time can move before it is announced again.</summary>
    /// <remarks>The projection is recomputed every pass and drifts with every sample. A countdown that is re-sent every five seconds is noise.</remarks>
    public TimeSpan EtaChange { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>The most current problems listed. The most severe are kept.</summary>
    public int MaxCurrentInsights { get; set; } = 25;

    /// <summary>The most anticipated problems listed. The soonest are kept.</summary>
    public int MaxAnticipatedInsights { get; set; } = 25;
}

/// <summary>Checks the forecast options at startup.</summary>
internal static class ForecastOptionsValidation
{
    public static IEnumerable<string> Problems(ForecastOptions options)
    {
        if (options.Horizon <= TimeSpan.Zero || options.Lookback <= TimeSpan.Zero || options.SampleInterval <= TimeSpan.Zero || options.Interval <= TimeSpan.Zero)
        {
            yield return "Forecast:Horizon, Lookback, SampleInterval and Interval must be positive.";
        }

        if (options.MinSamples < 3)
        {
            yield return "Forecast:MinSamples must be at least 3: a line through fewer says nothing.";
        }

        if (options.MinRSquared is <= 0 or > 1)
        {
            yield return "Forecast:MinRSquared must be above 0 and at most 1.";
        }

        if (options.MinFallPerMinute <= 0)
        {
            yield return "Forecast:MinFallPerMinute must be positive.";
        }

        if (options.MinRecentFallRatio is < 0 or > 1)
        {
            yield return "Forecast:MinRecentFallRatio must be between 0 and 1.";
        }

        if (options.EtaChange < TimeSpan.Zero || options.MaxCurrentInsights < 1 || options.MaxAnticipatedInsights < 1)
        {
            yield return "Forecast:EtaChange cannot be negative and the insight limits must be at least 1.";
        }
    }
}

/// <summary>A straight line fitted to a series of trust samples.</summary>
/// <param name="SlopePerSecond">How trust changes per second along the line. Negative when it is falling.</param>
/// <param name="Intercept">The line's value at the first sample.</param>
/// <param name="RSquared">The share of the samples' variation the line explains, from 0 to 1.</param>
/// <param name="Samples">How many samples it was fitted to.</param>
/// <param name="Span">How long the samples cover.</param>
/// <param name="RecentFallPerMinute">
/// How steeply trust fell over the most recent third of the samples, per minute, as a positive number for a fall. Not a number
/// when there were too few samples to say. Compared with the whole window's to see whether the fall is flattening.
/// </param>
internal readonly record struct TrendFit(
    double SlopePerSecond,
    double Intercept,
    double RSquared,
    int Samples,
    TimeSpan Span,
    double RecentFallPerMinute = double.NaN)
{
    /// <summary>The line's value at a moment, counted from the first sample.</summary>
    public double At(TimeSpan sinceFirst) => Intercept + SlopePerSecond * sinceFirst.TotalSeconds;

    /// <summary>How steeply trust is falling, per minute, as a positive number. Zero or less when it is not.</summary>
    public double FallPerMinute => -SlopePerSecond * 60;
}

/// <summary>Where a falling trend is heading.</summary>
/// <param name="Fit">The line.</param>
/// <param name="Score">The line's value at the latest sample: where the node is, with the noise taken out.</param>
/// <param name="TimeToFault">How long until the line reaches the fault threshold.</param>
/// <param name="ProjectedFaultAt">When that is.</param>
internal readonly record struct TrendForecast(TrendFit Fit, double Score, TimeSpan TimeToFault, DateTimeOffset ProjectedFaultAt);

/// <summary>
/// Fits a line to a node's recent trust samples and says whether, and when, it will reach the fault threshold.
/// </summary>
/// <remarks>
/// <b>Ordinary least squares.</b> The slope that minimises the squared distance from the line to the samples is
/// <c>Sxy / Sxx</c>, where <c>Sxx</c> is the sum of squared distances of the times from their mean and <c>Sxy</c> the sum of the
/// products of the time and trust distances from theirs. Two passes over the samples and no matrix. R² is
/// <c>Sxy² / (Sxx · Syy)</c>, the square of the correlation between time and trust: the share of the samples' movement that is
/// movement along the line.
/// <para>
/// <b>Why a forecast is only made when it can be trusted.</b> A straight line can be fitted to anything, and the answer is
/// always a number. Three things are required before the number is believed. There must be enough samples (a line through a
/// few points is a coincidence). The line must fit (R² at or above a minimum, so a noisy series that has no trend raises no
/// alarm). And the fall must be steep enough to reach the threshold within the horizon, which a fit that is perfect but
/// nearly flat does not.
/// </para>
/// <para>
/// A pure function of the samples and the options, with no clock and no state, so it is tested against series with a known
/// answer.
/// </para>
/// </remarks>
internal static class TrustForecaster
{
    /// <summary>Fits a line to samples taken at known moments. Oldest first.</summary>
    /// <returns>The fit, or null when there are fewer than two samples or all of them are at one moment.</returns>
    public static TrendFit? Fit(IReadOnlyList<(DateTimeOffset At, double Score)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (FitLine(samples) is not { } whole)
        {
            return null;
        }

        // The most recent third, with at least three samples, so there is a line to speak of.
        var third = Math.Max(3, samples.Count / 3);

        return samples.Count >= 6 && FitLine([.. samples.Skip(samples.Count - third)]) is { } recent
            ? whole with { RecentFallPerMinute = recent.FallPerMinute }
            : whole;
    }

    private static TrendFit? FitLine(IReadOnlyList<(DateTimeOffset At, double Score)> samples)
    {
        if (samples.Count < 2)
        {
            return null;
        }

        var origin = samples[0].At;
        double sumX = 0, sumY = 0;

        foreach (var (at, score) in samples)
        {
            sumX += (at - origin).TotalSeconds;
            sumY += score;
        }

        var meanX = sumX / samples.Count;
        var meanY = sumY / samples.Count;
        double sxx = 0, sxy = 0, syy = 0;

        foreach (var (at, score) in samples)
        {
            var dx = (at - origin).TotalSeconds - meanX;
            var dy = score - meanY;

            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        if (sxx <= 0)
        {
            return null;
        }

        var slope = sxy / sxx;
        var intercept = meanY - slope * meanX;

        // A series that does not move has no variation to explain. It is a flat line and not a perfect fit.
        var rSquared = syy <= 0 ? 0 : Math.Clamp(sxy * sxy / (sxx * syy), 0, 1);

        return new TrendFit(slope, intercept, rSquared, samples.Count, samples[^1].At - origin);
    }

    /// <summary>Projects a fit to the moment it reaches the fault threshold, if it is going to and the fit is worth believing.</summary>
    /// <param name="samples">The samples, oldest first.</param>
    /// <param name="faultThreshold">The score below which a node is in Fault.</param>
    /// <param name="options">What a forecast has to satisfy.</param>
    /// <returns>The forecast, or null when there is nothing worth announcing.</returns>
    public static TrendForecast? Forecast(IReadOnlyList<(DateTimeOffset At, double Score)> samples, double faultThreshold, ForecastOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (samples.Count < options.MinSamples || Fit(samples) is not { } fit)
        {
            return null;
        }

        return Project(fit, samples[^1].At - samples[0].At, samples[^1].At, faultThreshold, options.MinRSquared, options.MinFallPerMinute, options.Horizon, options.MinRecentFallRatio);
    }

    /// <summary>
    /// Projects a fit with the given bars. Separate from <see cref="Forecast"/> so a forecast already raised can be held to looser
    /// bars than it was raised at, which is what keeps it from flapping.
    /// </summary>
    public static TrendForecast? Project(
        TrendFit fit,
        TimeSpan sinceFirst,
        DateTimeOffset latest,
        double faultThreshold,
        double minimumRSquared,
        double minimumFallPerMinute,
        TimeSpan horizon,
        double minimumRecentFallRatio = 0)
    {
        if (fit.RSquared < minimumRSquared || fit.FallPerMinute < minimumFallPerMinute)
        {
            return null;
        }

        // A fall that is flattening is a node settling, and a line through it overshoots. Unknown (too few samples) is let through.
        if (!double.IsNaN(fit.RecentFallPerMinute) && fit.RecentFallPerMinute < minimumRecentFallRatio * fit.FallPerMinute)
        {
            return null;
        }

        // Where the line says the node is now, not where the last noisy sample happened to land.
        var score = fit.At(sinceFirst);

        // A line already at or under the threshold is a node that is about to be in Fault, or is: it is not a forecast.
        if (score <= faultThreshold)
        {
            return null;
        }

        var remaining = TimeSpan.FromSeconds((score - faultThreshold) / -fit.SlopePerSecond);

        return remaining <= horizon ? new TrendForecast(fit, score, remaining, latest + remaining) : null;
    }
}
