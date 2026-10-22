using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.Ingestion;
using SmartX.Api.Realtime;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Snapshots for the real-time topics: the current state, with the sequence number it is as of.
/// </summary>
/// <remarks>
/// <b>Why snapshots exist alongside the hub.</b> A push that happens while a client's connection is down is lost,
/// and reconnecting does not bring it back. So a client does not rely on pushes alone: on every connect and every
/// reconnect it joins the topic, then fetches one of these, then applies pushes that are newer than the snapshot.
/// <para>
/// <b>The order inside each handler matters.</b> The sequence number is read <i>first</i>, then the data. The
/// broadcaster reads a message's content before it numbers it, so any message numbered at or below the sequence read
/// here was built from state that is already in the data read afterwards. If the data were read first, a message
/// could be numbered between the two reads: the snapshot would claim to include it and not contain it, and the
/// client, trusting the number, would discard the very message that carried the change.
/// </para>
/// </remarks>
internal static class RealtimeEndpoints
{
    internal static RouteGroupBuilder MapRealtimeEndpoints(this RouteGroupBuilder api)
    {
        var realtime = api.MapGroup("/realtime");

        realtime.MapGet("/trust", Trust).WithName("GetTrustSnapshot");
        realtime.MapGet("/alerts", Alerts).WithName("GetAlertSnapshot");
        realtime.MapGet("/commands", Commands).WithName("GetCommandSnapshot");
        realtime.MapGet("/pipeline", Pipeline).WithName("GetPipelineSnapshot");
        realtime.MapGet("/stream", Stream).WithName("GetStreamSnapshot");
        realtime.MapGet("/suggestions", Suggestions).WithName("GetSuggestionsSnapshot");
        realtime.MapGet("/topology", TopologySnapshot).WithName("GetTopologySnapshotMessage");
        realtime.MapGet("/device/{mac}", DeviceAsync).WithName("GetDeviceSnapshot");

        return api;
    }

    private static IResult Trust(RealtimeBroadcaster broadcaster, ITrustEngine trust)
    {
        var sequence = broadcaster.SequenceOf(RealtimeTopics.Trust);

        return Results.Ok(new TrustSnapshot(sequence, trust.Summary(), trust.Snapshot()));
    }

    private static IResult Alerts(RealtimeBroadcaster broadcaster, AlertTracker alerts)
    {
        // The sequence and the recent critical readings are read as one pair; the alerts themselves after.
        var (sequence, recent) = broadcaster.ReadPriorityAlerts();

        return Results.Ok(new AlertSnapshot(sequence, alerts.Active(), alerts.Stats(), recent));
    }

    private static IResult Commands(RealtimeBroadcaster broadcaster, ICommandStore store, IOptions<RealtimeOptions> options)
    {
        var sequence = broadcaster.SequenceOf(RealtimeTopics.Commands);

        return Results.Ok(new CommandSnapshot(sequence, store.List(null, null, options.Value.SnapshotCommands)));
    }

    private static IResult Pipeline(RealtimeBroadcaster broadcaster, TelemetryIngestionQueue queue)
    {
        var sequence = broadcaster.SequenceOf(RealtimeTopics.Pipeline);

        return Results.Ok(new PipelineUpdate(sequence, queue.Stats()));
    }

    private static IResult Stream(RealtimeBroadcaster broadcaster) => Results.Ok(broadcaster.GetStreamSnapshot());

    private static IResult Suggestions(RealtimeBroadcaster broadcaster) => Results.Ok(broadcaster.GetSuggestionsSnapshot());

    private static IResult TopologySnapshot(RealtimeBroadcaster broadcaster) => Results.Ok(broadcaster.GetTopologySnapshot());

    /// <summary>One device's recent readings with the sequence of its <c>device:{mac}</c> topic.</summary>
    /// <remarks>
    /// The sequence is read before the readings, as everywhere else. The worker stores a reading before it announces
    /// it, so any batch numbered at or below the sequence read here is already in what is read from the store.
    /// </remarks>
    private static async Task<IResult> DeviceAsync(
        string mac,
        RealtimeBroadcaster broadcaster,
        ISensorRegistry registry,
        ITelemetryStore store,
        CancellationToken cancellationToken)
    {
        if (await registry.FindAsync(mac, cancellationToken) is not { } sensor)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{mac}'.");
        }

        var sequence = broadcaster.SequenceOf(RealtimeTopics.Device(sensor.MacAddress));
        var recent = await store.RecentAsync(sensor.MacAddress, cancellationToken);

        return Results.Ok(new DeviceSnapshot(
            sequence,
            sensor.MacAddress,
            [.. recent.Select(packet => ReadingPush.From(packet, packet.Timestamp))]));
    }
}
