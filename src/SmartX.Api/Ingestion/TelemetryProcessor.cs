using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Ingestion;

/// <summary>
/// Drains the ingestion queues: everything that happens to a reading after the caller has been answered.
/// </summary>
/// <remarks>
/// <b>Why the work is split here.</b> Validation stays on the request thread because the caller needs its
/// answer now — a device that sent the wrong type must be told so, and Part 1 shows that rejection on screen
/// the moment it happens. Everything after acceptance is the gateway talking to itself: retaining the packet,
/// scoring the node, and later writing history and broadcasting. None of that changes the answer, all of it
/// costs time, and it belongs behind the queue where a burst can be absorbed instead of being pushed back
/// onto the device.
/// <para>
/// One worker, deliberately. Trust is accumulated state behind a single lock, so a second worker would
/// contend on it, and the ordering the FIFO queue exists to preserve would be lost as soon as two readings
/// from one node were processed at once.
/// </para>
/// </remarks>
internal sealed class TelemetryProcessor(
    TelemetryIngestionQueue queue,
    ITelemetryStore store,
    ITrustEngine trust,
    ILogger<TelemetryProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            QueuedReading reading;

            try
            {
                reading = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ProcessAsync(reading, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad reading must not end the worker. If it did, the queues would fill behind it and
                // every device would start seeing 503s because of a single malformed packet.
                logger.LogError(
                    exception,
                    "Failed to process reading {Receipt} from {Node}. The worker continues with the next.",
                    reading.ReceiptId,
                    reading.Packet.NodeId);
            }
        }
    }

    /// <summary>Retains one reading and scores it. Internal so a test can drive it without a host.</summary>
    internal async Task ProcessAsync(QueuedReading reading, CancellationToken cancellationToken)
    {
        await store.AddAsync(reading.Packet, cancellationToken);

        // Stamped with the moment the reading arrived, not the moment it left the queue, so a backlog cannot
        // distort a node's trust history.
        trust.Record(reading.Packet.NodeId, TrustOutcome.Success, reading.Packet.Timestamp);

        // Counted as processed only now, with everything done, so the stats never claim work that is still
        // in flight.
        queue.Complete(reading);

        if (reading.Alert is { } alert)
        {
            logger.LogWarning(
                "Critical reading from {Node}: {Reason} (severity {Severity}).",
                reading.Packet.NodeId,
                alert.Reason,
                alert.Severity);
        }
    }
}
