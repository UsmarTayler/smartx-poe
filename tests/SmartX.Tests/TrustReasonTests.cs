using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the plain-language reason shown next to each score.
/// </summary>
public class TrustReasonTests
{
    [Fact]
    public void Range_violations_are_counted_in_words()
    {
        var (engine, _) = Create();

        RecordMany(engine, NodeA, TrustOutcome.RangeViolation, 3);

        Assert.Equal("3 range violations", Trust(engine, NodeA).Reason);
    }

    [Fact]
    public void Schema_failures_are_described_as_type_failures()
    {
        var (engine, _) = Create();

        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 12);

        Assert.Equal("12 type failures", Trust(engine, NodeA).Reason);
    }

    [Fact]
    public void A_single_failure_is_not_pluralised()
    {
        var (engine, _) = Create();

        engine.Record(NodeA, TrustOutcome.RangeViolation);

        Assert.Equal("1 range violation", Trust(engine, NodeA).Reason);
    }

    [Fact]
    public void A_silent_node_is_described_by_how_long_it_has_been_silent()
    {
        var (engine, clock) = Create();
        engine.Record(NodeA, TrustOutcome.Success);

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(20));
        engine.CheckHeartbeat(NodeA, TimeSpan.FromMinutes(1));

        Assert.Equal("no packets for 4m 20s", Trust(engine, NodeA).Reason);
    }

    [Fact]
    public void Silence_takes_priority_over_failures_from_before_it()
    {
        // The range violations are true but no longer current. What matters now is that the node
        // has gone quiet.
        var (engine, clock) = Create();
        RecordMany(engine, NodeA, TrustOutcome.RangeViolation, 3);

        clock.Advance(TimeSpan.FromMinutes(2));
        engine.CheckHeartbeat(NodeA, TimeSpan.FromMinutes(1));

        Assert.Equal("no packets for 2m 0s", Trust(engine, NodeA).Reason);
    }

    [Fact]
    public void A_node_that_resumes_reporting_names_the_heartbeats_it_missed()
    {
        var (engine, clock) = Create();
        engine.Record(NodeA, TrustOutcome.Success);

        clock.Advance(TimeSpan.FromMinutes(3));
        engine.CheckHeartbeat(NodeA, TimeSpan.FromMinutes(1));
        engine.Record(NodeA, TrustOutcome.Success);

        Assert.Equal("3 missed heartbeats", Trust(engine, NodeA).Reason);
    }

    [Fact]
    public void The_failure_holding_the_score_down_hardest_is_named_first()
    {
        // Three range violations weigh 3; one type failure weighs 2.
        var (engine, _) = Create();
        RecordMany(engine, NodeA, TrustOutcome.RangeViolation, 3);
        engine.Record(NodeA, TrustOutcome.SchemaFailure);

        Assert.Equal("3 range violations, 1 type failure", Trust(engine, NodeA).Reason);

        // Two more type failures take them to 6 and past the range violations.
        RecordMany(engine, NodeA, TrustOutcome.SchemaFailure, 2);

        Assert.Equal("3 type failures, 3 range violations", Trust(engine, NodeA).Reason);
    }

    [Fact]
    public void A_healthy_node_reports_its_valid_packets()
    {
        var (engine, _) = Create();

        RecordMany(engine, NodeA, TrustOutcome.Success, 5);

        Assert.Equal("5 recent packets valid", Trust(engine, NodeA).Reason);
    }

    [Fact]
    public void Failures_older_than_two_half_lives_stop_being_named()
    {
        // At two half-lives a failure counts for a quarter. Naming it beyond that would describe
        // the node's past rather than its present.
        var (engine, clock) = Create();
        RecordMany(engine, NodeA, TrustOutcome.RangeViolation, 3);

        clock.Advance(TimeSpan.FromMinutes(25));
        engine.Reevaluate();

        Assert.Equal("no recent telemetry", Trust(engine, NodeA).Reason);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(260, "4m 20s")]
    [InlineData(7500, "2h 5m")]
    [InlineData(97200, "1d 3h")]
    public void Durations_are_written_the_way_a_person_would_say_them(int seconds, string expected)
    {
        Assert.Equal(expected, TrustReasons.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
