using SmartX.Shared;

namespace SmartX.Api.Ingestion;

/// <summary>A reading the worker has finished with.</summary>
/// <param name="Packet">The reading.</param>
/// <param name="Sequence">Its arrival number.</param>
/// <param name="AcceptedAt">When the API accepted it, before it waited in a queue.</param>
/// <param name="Alert">Set when it crossed a critical threshold.</param>
internal readonly record struct ProcessedReading(
    ITelemetryPacket Packet,
    long Sequence,
    DateTimeOffset AcceptedAt,
    CriticalAlert? Alert);

/// <summary>
/// Announces each reading as the ingestion worker finishes with it.
/// </summary>
/// <remarks>
/// A domain event, and the way the pipeline tells the outside world anything. The worker stores and scores a
/// reading and then says so here; it does not know whether anyone is listening, or that what listens is a
/// real-time hub. That is deliberate. The pipeline stays testable without a hub, and if the transport changes
/// again nothing in ingestion has to.
/// <para>
/// Raised on the worker's own thread, once per reading, so a handler must be quick: copy what it needs and
/// return. Anything slow done here slows ingestion for every device. A handler that throws is ignored, so a
/// faulty subscriber cannot stop readings being processed.
/// </para>
/// </remarks>
internal sealed class ProcessedReadings
{
    /// <summary>Raised after a reading has been stored, scored and counted as processed.</summary>
    public event Action<ProcessedReading>? Processed;

    /// <summary>Announces a reading.</summary>
    public void Publish(ProcessedReading reading)
    {
        var handlers = Processed;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<ProcessedReading> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(reading);
            }
            catch
            {
                // Deliberately swallowed. See the remarks on the class.
            }
        }
    }
}
