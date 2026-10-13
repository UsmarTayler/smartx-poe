using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartX.Api.History;
using SmartX.Api.Patterns;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>The devices to look at before anyone searches: what is already wrong, and what is projected to go wrong.</summary>
public class InsightServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new(T0);
    private readonly TrustEngine _trust;
    private readonly MeshTestHost _mesh;
    private readonly InvestigationService _investigations;
    private readonly TrustSampler _sampler;
    private readonly InsightService _insights;
    private int _changes;

    public InsightServiceTests() : this(new ForecastOptions())
    {
    }

    private InsightServiceTests(ForecastOptions forecast)
    {
        _trust = new TrustEngine(Options.Create(new TrustOptions()), _clock, NullLogger<TrustEngine>.Instance);
        _mesh = MeshTestHost.Create(_clock);

        var directory = Directory();

        _investigations = new InvestigationService(
            _trust, _mesh.Alerts, _mesh.History, new RecordingEngine(), directory, _clock, Options.Create(new InvestigationOptions()));

        _sampler = new TrustSampler(_trust, _mesh.History, _mesh.Sequence, Options.Create(forecast), _clock);

        _insights = new InsightService(
            _trust,
            _mesh.History,
            _mesh.Alerts,
            _investigations,
            directory,
            Options.Create(forecast),
            Options.Create(new TrustOptions()),
            _clock,
            NullLogger<InsightService>.Instance);

        _insights.Changed += () => Interlocked.Increment(ref _changes);
    }

    public Task InitializeAsync() => _investigations.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _investigations.StopAsync(CancellationToken.None);
        _sampler.Dispose();
        _insights.Dispose();
    }

    private void Record(TrustOutcome outcome, string mac = MoistB)
    {
        _trust.Record(mac, outcome);
        _clock.Advance(Step);
    }

    private void Healthy(int steps = 60, string mac = MoistB)
    {
        for (var index = 0; index < steps; index++)
        {
            Record(TrustOutcome.Success, mac);
        }
    }

    /// <summary>Fails until the node is in fault, running a pass after each step. Returns how many steps it took.</summary>
    private int FailUntilFault(string mac = MoistB, Action? afterEach = null)
    {
        for (var index = 0; index < 400; index++)
        {
            Record(TrustOutcome.RangeViolation, mac);
            _insights.Pass();
            afterEach?.Invoke();

            if (Band(mac) == TrustBand.Fault)
            {
                return index + 1;
            }
        }

        throw new InvalidOperationException("The node never faulted.");
    }

    private TrustBand Band(string mac)
    {
        Assert.True(_trust.TryGet(mac, out var snapshot));

        return snapshot.Band;
    }

    private Insight? Anticipated(string mac = MoistB) =>
        _insights.Current.SingleOrDefault(insight => insight.Kind == InsightKind.Anticipated && insight.MacAddress == mac);

    private Insight? Current(string mac = MoistB) =>
        _insights.Current.SingleOrDefault(insight => insight.Kind == InsightKind.Current && insight.MacAddress == mac);

    // ---- the samples ------------------------------------------------------------------------------------------------

    [Fact]
    public void Trust_is_stored_in_the_history_store_one_sample_every_interval()
    {
        Healthy(steps: 24);

        var samples = _mesh.History.GetRange(MoistB, T0, _clock.GetUtcNow().AddTicks(1)).Where(entry => entry.Kind == HistoryKind.TrustSample).ToList();

        // 24 evaluations five seconds apart, 120 s, sampled every ten seconds.
        Assert.InRange(samples.Count, 11, 13);
        Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.True(pair.Second.Timestamp - pair.First.Timestamp >= TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void A_trust_sample_is_a_measurement_and_not_an_event()
    {
        Healthy(steps: 10);

        Assert.DoesNotContain(_mesh.History.GetEvents(T0, _clock.GetUtcNow().AddTicks(1)), entry => entry.Kind == HistoryKind.TrustSample);
    }

    // ---- anticipating -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_node_whose_trust_is_falling_appears_in_the_insights_before_it_reaches_fault()
    {
        Healthy();
        var seenWhileNotFaulted = false;

        FailUntilFault(afterEach: () =>
        {
            if (Band(MoistB) != TrustBand.Fault && Anticipated() is not null)
            {
                seenWhileNotFaulted = true;
            }
        });

        Assert.True(seenWhileNotFaulted, "No anticipated insight appeared before the node faulted.");
    }

    [Fact]
    public void The_insight_names_the_device_and_says_when()
    {
        Healthy();
        Insight? first = null;

        FailUntilFault(afterEach: () => first ??= Anticipated());

        Assert.NotNull(first);
        Assert.Equal("Moisture B humidity sensor: trust falling", first.Headline);
        Assert.Matches(@"^Projected to reach Fault in (about \d+ minutes?|under a minute)\.$", first.Detail);
        Assert.NotNull(first.ProjectedFaultAt);
        Assert.NotNull(first.FitQuality);
        Assert.InRange(first.FitQuality.Value, 0.8, 1);
        Assert.Equal(InsightKind.Anticipated, first.Kind);
        Assert.Equal(3, first.Reasons.Count);
        Assert.Contains("R²", first.Reasons[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_projected_moment_is_near_the_moment_it_actually_faulted()
    {
        Healthy();
        DateTimeOffset? projected = null;

        FailUntilFault(afterEach: () => projected ??= Anticipated()?.ProjectedFaultAt);

        var actual = _clock.GetUtcNow();

        Assert.NotNull(projected);
        Assert.InRange((projected.Value - actual).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void A_node_already_in_fault_is_current_and_not_forecast()
    {
        Healthy();
        FailUntilFault();

        Assert.Null(Anticipated());
        Assert.NotNull(Current());
        Assert.Contains("in Fault", Current()!.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void An_insight_is_withdrawn_when_the_trend_reverses()
    {
        Healthy();

        // Fall until the insight is up, and not yet faulted.
        for (var index = 0; index < 400 && Anticipated() is null; index++)
        {
            Record(TrustOutcome.RangeViolation);
            _insights.Pass();
        }

        Assert.NotNull(Anticipated());

        // Then recover.
        for (var index = 0; index < 200 && Anticipated() is not null; index++)
        {
            Record(TrustOutcome.Success);
            _insights.Pass();
        }

        Assert.Null(Anticipated());
    }

    [Fact]
    public void A_node_that_is_doing_fine_raises_nothing()
    {
        for (var index = 0; index < 120; index++)
        {
            Record(TrustOutcome.Success);
            _insights.Pass();

            Assert.Null(Anticipated());
        }
    }

    [Fact]
    public void A_fall_that_levels_off_before_it_gets_anywhere_is_not_forecast_for_long()
    {
        // A failure every fourth reading after a healthy run: trust drops, then settles near 0.75 and never nears the fault band.
        Healthy(steps: 20);
        var raised = 0;

        for (var index = 0; index < 240; index++)
        {
            Record(index % 4 == 0 ? TrustOutcome.RangeViolation : TrustOutcome.Success);
            _insights.Pass();

            if (Anticipated() is not null)
            {
                raised++;
            }
        }

        // A few passes while it is still dropping, and none once it is settling. Not the ten minutes a straight line would hold it for.
        Assert.True(raised < 20, $"The insight was up for {raised} passes.");
        Assert.Null(Anticipated());
    }

    [Fact]
    public void Nothing_is_forecast_from_too_few_samples()
    {
        Healthy(steps: 4);

        // Failing for 25 seconds leaves two or three samples, and a line through them is a coincidence.
        for (var index = 0; index < 5; index++)
        {
            Record(TrustOutcome.RangeViolation);
            _insights.Pass();
        }

        Assert.Null(Anticipated());
    }

    [Fact]
    public void A_node_with_no_evidence_yet_is_not_forecast()
    {
        _trust.Track(MoistC);

        for (var index = 0; index < 20; index++)
        {
            _clock.Advance(Step);
            _insights.Pass();
        }

        Assert.Null(Anticipated(MoistC));
    }

    [Fact]
    public void Announcing_happens_when_an_insight_appears_and_when_it_goes()
    {
        Healthy();
        var before = _changes;

        for (var index = 0; index < 400 && Anticipated() is null; index++)
        {
            Record(TrustOutcome.RangeViolation);
            _insights.Pass();
        }

        Assert.True(_changes > before);

        before = _changes;

        for (var index = 0; index < 200 && Anticipated() is not null; index++)
        {
            Record(TrustOutcome.Success);
            _insights.Pass();
        }

        Assert.True(_changes > before);
    }

    [Fact]
    public void A_projected_time_that_drifts_a_little_is_not_announced_again()
    {
        Healthy();

        for (var index = 0; index < 400 && Anticipated() is null; index++)
        {
            Record(TrustOutcome.RangeViolation);
            _insights.Pass();
        }

        var held = Anticipated()!;
        var before = _changes;

        // One more failure moves the projection by a few seconds, which is inside the threshold.
        Record(TrustOutcome.RangeViolation);
        _insights.Pass();

        Assert.Equal(held.ProjectedFaultAt, Anticipated()!.ProjectedFaultAt);
        Assert.Equal(before, _changes);
    }

    // ---- what is wrong now ------------------------------------------------------------------------------------------

    [Fact]
    public void A_device_with_an_active_alert_is_listed_as_current()
    {
        _mesh.Alerts.Raise(new AlertKey(MoistC, AlertKind.OutOfRange), "out");
        _insights.Pass();

        var insight = Current(MoistC) ?? throw new InvalidOperationException();

        Assert.Equal("Moisture C humidity sensor: 1 alert", insight.Headline);
        Assert.Contains("out-of-range alert", insight.Detail, StringComparison.Ordinal);
        Assert.Equal(0.7, insight.Severity, precision: 6);
    }

    [Fact]
    public void A_device_in_fault_with_an_open_investigation_is_one_insight_that_says_both()
    {
        Healthy();
        FailUntilFault();

        var insight = Current() ?? throw new InvalidOperationException();

        Assert.Equal(TrustBand.Fault, insight.Band);
        Assert.Equal("Moisture B humidity sensor: in Fault, investigation open", insight.Headline);
        Assert.Equal(1.0, insight.Severity);
        Assert.NotNull(insight.InvestigationId);
        Assert.Equal(_investigations.Open().Single().Id, insight.InvestigationId);
    }

    [Fact]
    public void Current_problems_are_ranked_by_severity()
    {
        Healthy();
        FailUntilFault();
        _mesh.Alerts.Raise(new AlertKey(MoistD, AlertKind.TypeViolation), "type");
        _mesh.Alerts.Raise(new AlertKey(MoistC, AlertKind.Disconnected), "gone");
        _insights.Pass();

        var current = _insights.Current.Where(insight => insight.Kind == InsightKind.Current).Select(insight => insight.MacAddress).ToList();

        Assert.Equal([MoistB, MoistC, MoistD], current);
    }

    [Fact]
    public void An_acknowledged_alert_counts_for_less()
    {
        var key = new AlertKey(MoistC, AlertKind.OutOfRange);
        _mesh.Alerts.Raise(key, "out");
        _insights.Pass();
        var unacknowledged = Current(MoistC)!.Severity;

        _mesh.Alerts.Acknowledge(key);
        _insights.Pass();

        Assert.True(Current(MoistC)!.Severity < unacknowledged);
    }

    [Fact]
    public void A_cleared_alert_is_no_longer_listed()
    {
        var key = new AlertKey(MoistC, AlertKind.OutOfRange);
        _mesh.Alerts.Raise(key, "out");
        _insights.Pass();
        Assert.NotNull(Current(MoistC));

        _mesh.Alerts.Clear(key);
        _insights.Pass();

        Assert.Null(Current(MoistC));
    }

    [Fact]
    public void A_device_that_recovers_leaves_the_list()
    {
        Healthy();
        FailUntilFault();

        for (var index = 0; index < 400 && Band(MoistB) == TrustBand.Fault; index++)
        {
            Record(TrustOutcome.Success);
        }

        _insights.Pass();

        // The investigation closed itself when the fault cleared.
        Assert.Null(Current());
    }

    [Fact]
    public void Anticipated_come_before_current_in_the_list()
    {
        _mesh.Alerts.Raise(new AlertKey(MoistC, AlertKind.OutOfRange), "out");
        Healthy();

        for (var index = 0; index < 400 && Anticipated() is null; index++)
        {
            Record(TrustOutcome.RangeViolation);
            _insights.Pass();
        }

        Assert.Equal(InsightKind.Anticipated, _insights.Current[0].Kind);
        Assert.Equal(InsightKind.Current, _insights.Current[^1].Kind);
    }

    [Fact]
    public async Task Disabling_the_forecast_leaves_the_current_problems_and_nothing_anticipated()
    {
        var off = new InsightServiceTests(new ForecastOptions { Enabled = false });
        await off.InitializeAsync();

        off.Healthy();

        for (var index = 0; index < 100; index++)
        {
            off.Record(TrustOutcome.RangeViolation);
            off._insights.Pass();
        }

        Assert.DoesNotContain(off._insights.Current, insight => insight.Kind == InsightKind.Anticipated);
        Assert.Contains(off._insights.Current, insight => insight.Kind == InsightKind.Current);

        await off.DisposeAsync();
    }
}
