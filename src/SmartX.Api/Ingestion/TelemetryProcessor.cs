using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.History;
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
    IActuatorStateStore actuators,
    IHistoryStore history,
    AlertTracker alerts,
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

    /// <summary>An actuator's state from a packet: 1 or 0 for a state, the level for a number.</summary>
    private static double? ObservedValue(ITelemetryPacket packet) => packet switch
    {
        StatePacket state => state.Value ? 1 : 0,
        _ when packet.TryGetNumericValue(out var value) => value,
        _ => null,
    };

    /// <summary>Retains one reading and scores it. Internal so a test can drive it without a host.</summary>
    internal async Task ProcessAsync(QueuedReading reading, CancellationToken cancellationToken)
    {
        await store.AddAsync(reading.Packet, cancellationToken);

        // History keeps every accepted reading in time order, keyed by when it was taken and the arrival number
        // the queue gave it, so two readings stamped the same instant are both kept in the order they arrived.
        // The store above is the ring of recent readings for the sensor page; this is the record timelines read.
        history.Append(HistoryEntry.FromReading(reading.Sequence, reading.Packet));

        // A reading proves the node is connected, and counts towards ending an out-of-range alert.
        alerts.ReadingAccepted(reading.Packet.NodeId);

        // Stamped with the moment the reading arrived, not the moment it left the queue, so a backlog cannot
        // distort a node's trust history.
        trust.Record(reading.Packet.NodeId, TrustOutcome.Success, reading.Packet.Timestamp);

        // An actuator's own telemetry is how the gateway learns what state it is in, and a command records
        // that state as the one it replaced. Read here, in the worker, so it is the state as stored.
        if (reading.Packet.Category == SensorCategory.Actuator && ObservedValue(reading.Packet) is { } value)
        {
            actuators.Observe(reading.Packet.NodeId, value, reading.Packet.Timestamp, ObservationSource.Telemetry);
        }

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
