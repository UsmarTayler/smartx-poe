using SmartX.Api.History;
using SmartX.Api.Search;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Alerts;

/// <summary>
/// Connects the trust engine's transitions to the things that follow them: the alert set, the device index and
/// the history.
/// </summary>
/// <remarks>
/// <b>Why a bridge and not a second mechanism.</b> The trust engine already decides when a node enters or
/// leaves a band, and already announces it once per crossing. Anything that wants to know — an alert for a node
/// that has faulted, a search for nodes that are degrading, a timeline marker where the band changed — should
/// listen to that announcement rather than recompute trust or poll for it. Recomputing would be a parallel
/// definition of "faulted" that could disagree with the real one. So this class subscribes and does nothing
/// else.
/// <para>
/// Two seams are used. <see cref="ITrustEngine.FaultEntered"/> and <see cref="ITrustEngine.FaultCleared"/> are
/// the existing escalation seam, and drive the Fault alert; their rule that decay to unproven is not a recovery
/// therefore applies to the alert as well, and a fault whose evidence has faded stays raised until something
/// real clears it. <see cref="ITrustEngine.BandChanged"/> fires for every change of band, and drives the index
/// and the history.
/// </para>
/// <para>
/// A hosted service so it is subscribed before the API takes requests, and unsubscribed on shutdown.
/// </para>
/// </remarks>
internal sealed class TrustEventBridge(
    ITrustEngine trust,
    DeviceIndex index,
    AlertTracker alerts,
    IHistoryStore history,
    ArrivalSequence arrival) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        trust.BandChanged += OnBandChanged;
        trust.FaultEntered += OnFaultEntered;
        trust.FaultCleared += OnFaultCleared;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        trust.BandChanged -= OnBandChanged;
        trust.FaultEntered -= OnFaultEntered;
        trust.FaultCleared -= OnFaultCleared;

        return Task.CompletedTask;
    }

    private void OnBandChanged(TrustTransition transition)
    {
        // Index first, so a search made the instant after the history shows the change already sees it.
        index.SetBand(transition.MacAddress, transition.Band);

        history.Append(HistoryEntry.FromEvent(
            arrival.Next(),
            transition.WindowEnd,
            transition.MacAddress,
            HistoryKind.TrustTransition,
            new HistoryEventDetails(
                $"Trust moved from {transition.PreviousBand} to {transition.Band}",
                transition.Reason,
                Band: transition.Band,
                PreviousBand: transition.PreviousBand)));
    }

    private void OnFaultEntered(TrustTransition transition) =>
        alerts.Raise(new AlertKey(transition.MacAddress, AlertKind.Fault), transition.Reason);

    private void OnFaultCleared(TrustTransition transition) =>
        alerts.Clear(new AlertKey(transition.MacAddress, AlertKind.Fault));
}
