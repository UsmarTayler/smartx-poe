using Microsoft.Extensions.Options;
using SmartX.Api.Ingestion;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Covers the two ingestion lanes on their own: order, priority, stability, starvation and bounds.
/// </summary>
/// <remarks>
/// These drive <see cref="TelemetryIngestionQueue"/> directly with no API and no worker, so what is asserted is
/// the queue's behaviour alone. Each test enqueues everything first and then drains, which makes the order it
/// checks a property of the queue and not of how two threads happened to interleave.
/// </remarks>
public class IngestionQueueTests
{
    private const string Node = "AA:BB:CC:DD:EE:01";

    private static TelemetryIngestionQueue NewQueue(Action<IngestionOptions>? configure = null)
    {
        var options = new IngestionOptions();
        configure?.Invoke(options);

        return new TelemetryIngestionQueue(Options.Create(options), TimeProvider.System, new SmartX.Api.History.ArrivalSequence());
    }

    private static NumericPacket<int> Packet(int value) =>
        new(Node, value, DateTimeOffset.UnixEpoch, SensorCategory.PowerConsumption, "W");

    private static async Task<List<int>> DrainValuesAsync(TelemetryIngestionQueue queue, int count)
    {
        var values = new List<int>();

        for (var i = 0; i < count; i++)
        {
            var reading = await queue.DequeueAsync(CancellationToken.None);
            values.Add(((NumericPacket<int>)reading.Packet).Value);
        }

        return values;
    }

    [Fact]
    public async Task Standard_readings_come_out_in_the_order_they_went_in()
    {
        var queue = NewQueue();

        for (var value = 0; value < 500; value++)
        {
            Assert.True(queue.TryEnqueue(Packet(value), alert: null, out _));
        }

        Assert.Equal(Enumerable.Range(0, 500), await DrainValuesAsync(queue, 500));
    }

    [Fact]
    public void A_receipt_names_the_lane_and_the_position_in_it()
    {
        var queue = NewQueue();

        queue.TryEnqueue(Packet(1), alert: null, out _);
        queue.TryEnqueue(Packet(2), alert: null, out var second);
        queue.TryEnqueue(Packet(3), new CriticalAlert(1, "power spike"), out var critical);

        Assert.Equal("standard", second?.Lane);
        Assert.Equal(2, second?.QueuePosition);

        // Its own lane, so it is first there however many ordinary readings are ahead in the other.
        Assert.Equal("critical", critical?.Lane);
        Assert.Equal(1, critical?.QueuePosition);
    }

    [Fact]
    public async Task A_critical_reading_is_processed_before_thousands_of_queued_normal_ones()
    {
        var queue = NewQueue(options => options.MaxQueueDepth = 10_000);

        for (var value = 0; value < 5_000; value++)
        {
            queue.TryEnqueue(Packet(value), alert: null, out _);
        }

        queue.TryEnqueue(Packet(-1), new CriticalAlert(1, "power spike"), out _);

        var first = await queue.DequeueAsync(CancellationToken.None);

        Assert.True(first.IsCritical);
        Assert.Equal(-1, ((NumericPacket<int>)first.Packet).Value);
    }

    [Fact]
    public async Task Critical_readings_of_equal_severity_are_processed_in_arrival_order()
    {
        // The stability requirement. PriorityQueue is a binary heap and does not keep equal priorities in
        // insertion order, so ordering by severity alone would let two alerts of the same severity swap. The
        // (severity, sequence) composite priority is what stops that.
        var queue = NewQueue();

        for (var value = 0; value < 200; value++)
        {
            queue.TryEnqueue(Packet(value), new CriticalAlert(Severity: 1, "power spike"), out _);
        }

        Assert.Equal(Enumerable.Range(0, 200), await DrainValuesAsync(queue, 200));
    }

    [Fact]
    public void Without_a_sequence_number_the_framework_heap_does_reorder_equal_priorities()
    {
        // Documents why the composite priority exists, by showing the failure it prevents on the same
        // structure. If a future runtime made PriorityQueue stable, this would stop failing and the sequence
        // number would become unnecessary but harmless; today it is the difference between right and wrong.
        var heap = new PriorityQueue<int, int>();

        for (var value = 0; value < 200; value++)
        {
            heap.Enqueue(value, priority: 1);
        }

        var order = new List<int>();

        while (heap.TryDequeue(out var value, out _))
        {
            order.Add(value);
        }

        Assert.NotEqual(Enumerable.Range(0, 200), order);
    }

    [Fact]
    public async Task A_more_severe_alert_is_processed_before_a_milder_one_that_arrived_earlier()
    {
        var queue = NewQueue();

        queue.TryEnqueue(Packet(10), new CriticalAlert(Severity: 2, "temperature excursion"), out _);
        queue.TryEnqueue(Packet(20), new CriticalAlert(Severity: 1, "power spike"), out _);

        Assert.Equal([20, 10], await DrainValuesAsync(queue, 2));
    }

    [Fact]
    public async Task After_the_configured_run_of_criticals_a_normal_reading_is_let_through()
    {
        // The starvation policy: never more than three alerts in a row while ordinary readings are waiting.
        var queue = NewQueue(options => options.MaxConsecutiveCritical = 3);

        queue.TryEnqueue(Packet(100), alert: null, out _);
        queue.TryEnqueue(Packet(101), alert: null, out _);

        for (var value = 0; value < 8; value++)
        {
            queue.TryEnqueue(Packet(value), new CriticalAlert(1, "power spike"), out _);
        }

        // Three alerts, a normal one, three alerts, the other normal one, then the last two alerts.
        Assert.Equal([0, 1, 2, 100, 3, 4, 5, 101, 6, 7], await DrainValuesAsync(queue, 10));
    }

