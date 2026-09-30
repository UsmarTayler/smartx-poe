using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Ingestion;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;
using Xunit.Abstractions;

namespace SmartX.Tests;

/// <summary>
/// Measures, rather than asserts, that a critical reading really does bypass a backed-up standard queue.
/// </summary>
/// <remarks>
/// "Critical alerts are faster" is a claim; this is the evidence. The real worker runs against a store made
/// deliberately slow, thousands of ordinary readings are queued behind it, and then one critical and one
/// normal reading arrive at the same moment. The test records how long each waited before it was stored and
/// prints the numbers, which are the ones quoted in the README.
/// <para>
/// The slowness is a wall-clock busy-wait per packet, not a sleep. Sleeping for a fraction of a
/// millisecond on Windows rounds up to the timer resolution, so a 0.3 ms "delay" would really be 15 ms and
/// the backlog would take over a minute. A busy-wait costs exactly what it says, on any machine, which is what
/// keeps the size of the backlog — and so the normal reading's wait — repeatable.
/// </para>
/// </remarks>
public class CriticalBypassMeasurementTests(ITestOutputHelper output)
{
    private const string BacklogNode = "AA:BB:CC:00:00:10";
    private const string CriticalProbe = "AA:BB:CC:00:00:11";
    private const string NormalProbe = "AA:BB:CC:00:00:12";

    private const int BacklogSize = 5_000;
    private static readonly TimeSpan PerPacketCost = TimeSpan.FromMicroseconds(300);

