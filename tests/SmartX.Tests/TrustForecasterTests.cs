using SmartX.Api.Patterns;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>The least-squares line, R², and when a falling trend reaches the fault threshold.</summary>
public class TrustForecasterTests
{
    private static readonly ForecastOptions Options = new();

    private const double Threshold = 0.5;

    /// <summary>Samples at a steady spacing on <c>start + slope × minutes</c>, plus a wobble of the given size that alternates sign.</summary>
    private static List<(DateTimeOffset At, double Score)> Series(
        int count, double start, double perMinute, double wobble = 0, int spacingSeconds = 10)
    {
        List<(DateTimeOffset, double)> samples = [];

        for (var index = 0; index < count; index++)
        {
            var minutes = index * spacingSeconds / 60.0;
            var noise = wobble * (index % 2 == 0 ? 1 : -1);

            samples.Add((T0 + TimeSpan.FromSeconds(index * spacingSeconds), start + perMinute * minutes + noise));
        }

        return samples;
    }

    // ---- the fit ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_perfect_line_is_recovered_exactly()
    {
        var fit = TrustForecaster.Fit(Series(12, start: 0.9, perMinute: -0.06)) ?? throw new InvalidOperationException();

        Assert.Equal(-0.06 / 60, fit.SlopePerSecond, precision: 10);
        Assert.Equal(0.9, fit.Intercept, precision: 10);
        Assert.Equal(1.0, fit.RSquared, precision: 10);
        Assert.Equal(12, fit.Samples);
        Assert.Equal(0.06, fit.FallPerMinute, precision: 10);
    }

    [Fact]
    public void The_slope_is_the_least_squares_slope_and_not_the_one_through_the_end_points()
    {
        // 0, 0, 3 against 0, 1, 2: the end points say 1.5 a step, the best line says 1.5 too, but the middle point moves the intercept.
        var samples = new List<(DateTimeOffset, double)> { (T0, 0), (T0.AddSeconds(1), 0), (T0.AddSeconds(2), 3) };

        var fit = TrustForecaster.Fit(samples) ?? throw new InvalidOperationException();

        Assert.Equal(1.5, fit.SlopePerSecond, precision: 10);
        Assert.Equal(-0.5, fit.Intercept, precision: 10);
        Assert.Equal(0.75, fit.RSquared, precision: 10);
    }

    [Fact]
    public void A_flat_series_has_a_zero_slope_and_no_fit_to_speak_of()
    {
        var fit = TrustForecaster.Fit(Series(12, start: 0.7, perMinute: 0)) ?? throw new InvalidOperationException();

        Assert.Equal(0, fit.SlopePerSecond, precision: 12);
        Assert.Equal(0, fit.RSquared);
    }

    [Fact]
    public void Fewer_than_two_samples_or_one_moment_cannot_be_fitted()
    {
        Assert.Null(TrustForecaster.Fit([]));
        Assert.Null(TrustForecaster.Fit([(T0, 0.7)]));
        Assert.Null(TrustForecaster.Fit([(T0, 0.7), (T0, 0.6)]));
    }

    [Fact]
    public void R_squared_is_between_zero_and_one()
    {
        var wild = Series(20, 0.7, -0.01, wobble: 0.2);

        var fit = TrustForecaster.Fit(wild) ?? throw new InvalidOperationException();

        Assert.InRange(fit.RSquared, 0, 1);
    }

    // ---- when it reaches the threshold ------------------------------------------------------------------------------

