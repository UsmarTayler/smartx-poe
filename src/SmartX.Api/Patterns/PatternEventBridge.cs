using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.Ingestion;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>
/// Tells the pattern engine what the rest of the system is doing: the triggers an operator might respond to, and the commands
/// an operator issues.
/// </summary>
/// <remarks>
/// The same shape as <see cref="TrustEventBridge"/>: the domain raises events and knows nothing of what listens, and one
/// class subscribes and forwards. The engine stays testable without any of them.
/// <para>
/// <b>Triggers</b> come from four places: a reading crossing a watch level or starting a critical episode (from the
/// ingestion worker's processed-reading event), an alert being raised, and a node's trust moving into Degrading or Fault.
/// A Fault alert is not forwarded as well, because the band change into Fault that raised it already is the trigger, and
/// counting one incident under two keys would double its weight.
/// </para>
/// <para>
/// <b>Commands</b> are forwarded when an operator issues them. A restore command made by an undo is not: it is the system
/// carrying out the operator's undo, not a new decision, and counting it would teach the engine that closing a valve is
/// followed by opening it.
/// </para>
/// <para>
/// The processed-reading handler runs on the ingestion worker's thread for every reading, so it does a few dictionary lookups
/// and returns. The zone tree is fetched once at start and handed to the device directory.
/// </para>
/// </remarks>
internal sealed class PatternEventBridge(
    ProcessedReadings readings,
    AlertTracker alerts,
    ITrustEngine trust,
    CommandService commands,
    ReadingTriggerDetector detector,
    IPatternEngine patterns,
    DeviceDirectory directory,
    IZoneStore zones,
    TimeProvider time) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        directory.Catalogue = await zones.GetCatalogueAsync(cancellationToken);

        readings.Processed += OnProcessed;
        alerts.Changed += OnAlertChanged;
        trust.BandChanged += OnBandChanged;
        commands.Changed += OnCommandChanged;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        readings.Processed -= OnProcessed;
        alerts.Changed -= OnAlertChanged;
        trust.BandChanged -= OnBandChanged;
        commands.Changed -= OnCommandChanged;

        return Task.CompletedTask;
    }

    private void OnProcessed(ProcessedReading reading)
    {
        var mac = reading.Packet.NodeId;
        var now = time.GetUtcNow();

        if (detector.ObserveCritical(mac, reading.Alert?.Reason, now) is { } critical)
        {
            patterns.RecordTrigger(critical);
        }

        // A state is on or off, and has no level to cross.
        if (reading.Packet is not StatePacket && reading.Packet.TryGetNumericValue(out var value)
            && detector.ObserveReading(mac, value, now) is { } crossing)
        {
            patterns.RecordTrigger(crossing);
        }
    }

    private void OnAlertChanged(AlertChange change)
    {
        if (change.Kind == AlertChangeKind.Raised && change.Key.Kind != AlertKind.Fault)
        {
            patterns.RecordTrigger(new TriggerEvent(TriggerKind.AlertRaised, change.Key.Mac, change.Key.Kind.ToString(), time.GetUtcNow()));
        }
    }

    private void OnBandChanged(TrustTransition transition)
    {
        if (transition.Band is TrustBand.Degrading or TrustBand.Fault)
        {
            patterns.RecordTrigger(new TriggerEvent(TriggerKind.TrustBand, transition.MacAddress, transition.Band.ToString(), transition.WindowEnd));
        }
    }

    private void OnCommandChanged(Command command, CommandEvent change)
    {
        if (change == CommandEvent.Issued && !command.IsRestore)
        {
            patterns.RecordAction(new OperatorAction(OperatorActionKind.Command, command.MacAddress, command.Action.ToString(), time.GetUtcNow()));
        }
    }
}