    [Fact]
    public async Task With_no_normal_readings_waiting_criticals_are_not_held_back()
    {
        // The valve only yields when there is something to yield to. An empty standard lane must not stall
        // alerts just because the counter reached its limit.
        var queue = NewQueue(options => options.MaxConsecutiveCritical = 2);

        for (var value = 0; value < 6; value++)
        {
            queue.TryEnqueue(Packet(value), new CriticalAlert(1, "power spike"), out _);
        }

        Assert.Equal(Enumerable.Range(0, 6), await DrainValuesAsync(queue, 6));
    }

    [Fact]
    public void A_full_standard_lane_refuses_and_counts_the_refusal()
    {
        var queue = NewQueue(options => options.MaxQueueDepth = 3);

        for (var value = 0; value < 3; value++)
        {
            Assert.True(queue.TryEnqueue(Packet(value), alert: null, out _));
        }

        Assert.False(queue.TryEnqueue(Packet(3), alert: null, out var receipt));
        Assert.Null(receipt);

        var stats = queue.Stats();
        Assert.Equal(1, stats.RejectedWhenFull);
        Assert.Equal(3, stats.Lanes.Single(lane => lane.Lane == "standard").Depth);
    }

    [Fact]
    public void A_full_standard_lane_does_not_block_a_critical_reading()
    {
        // Separate bounds, so the alert that matters most cannot be refused because ordinary telemetry has
        // filled the other lane.
        var queue = NewQueue(options => options.MaxQueueDepth = 1);

        Assert.True(queue.TryEnqueue(Packet(1), alert: null, out _));
        Assert.False(queue.TryEnqueue(Packet(2), alert: null, out _));

        Assert.True(queue.TryEnqueue(Packet(3), new CriticalAlert(1, "power spike"), out _));
    }

    [Fact]
    public async Task Completing_a_reading_records_its_wait_in_the_right_lane()
    {
        var queue = NewQueue();

        queue.TryEnqueue(Packet(1), alert: null, out _);
        queue.TryEnqueue(Packet(2), new CriticalAlert(1, "power spike"), out _);

        queue.Complete(await queue.DequeueAsync(CancellationToken.None));
        queue.Complete(await queue.DequeueAsync(CancellationToken.None));

        var stats = queue.Stats();

        Assert.Equal(1, stats.Lanes.Single(lane => lane.Lane == "critical").Processed);
        Assert.Equal(1, stats.Lanes.Single(lane => lane.Lane == "standard").Processed);
        Assert.Equal(0, stats.TotalDepth);
    }

    [Fact]
    public async Task Dequeueing_waits_for_a_reading_and_wakes_when_one_arrives()
    {
        var queue = NewQueue();

        var waiting = queue.DequeueAsync(CancellationToken.None);
        await Task.Delay(50);

        Assert.False(waiting.IsCompleted);

        queue.TryEnqueue(Packet(7), alert: null, out _);

        Assert.Equal(7, ((NumericPacket<int>)(await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Packet).Value);
    }
}

/// <summary>
/// Covers deciding which readings are critical.
/// </summary>
public class CriticalClassifierTests
{
    private static CriticalClassifier Classifier(Action<IngestionOptions>? configure = null)
    {
        var options = new IngestionOptions();
        configure?.Invoke(options);

        return new CriticalClassifier(Options.Create(options));
    }

    private static NumericPacket<float> Reading(float value, SensorCategory category, string unit) =>
        new("AA:BB:CC:DD:EE:01", value, DateTimeOffset.UnixEpoch, category, unit);

    [Theory]
    [InlineData(6000f, "W", true)]
    [InlineData(4500f, "W", false)]
    [InlineData(190f, "kW", true)]
    [InlineData(150f, "kW", false)]
    public void A_power_reading_is_critical_only_above_its_units_limit(float value, string unit, bool expected)
    {
        // The same category reports in two units, and 180 is an emergency in one and nothing in the other,
        // which is why a rule names the unit as well as the category.
        var critical = Classifier().IsCritical(Reading(value, SensorCategory.PowerConsumption, unit), out var alert);

        Assert.Equal(expected, critical);
        Assert.Equal(expected, alert.Reason == "power spike");
    }

    [Theory]
    [InlineData(20f, true)]
    [InlineData(34.9f, true)]
    [InlineData(35f, false)]
    [InlineData(55f, false)]
    public void Moisture_is_critical_when_it_falls_below_the_limit(float value, bool expected) =>
        Assert.Equal(expected, Classifier().IsCritical(Reading(value, SensorCategory.Environmental, "%RH"), out _));

    [Fact]
    public void A_low_reading_is_never_critical_for_a_rule_with_only_an_upper_limit()
    {
        Assert.False(Classifier().IsCritical(Reading(1f, SensorCategory.PowerConsumption, "W"), out _));
    }

    [Fact]
    public void A_state_reading_can_never_be_critical()
    {
        var packet = new StatePacket("AA:BB:CC:DD:EE:01", true, DateTimeOffset.UnixEpoch, "state");

        Assert.False(Classifier().IsCritical(packet, out _));
    }

    [Fact]
    public void A_unit_with_no_rule_is_never_critical()
    {
        Assert.False(Classifier().IsCritical(Reading(1_000_000f, SensorCategory.PowerConsumption, "MW"), out _));
    }

    [Fact]
    public void The_thresholds_come_from_configuration()
    {
        var classifier = Classifier(options => options.CriticalRules =
            [new CriticalRule(SensorCategory.PowerConsumption, "W", Above: 100, Severity: 3, Reason: "tight limit")]);

        Assert.True(classifier.IsCritical(Reading(150f, SensorCategory.PowerConsumption, "W"), out var alert));
        Assert.Equal(new CriticalAlert(3, "tight limit"), alert);
    }
}