    [Fact]
    public void A_steady_fall_projects_the_right_time_to_fault()
    {
        // 0.70 falling 0.02 a minute, twelve samples ten seconds apart. The last is at 110 s, at 0.70 - 0.02 x 110 / 60.
        var samples = Series(12, start: 0.70, perMinute: -0.02);

        var forecast = TrustForecaster.Forecast(samples, Threshold, Options) ?? throw new InvalidOperationException("No forecast.");

        var scoreNow = 0.70 - 0.02 * 110 / 60;
        var expected = TimeSpan.FromMinutes((scoreNow - Threshold) / 0.02);

        Assert.Equal(expected.TotalSeconds, forecast.TimeToFault.TotalSeconds, precision: 3);
        Assert.Equal(samples[^1].At + expected, forecast.ProjectedFaultAt);
        Assert.Equal(scoreNow, forecast.Score, precision: 6);
        Assert.InRange(forecast.TimeToFault.TotalMinutes, 8, 8.5);
    }

    [Fact]
    public void A_steady_fall_with_a_little_noise_is_still_forecast_close_to_right()
    {
        var forecast = TrustForecaster.Forecast(Series(18, start: 0.70, perMinute: -0.03, wobble: 0.004), Threshold, Options);

        Assert.NotNull(forecast);
        Assert.InRange(forecast.Value.Fit.RSquared, 0.8, 1);

        // Truth is (0.70 - 0.03 x 170/60 - 0.5) / 0.03 minutes.
        var truth = (0.70 - 0.03 * 170 / 60 - 0.5) / 0.03;

        Assert.InRange(forecast.Value.TimeToFault.TotalMinutes, truth - 0.7, truth + 0.7);
    }

    [Fact]
    public void A_noisy_flat_series_raises_nothing()
    {
        // Around 0.62 with a swing of 0.04 either side, going nowhere: the line fits badly and the slope is nothing.
        Assert.Null(TrustForecaster.Forecast(Series(24, start: 0.62, perMinute: 0, wobble: 0.04), Threshold, Options));
    }

    [Fact]
    public void A_noisy_series_whose_chance_slope_is_downward_raises_nothing_because_the_fit_is_poor()
    {
        // Pseudo-random scatter with a faint downward lean: a line can be drawn, and it explains little of the scatter.
        var random = new Random(3);
        List<(DateTimeOffset, double)> samples = [];

        for (var index = 0; index < 30; index++)
        {
            samples.Add((T0 + TimeSpan.FromSeconds(index * 10), 0.65 - 0.0004 * index + (random.NextDouble() - 0.5) * 0.12));
        }

        var fit = TrustForecaster.Fit(samples) ?? throw new InvalidOperationException();

        Assert.True(fit.RSquared < 0.8, $"R² was {fit.RSquared}.");
        Assert.Null(TrustForecaster.Forecast(samples, Threshold, Options));
    }

    [Fact]
    public void Too_few_samples_raise_nothing_however_good_the_line()
    {
        Assert.Null(TrustForecaster.Forecast(Series(Options.MinSamples - 1, start: 0.7, perMinute: -0.05), Threshold, Options));
        Assert.NotNull(TrustForecaster.Forecast(Series(Options.MinSamples, start: 0.7, perMinute: -0.05), Threshold, Options));
    }

    [Fact]
    public void A_rising_trend_raises_nothing()
    {
        Assert.Null(TrustForecaster.Forecast(Series(15, start: 0.55, perMinute: 0.05), Threshold, Options));
    }

    [Fact]
    public void A_fall_too_slow_to_matter_raises_nothing_even_when_the_fit_is_perfect()
    {
        Assert.Null(TrustForecaster.Forecast(Series(15, start: 0.7, perMinute: -0.001), Threshold, Options));
    }

    [Fact]
    public void A_fall_that_would_take_longer_than_the_horizon_raises_nothing()
    {
        // 0.007 a minute from 0.7 reaches 0.5 in about 28 minutes, past the default fifteen.
        Assert.Null(TrustForecaster.Forecast(Series(15, start: 0.7, perMinute: -0.007), Threshold, Options));

        Assert.NotNull(TrustForecaster.Forecast(Series(15, start: 0.7, perMinute: -0.007), Threshold, new ForecastOptions { Horizon = TimeSpan.FromMinutes(40) }));
    }

