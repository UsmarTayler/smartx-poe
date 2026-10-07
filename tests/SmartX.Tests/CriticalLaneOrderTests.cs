using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Ingestion;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// A critical reading is processed before the ordinary readings queued ahead of it, proved by order alone.
/// </summary>
/// <remarks>
/// <b>No stopwatch.</b> The claim is about <i>order</i>: the alert overtakes a backlog. Whether it does so in
/// 2 ms or 20 ms depends on the machine, and a test that asserts a duration fails on a slow one for no fault of
/// the code. The timing measurement lives in <see cref="CriticalBypassMeasurementTests"/>, behind the Benchmark
/// trait and out of the default run.
/// <para>
/// Order is made deterministic with a gate rather than with speed. The store blocks on the first reading it
/// receives, so the worker is held mid-reading with the rest of the backlog certainly still queued. The two
/// probes are enqueued while it is held, then the gate opens, and what is recorded is simply the order the store
/// saw them in. Nothing in the assertions depends on how long anything took.
/// </para>
/// </remarks>
public class CriticalLaneOrderTests
{
    private const string BacklogNode = "AA:BB:CC:00:00:10";
    private const string CriticalProbe = "AA:BB:CC:00:00:11";
    private const string NormalProbe = "AA:BB:CC:00:00:12";

    private const int BacklogSize = 300;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_critical_reading_is_processed_before_the_normal_reading_that_arrived_with_it_and_before_the_backlog(bool criticalArrivesFirst)
    {
        var store = new GatedStore();
        var queue = new TelemetryIngestionQueue(
            Options.Create(new IngestionOptions { MaxQueueDepth = 20_000 }), TimeProvider.System, new SmartX.Api.History.ArrivalSequence());
        var trust = new TrustEngine(Options.Create(new TrustOptions()), TimeProvider.System, NullLogger<TrustEngine>.Instance);
        var mesh = MeshTestHost.Create(TimeProvider.System);
        var worker = new TelemetryProcessor(
            queue, store, trust, new SmartX.Api.Commands.InMemoryActuatorStateStore(), mesh.History, mesh.Alerts, NullLogger<TelemetryProcessor>.Instance);

        for (var value = 0; value < BacklogSize; value++)
        {
            Assert.True(queue.TryEnqueue(Numeric(BacklogNode, value), alert: null, out _));
        }

        await worker.StartAsync(CancellationToken.None);

        try
        {
            // The worker has taken the first backlog reading and is held inside the store with it. Everything
            // else is queued, so the probes arrive behind a genuine backlog. The timeout is only a guard
            // against a hang; it is never what a test result depends on.
            await store.FirstTaken.WaitAsync(TimeSpan.FromSeconds(60));

            if (criticalArrivesFirst)
            {
                queue.TryEnqueue(Numeric(CriticalProbe, 1), new CriticalAlert(1, "power spike"), out _);
                queue.TryEnqueue(Numeric(NormalProbe, 1), alert: null, out _);
            }
            else
            {
                // The ordinary reading got there first, and the critical one still overtakes it.
                queue.TryEnqueue(Numeric(NormalProbe, 1), alert: null, out _);
                queue.TryEnqueue(Numeric(CriticalProbe, 1), new CriticalAlert(1, "power spike"), out _);
            }

            store.Release();

            await store.NormalProbeStored.WaitAsync(TimeSpan.FromSeconds(60));

            var order = store.Order;
            var critical = order.IndexOf(CriticalProbe);
            var normal = order.IndexOf(NormalProbe);

            Assert.True(critical >= 0, "The critical reading was never processed.");
            Assert.True(critical < normal, "The critical reading was processed after the ordinary one.");

            // Only the reading already in flight when the probes arrived came before it. Not one of the
            // backlog queued behind that.
            Assert.Equal(1, critical);

            // The ordinary reading waited its turn behind the whole backlog.
            Assert.Equal(BacklogSize + 1, normal);
        }
        finally
        {
            store.Release();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static NumericPacket<int> Numeric(string node, int value) =>
        new(node, value, DateTimeOffset.UtcNow, SensorCategory.PowerConsumption, "W");

    /// <summary>A store that holds the first reading until released, and records the order readings arrive in.</summary>
    private sealed class GatedStore : ITelemetryStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _order = [];
        private readonly Lock _orderGate = new();
        private int _calls;

        public TaskCompletionSource FirstTakenSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstTaken => FirstTakenSource.Task;

        public TaskCompletionSource NormalProbeStoredSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NormalProbeStored => NormalProbeStoredSource.Task;

        public List<string> Order
        {
            get
            {
                lock (_orderGate)
                {
                    return [.. _order];
                }
            }
        }

        public void Release() => _gate.TrySetResult();

        public async Task AddAsync(ITelemetryPacket packet, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstTakenSource.TrySetResult();
                await _gate.Task;
            }

            lock (_orderGate)
            {
                _order.Add(packet.NodeId);
            }

            if (packet.NodeId == NormalProbe)
            {
                NormalProbeStoredSource.TrySetResult();
            }
        }

        public Task<int> CountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CountForAsync(string nodeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ITelemetryPacket>> RecentAsync(string nodeId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
