using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartX.Api.History;
using SmartX.Api.Patterns;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>
/// Closing the loop from the Part 1 research: a node falling into fault opens an investigation, and the operator's verdict
/// feeds back into detection, within bounds.
/// </summary>
public class InvestigationTests : IAsyncLifetime
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new(T0);
    private readonly TrustEngine _trust;
    private readonly MeshTestHost _mesh;
    private readonly RecordingEngine _recorded = new();
    private readonly InvestigationOptions _options = new();
    private readonly InvestigationService _service;

    public InvestigationTests()
    {
        _trust = new TrustEngine(Options.Create(new TrustOptions()), _clock, NullLogger<TrustEngine>.Instance);
        _mesh = MeshTestHost.Create(_clock);
        _service = Service(_recorded);
    }

    private InvestigationService Service(IPatternEngine engine) =>
        new(_trust, _mesh.Alerts, _mesh.History, engine, Directory(), _clock, Options.Create(_options));

    public Task InitializeAsync() => _service.StartAsync(CancellationToken.None);

    public Task DisposeAsync() => _service.StopAsync(CancellationToken.None);

    private void Step_(TrustOutcome outcome, string mac = MoistB)
    {
        _trust.Record(mac, outcome);
        _clock.Advance(Step);
    }

    /// <summary>A node that is nominal for a while and then fails until it is in fault. Returns the moment it was last nominal.</summary>
    private DateTimeOffset Fault(string mac = MoistB)
    {
        for (var index = 0; index < 6; index++)
        {
            Step_(TrustOutcome.Success, mac);
        }

        var lastNominal = _clock.GetUtcNow() - Step;

        for (var index = 0; index < 200 && Band(mac) != TrustBand.Fault; index++)
        {
            Step_(TrustOutcome.RangeViolation, mac);
        }

        Assert.Equal(TrustBand.Fault, Band(mac));

        return lastNominal;
    }

    private void Recover(string mac = MoistB)
    {
        for (var index = 0; index < 400 && Band(mac) == TrustBand.Fault; index++)
        {
            Step_(TrustOutcome.Success, mac);
        }

        Assert.NotEqual(TrustBand.Fault, Band(mac));
    }

    private TrustBand Band(string mac)
    {
        Assert.True(_trust.TryGet(mac, out var snapshot));

        return snapshot.Band;
    }

    private double Scale(TrustOutcome outcome, string mac = MoistB) =>
        _trust.Sensitivity(mac, _options.SensitivityFloor).Single(item => item.Outcome == outcome).Scale;

    private InvestigationDetails Only() => Assert.Single(_service.List(null, 10));

    [Fact]
    public void A_node_falling_into_fault_opens_an_investigation_with_the_decay_window()
    {
        TrustTransition? crossing = null;
        _trust.FaultEntered += transition => crossing = transition;

        // Readings in and around the window, so the investigation has something to gather.
        for (var second = 0; second < 120; second += 5)
        {
            _mesh.History.Append(HistoryEntry.FromReading(
                _mesh.Sequence.Next(),
                new NumericPacket<float>(MoistB, 40 - second / 10f, T0 + TimeSpan.FromSeconds(second), SensorCategory.Environmental, "%RH")));
        }

        _mesh.Alerts.Raise(new AlertKey(MoistB, AlertKind.OutOfRange));
        var lastNominal = Fault();

        var investigation = Only();

        Assert.NotNull(crossing);
        Assert.Equal(InvestigationStatus.Open, investigation.Status);
        Assert.Equal(MoistB, investigation.MacAddress);
        Assert.Equal("Moisture B", investigation.Room);

        // The window is from the last moment the node was nominal to the moment it crossed: when its decay began.
        Assert.Equal(crossing.WindowStart, investigation.WindowStart);
        Assert.Equal(crossing.WindowEnd, investigation.WindowEnd);
        Assert.Equal(crossing.Score, investigation.Score);
        Assert.Equal(crossing.Reason, investigation.Reason);
        Assert.True(investigation.WindowStart <= lastNominal + Step);
        Assert.True(investigation.WindowStart < investigation.WindowEnd);
        Assert.Equal(investigation.WindowEnd, investigation.OpenedAt);

        // What was gathered is inside the window, and says what the engine is blaming.
        Assert.Equal(TrustOutcome.RangeViolation, investigation.CausedBy);
        Assert.NotEmpty(investigation.TrustHistory);
        Assert.All(investigation.TrustHistory, point => Assert.InRange(point.Timestamp, investigation.WindowStart, investigation.WindowEnd));
        Assert.Contains(new AlertKey(MoistB, AlertKind.OutOfRange), investigation.ActiveAlerts);
        Assert.All(investigation.Readings, reading => Assert.InRange(reading.Timestamp, investigation.WindowStart, investigation.WindowEnd));
        Assert.Equal(investigation.Readings.Count, investigation.ReadingCount);
    }

    [Fact]
    public void The_readings_kept_are_bounded_to_the_latest_in_the_window()
    {
        _options.MaxReadings = 3;

        for (var second = 0; second < 200; second += 2)
        {
            _mesh.History.Append(HistoryEntry.FromReading(
                _mesh.Sequence.Next(),
                new NumericPacket<float>(MoistB, second, T0 + TimeSpan.FromSeconds(second), SensorCategory.Environmental, "%RH")));
        }

        Fault();

        var investigation = Only();

        Assert.Equal(3, investigation.Readings.Count);
        Assert.True(investigation.ReadingCount > 3);
        Assert.Equal(investigation.Readings.OrderBy(reading => reading.Timestamp), investigation.Readings);
    }

    [Fact]
    public void The_fault_clearing_resolves_it_automatically()
    {
        Fault();
        Assert.Equal(InvestigationStatus.Open, Only().Status);

        Recover();

        var investigation = Only();

        Assert.Equal(InvestigationStatus.Resolved, investigation.Status);

        // The moment the node came out of fault: the last outcome recorded, one step before the clock was moved on.
        Assert.Equal(_clock.GetUtcNow() - Step, investigation.ResolvedAt);
        Assert.Null(investigation.Verdict);
    }

    [Fact]
    public void An_operator_who_has_already_given_a_verdict_keeps_the_moment_they_gave_it()
    {
        Fault();
        var id = Only().Id;

        var verdictAt = _clock.GetUtcNow();
        Assert.Equal(VerdictOutcome.Recorded, _service.GiveVerdict(id, new VerdictRequest(Verdict.Confirm)).Outcome);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Recover();

        var investigation = Only();

        Assert.Equal(Verdict.Confirm, investigation.Verdict);
        Assert.Equal(InvestigationStatus.Resolved, investigation.Status);
        Assert.Equal(verdictAt, investigation.ResolvedAt);
    }

    [Fact]
    public void Confirm_changes_nothing_about_detection()
    {
        Fault();

        var result = _service.GiveVerdict(Only().Id, new VerdictRequest(Verdict.Confirm));

        Assert.Equal(VerdictOutcome.Recorded, result.Outcome);
        Assert.Null(result.Investigation!.SensitivityChange);
        Assert.All(result.Investigation.Sensitivity, state => Assert.Equal(1, state.Scale));
    }

    [Fact]
    public void Dismiss_makes_that_node_a_little_less_sensitive_to_what_caused_the_fault_and_nothing_else()
    {
        Fault();

        // Another node with its own history, which a dismissal here must not touch.
        Step_(TrustOutcome.Success, PumpA);
        Assert.True(_trust.TryGet(MoistB, out var before));

        var result = _service.GiveVerdict(Only().Id, new VerdictRequest(Verdict.Dismiss));

        var change = result.Investigation!.SensitivityChange;

        Assert.Equal(new SensitivityChange(TrustOutcome.RangeViolation, 1, 0.85), change);
        Assert.Equal(0.85, Scale(TrustOutcome.RangeViolation));
        Assert.Equal(1, Scale(TrustOutcome.SchemaFailure));
        Assert.Equal(1, Scale(TrustOutcome.RangeViolation, PumpA));

        Assert.True(_trust.TryGet(MoistB, out var after));
        Assert.True(after.Score > before.Score);
    }

    [Fact]
    public void Changing_the_verdict_gives_the_sensitivity_back()
    {
        Fault();
        var id = Only().Id;

        _service.GiveVerdict(id, new VerdictRequest(Verdict.Dismiss));
        Assert.True(Scale(TrustOutcome.RangeViolation) < 1);

        var changed = _service.GiveVerdict(id, new VerdictRequest(Verdict.Confirm));

        Assert.Equal(1, Scale(TrustOutcome.RangeViolation));
        Assert.Null(changed.Investigation!.SensitivityChange);
        Assert.Equal(Verdict.Confirm, changed.Investigation.Verdict);
    }

    [Fact]
    public void However_many_faults_are_dismissed_the_sensitivity_stops_at_the_floor_and_the_node_can_still_fault()
    {
        for (var round = 0; round < 12; round++)
        {
            Fault();
            var open = _service.List(InvestigationStatus.Open, 1).Single();

            _service.GiveVerdict(open.Id, new VerdictRequest(Verdict.Dismiss));
            Recover();

            Assert.True(Scale(TrustOutcome.RangeViolation) >= _options.SensitivityFloor);
        }

        Assert.Equal(_options.SensitivityFloor, Scale(TrustOutcome.RangeViolation));

        // At the floor it is still detecting: a node that keeps failing still reaches fault.
        Fault();

        Assert.Equal(TrustBand.Fault, Band(MoistB));
    }

    [Fact]
    public void A_relabel_needs_a_cause_and_stores_it_without_changing_detection()
    {
        Fault();
        var id = Only().Id;

        var missing = _service.GiveVerdict(id, new VerdictRequest(Verdict.Relabel));

        Assert.Equal(VerdictOutcome.Invalid, missing.Outcome);
        Assert.Null(Only().Verdict);

        var given = _service.GiveVerdict(id, new VerdictRequest(Verdict.Relabel, FaultCause.Power));

        Assert.Equal(VerdictOutcome.Recorded, given.Outcome);
        Assert.Equal(Verdict.Relabel, given.Investigation!.Verdict);
        Assert.Equal(FaultCause.Power, given.Investigation.Cause);
        Assert.Null(given.Investigation.SensitivityChange);
        Assert.Equal(1, Scale(TrustOutcome.RangeViolation));
    }

    [Fact]
    public void An_unknown_investigation_is_not_found()
    {
        Assert.Equal(VerdictOutcome.NotFound, _service.GiveVerdict(Guid.NewGuid(), new VerdictRequest(Verdict.Confirm)).Outcome);
        Assert.Null(_service.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Every_verdict_is_recorded_as_an_operator_action_so_the_engine_can_learn_from_it()
    {
        Fault();
        var id = Only().Id;

        _service.GiveVerdict(id, new VerdictRequest(Verdict.Dismiss));

        var action = Assert.Single(_recorded.Actions);

        Assert.Equal(OperatorActionKind.Verdict, action.Kind);
        Assert.Equal(MoistB, action.MacAddress);
        Assert.Equal("Dismiss", action.Command);
        Assert.Equal(_clock.GetUtcNow(), action.At);
    }

    [Fact]
    public async Task The_investigation_carries_what_the_pattern_engine_knows_about_faults_like_it()
    {
        var engine = Engine();
        var learned = new InvestigationService(_trust, _mesh.Alerts, _mesh.History, engine, Directory(), _clock, Options.Create(_options));

        // Three times before, this node fell into fault and an operator then opened Pump A.
        for (var round = 0; round < 3; round++)
        {
            var at = T0 - TimeSpan.FromHours(3 - round);
            engine.RecordTrigger(new TriggerEvent(TriggerKind.TrustBand, MoistB, nameof(TrustBand.Fault), at));
            engine.RecordAction(View(PumpA, at + TimeSpan.FromMinutes(2)));
        }

        await learned.StartAsync(CancellationToken.None);

        try
        {
            Fault();

            var precedent = learned.List(null, 10).Single().Precedent;

            Assert.Equal(PatternLevel.Specific, precedent.AnsweredBy);
            Assert.Equal(PumpA, precedent.Associations[0].Action.Target);
        }
        finally
        {
            await learned.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_node_like_this_one_answers_when_this_node_has_never_faulted()
    {
        var engine = Engine();
        var learned = new InvestigationService(_trust, _mesh.Alerts, _mesh.History, engine, Directory(), _clock, Options.Create(_options));

        // It was MoistC that faulted before, in the same zone and of the same kind.
        for (var round = 0; round < 3; round++)
        {
            var at = T0 - TimeSpan.FromHours(3 - round);
            engine.RecordTrigger(new TriggerEvent(TriggerKind.TrustBand, MoistC, nameof(TrustBand.Fault), at));
            engine.RecordAction(View(PumpA, at + TimeSpan.FromMinutes(2)));
        }

        await learned.StartAsync(CancellationToken.None);

        try
        {
            Fault();

            var precedent = learned.List(null, 10).Single().Precedent;

            Assert.Equal(PatternLevel.General, precedent.AnsweredBy);
        }
        finally
        {
            await learned.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void The_newest_comes_first_and_the_list_can_be_filtered_by_status()
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        Fault(MoistB);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Fault(MoistC);
        Recover(MoistB);

        var all = _service.List(null, 10);

        Assert.Equal([MoistC, MoistB], all.Select(item => item.MacAddress));
        Assert.Equal(MoistC, Assert.Single(_service.List(InvestigationStatus.Open, 10)).MacAddress);
        Assert.Equal(MoistB, Assert.Single(_service.List(InvestigationStatus.Resolved, 10)).MacAddress);
        Assert.Single(_service.List(null, 1));
    }

    [Fact]
    public void Retention_is_bounded_and_never_drops_one_that_is_still_open()
    {
        _options.MaxRetained = 3;
        string[] nodes = ["B8:27:EB:50:00:01", "B8:27:EB:50:00:02", "B8:27:EB:50:00:03", "B8:27:EB:50:00:04", "B8:27:EB:50:00:05"];

        foreach (var node in nodes)
        {
            Fault(node);
            Recover(node);
        }

        // The last one is faulted and open, and stays however many closed ones there are.
        Fault(MoistB);

        var kept = _service.List(null, 100);

        Assert.Equal(3, kept.Count);
        Assert.Contains(kept, item => item.MacAddress == MoistB && item.Status == InvestigationStatus.Open);
    }
}
