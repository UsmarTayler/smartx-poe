using SmartX.Shared;

namespace SmartX.Api.Realtime;

/// <summary>
/// What the server can call on a connected client.
/// </summary>
/// <remarks>
/// A typed interface, so the broadcaster calls <c>TrustUpdated(update)</c> and the compiler checks the payload,
/// instead of <c>SendAsync("TrustUpdated", update)</c> where a wrong name or a wrong type is found by a client that
/// never receives anything. The member names are the wire names; a test compares them with
/// <see cref="RealtimeMethods"/>, which the client uses, so the two ends cannot drift.
/// </remarks>
public interface ITelemetryClient
{
    /// <summary>Nodes whose trust changed, each at its latest state. Coalesced.</summary>
    Task TrustUpdated(TrustUpdate update);

    /// <summary>Alerts that appeared, changed or ended.</summary>
    Task AlertsUpdated(AlertUpdate update);

    /// <summary>A reading crossed a critical threshold. Never batched.</summary>
    Task PriorityAlerted(PriorityAlert alert);

    /// <summary>A command changed state.</summary>
    Task CommandChanged(CommandUpdate update);

    /// <summary>Ingestion pipeline statistics.</summary>
    Task PipelineUpdated(PipelineUpdate update);

    /// <summary>Readings accepted since the last flush, as one message.</summary>
    Task ReadingsArrived(ReadingsBatch batch);
}
