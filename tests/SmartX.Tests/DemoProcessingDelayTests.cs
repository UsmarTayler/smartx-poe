using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Ingestion;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// The demonstration setting that gives the ingestion worker a cost per reading, so a backlog can be built on purpose.
/// </summary>
/// <remarks>
/// Only a lower bound is asserted. A slow machine makes the pauses longer and never shorter, so "took at least this long"
/// cannot fail for reasons that are not the code's.
/// </remarks>
public class DemoProcessingDelayTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void The_delay_is_off_unless_it_is_configured()
    {
        Assert.Equal(TimeSpan.Zero, new IngestionOptions().DemoProcessingDelay);
    }

    [Fact]
    public async Task The_worker_pauses_after_each_reading_it_processes()
    {
        var options = Options.Create(new IngestionOptions { DemoProcessingDelay = Delay });
        var queue = new TelemetryIngestionQueue(options, TimeProvider.System, new SmartX.Api.History.ArrivalSequence());
        var trust = new TrustEngine(Options.Create(new TrustOptions()), TimeProvider.System, NullLogger<TrustEngine>.Instance);
        var mesh = MeshTestHost.Create(TimeProvider.System);
        var processed = new ProcessedReadings();

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        processed.Processed += _ =>
        {
            if (Interlocked.Increment(ref count) == 4)
            {
                done.TrySetResult();
            }
        };

        var worker = new TelemetryProcessor(
            queue,
            new InMemoryTelemetryStore(Options.Create(new TelemetryOptions())),
            trust,
            new SmartX.Api.Commands.InMemoryActuatorStateStore(),
            mesh.History,
            mesh.Alerts,
            processed,
            NullLogger<TelemetryProcessor>.Instance,
            options);

        for (var value = 0; value < 4; value++)
        {
            Assert.True(queue.TryEnqueue(new NumericPacket<int>("AA:BB:CC:00:00:10", value, DateTimeOffset.UtcNow, SensorCategory.PowerConsumption, "W"), alert: null, out _));
        }

        var clock = Stopwatch.StartNew();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            clock.Stop();

            // Four readings have three pauses between them, each of 50 ms. The margin allows for timer granularity.
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(100), $"Four readings took {clock.Elapsed.TotalMilliseconds:0} ms; the pauses were not applied.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