    [Fact]
    public void A_line_already_at_the_threshold_is_not_a_forecast()
    {
        Assert.Null(TrustForecaster.Forecast(Series(12, start: 0.55, perMinute: -0.2), Threshold, Options));
    }

    [Fact]
    public void A_forecast_raised_at_the_full_bars_survives_looser_ones_and_a_fresh_one_does_not()
    {
        // R² of about 0.75: under the 0.8 a forecast is raised at, over the 0.4 it is withdrawn at.
        var samples = Series(20, start: 0.7, perMinute: -0.03, wobble: 0.02);
        var fit = TrustForecaster.Fit(samples) ?? throw new InvalidOperationException();
        var span = samples[^1].At - samples[0].At;

        Assert.InRange(fit.RSquared, 0.4, 0.8);
        Assert.Null(TrustForecaster.Project(fit, span, samples[^1].At, Threshold, 0.8, 0.005, TimeSpan.FromMinutes(15)));
        Assert.NotNull(TrustForecaster.Project(fit, span, samples[^1].At, Threshold, 0.4, 0.0025, TimeSpan.FromMinutes(20)));
    }

    /// <summary>A fall whose rate changes: the first part of the window falls at <paramref name="early"/> a minute and the rest at <paramref name="late"/>.</summary>
    private static List<(DateTimeOffset, double)> Bent(double early, double late, int count = 24)
    {
        List<(DateTimeOffset, double)> samples = [];
        var score = 0.9;

        for (var index = 0; index < count; index++)
        {
            samples.Add((T0 + TimeSpan.FromSeconds(index * 10), score));
            score += (index < count / 2 ? early : late) * 10 / 60;
        }

        return samples;
    }

    [Fact]
    public void A_fall_that_is_flattening_is_not_forecast_because_a_line_would_overshoot_it()
    {
        // Fast at first and then nearly flat: the whole-window line still slopes down and fits tolerably.
        var samples = Bent(early: -0.12, late: -0.01);

        Assert.Null(TrustForecaster.Forecast(samples, 0.5, Options));
        Assert.True(TrustForecaster.Fit(samples)!.Value.RecentFallPerMinute < TrustForecaster.Fit(samples)!.Value.FallPerMinute * 0.5);
    }

    [Fact]
    public void A_fall_that_is_speeding_up_is_forecast()
    {
        var samples = Bent(early: -0.03, late: -0.12);

        var forecast = TrustForecaster.Forecast(samples, 0.5, Options);

        Assert.NotNull(forecast);
        Assert.True(TrustForecaster.Fit(samples)!.Value.RecentFallPerMinute > TrustForecaster.Fit(samples)!.Value.FallPerMinute);
    }

    [Fact]
    public void The_recent_rate_is_not_known_from_a_handful_of_samples_and_does_not_block_a_forecast()
    {
        var samples = Series(Options.MinSamples, start: 0.7, perMinute: -0.1);

        // Eight samples have a recent third of three, so the rate is known; five do not.
        Assert.True(double.IsNaN(TrustForecaster.Fit(samples.Take(5).ToList())!.Value.RecentFallPerMinute));
        Assert.NotNull(TrustForecaster.Forecast(samples, 0.5, Options));
    }

    [Fact]
    public void The_options_are_validated()
    {
        Assert.Empty(ForecastOptionsValidation.Problems(new ForecastOptions()));
        Assert.NotEmpty(ForecastOptionsValidation.Problems(new ForecastOptions { MinSamples = 2 }));
        Assert.NotEmpty(ForecastOptionsValidation.Problems(new ForecastOptions { MinRSquared = 0 }));
        Assert.NotEmpty(ForecastOptionsValidation.Problems(new ForecastOptions { Horizon = TimeSpan.Zero }));
        Assert.NotEmpty(ForecastOptionsValidation.Problems(new ForecastOptions { MinFallPerMinute = 0 }));
    }
}