    [Fact]
    public async Task A_critical_reading_waits_a_fraction_of_the_time_a_normal_one_does_behind_a_backlog()
    {
        var store = new SlowStore(PerPacketCost);
        var queue = new TelemetryIngestionQueue(Options.Create(new IngestionOptions { MaxQueueDepth = 20_000 }), TimeProvider.System);
        var trust = new TrustEngine(Options.Create(new TrustOptions()), TimeProvider.System, NullLogger<TrustEngine>.Instance);
        var worker = new TelemetryProcessor(queue, store, trust, new SmartX.Api.Commands.InMemoryActuatorStateStore(), NullLogger<TelemetryProcessor>.Instance);

        // Fill the standard lane before the worker starts, so it opens on a genuine backlog instead of racing
        // to keep up with one.
        for (var value = 0; value < BacklogSize; value++)
        {
            Assert.True(queue.TryEnqueue(Numeric(BacklogNode, value), alert: null, out _));
        }

        var depthBefore = queue.Stats().TotalDepth;

        await worker.StartAsync(CancellationToken.None);

        try
        {
            // The same instant, from the queue's point of view: back to back, with nothing between them.
            var criticalSentAt = Stopwatch.GetTimestamp();
            queue.TryEnqueue(Numeric(CriticalProbe, 9_999), new CriticalAlert(1, "power spike"), out _);
            var normalSentAt = Stopwatch.GetTimestamp();
            queue.TryEnqueue(Numeric(NormalProbe, 9_999), alert: null, out _);

            var backlogWhenInjected = queue.Stats().Lanes.Single(lane => lane.Lane == "standard").Depth;

            await store.NormalProbeStored.Task.WaitAsync(TimeSpan.FromSeconds(60));

            var criticalWait = Stopwatch.GetElapsedTime(criticalSentAt, store.CriticalProbeStoredAt);
            var normalWait = Stopwatch.GetElapsedTime(normalSentAt, store.NormalProbeStoredAt);

            output.WriteLine($"Backlog queued before the worker started : {depthBefore:N0} readings");
            output.WriteLine($"Standard lane depth when probes arrived  : {backlogWhenInjected:N0} readings");
            output.WriteLine($"Worker cost per reading                  : {PerPacketCost.TotalMilliseconds:0.0} ms");
            output.WriteLine($"Critical reading waited                  : {criticalWait.TotalMilliseconds:N1} ms");
            output.WriteLine($"Normal reading waited                    : {normalWait.TotalMilliseconds:N1} ms");
            output.WriteLine($"Critical was processed                   : {normalWait / criticalWait:N0}x sooner");

            // The bypass, stated as numbers: the alert did not wait for the backlog.
            Assert.True(backlogWhenInjected > BacklogSize / 2, $"Only {backlogWhenInjected} readings were still queued.");
            Assert.True(criticalWait < TimeSpan.FromMilliseconds(100), $"The critical reading waited {criticalWait.TotalMilliseconds:N1} ms.");
            Assert.True(normalWait > TimeSpan.FromMilliseconds(500), $"The normal reading waited only {normalWait.TotalMilliseconds:N1} ms.");
            Assert.True(normalWait > criticalWait * 10, "The critical reading was not at least ten times faster.");

            // The alert left the queue while most of the backlog was still waiting.
            Assert.True(store.BacklogStoredBeforeCritical < BacklogSize / 10, $"{store.BacklogStoredBeforeCritical} backlog readings went first.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Ordinary_readings_still_progress_while_criticals_keep_arriving()
    {
        // The other half of the trade-off. If criticals could starve the standard lane, ordinary telemetry
        // would stop entirely under an alert storm. The valve lets one normal reading through per run of
        // MaxConsecutiveCritical alerts, so the standard lane always moves.
        var store = new SlowStore(TimeSpan.FromMicroseconds(200));
        var queue = new TelemetryIngestionQueue(
            Options.Create(new IngestionOptions { MaxConsecutiveCritical = 10, MaxCriticalQueueDepth = 5_000 }), TimeProvider.System);
        var trust = new TrustEngine(Options.Create(new TrustOptions()), TimeProvider.System, NullLogger<TrustEngine>.Instance);
        var worker = new TelemetryProcessor(queue, store, trust, new SmartX.Api.Commands.InMemoryActuatorStateStore(), NullLogger<TelemetryProcessor>.Instance);

        for (var value = 0; value < 200; value++)
        {
            queue.TryEnqueue(Numeric(BacklogNode, value), alert: null, out _);
        }

        for (var value = 0; value < 2_000; value++)
        {
            queue.TryEnqueue(Numeric(CriticalProbe, value), new CriticalAlert(1, "power spike"), out _);
        }

        await worker.StartAsync(CancellationToken.None);

        try
        {
            // Wait until the worker has cleared a good share of the alerts, then look at where the normal
            // readings had got to. Strict priority would show zero.
            while (queue.Stats().Lanes.Single(lane => lane.Lane == "critical").Processed < 1_000)
            {
                await Task.Delay(20);
            }

            var normalsSoFar = queue.Stats().Lanes.Single(lane => lane.Lane == "standard").Processed;
            var criticalsSoFar = queue.Stats().Lanes.Single(lane => lane.Lane == "critical").Processed;

            output.WriteLine($"After {criticalsSoFar:N0} critical readings, {normalsSoFar:N0} normal readings had been processed.");

            // One normal per ten criticals, so around a tenth of the criticals' count.
            Assert.InRange(normalsSoFar, criticalsSoFar / 12, criticalsSoFar / 8);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static NumericPacket<int> Numeric(string node, int value) =>
        new(node, value, DateTimeOffset.UtcNow, SensorCategory.PowerConsumption, "W");

    /// <summary>A store that takes a fixed amount of time per packet and notes when the probes arrive.</summary>
    private sealed class SlowStore(TimeSpan perPacket) : ITelemetryStore
    {
        private int _backlogStored;

        public TaskCompletionSource NormalProbeStored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long CriticalProbeStoredAt { get; private set; }

        public long NormalProbeStoredAt { get; private set; }

        public int BacklogStoredBeforeCritical { get; private set; }

        public Task AddAsync(ITelemetryPacket packet, CancellationToken cancellationToken = default)
        {
            SpinFor(perPacket);

            switch (packet.NodeId)
            {
                case CriticalProbe:
                    CriticalProbeStoredAt = Stopwatch.GetTimestamp();
                    BacklogStoredBeforeCritical = _backlogStored;
                    break;

                case NormalProbe:
                    NormalProbeStoredAt = Stopwatch.GetTimestamp();
                    NormalProbeStored.TrySetResult();
                    break;

                default:
                    _backlogStored++;
                    break;
            }

            return Task.CompletedTask;
        }

        public Task<int> CountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CountForAsync(string nodeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ITelemetryPacket>> RecentAsync(string nodeId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static void SpinFor(TimeSpan duration)
        {
            var until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);

            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(20);
            }
        }
    }
}
