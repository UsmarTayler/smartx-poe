using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Realtime;

/// <summary>
/// The one real-time endpoint. A client connects once and joins the topics it wants.
/// </summary>
/// <remarks>
/// <b>Groups are the subscriptions.</b> Joining <c>trust</c> puts the connection in a SignalR group of that name,
/// and the broadcaster sends trust messages to that group, so a client receives only what it joined. Nothing is
/// filtered on the client: a page that is not showing alerts never has an alert delivered to it, which at scale is
/// the difference between a dashboard and a firehose.
/// <para>
/// The hub does nothing else. It does not read the trust engine, the alert set or the queues, and it sends
/// nothing. All pushing is the broadcaster's, from domain events; the hub is only where clients say what they
/// want. That keeps the transport out of the domain: the trust engine, the alert set, the command service and the
/// worker do not reference SignalR, and the hub, which does, depends on none of them.
/// </para>
/// <para>
/// Joining does not return anything. A client that has joined fetches a snapshot over HTTP and uses the sequence
/// number in it to decide which pushes are new; see <see cref="IRealtimeMessage"/>.
/// </para>
/// </remarks>
internal sealed class TelemetryHub(RealtimeSubscriptions subscriptions, ISensorRegistry registry) : Hub<ITelemetryClient>
{
    /// <summary>Joins a topic: one of the fixed topics, or <c>device:{mac}</c> for one device.</summary>
    /// <exception cref="HubException">The topic is not one the hub offers, or names a device that is not registered.</exception>
    public async Task Subscribe(string topic)
    {
        var canonical = await CanonicalAsync(topic);

        // Recorded first. The broadcaster starts keeping what this topic needs from the moment someone is
        // listening, and the client's snapshot is only fetched after this call returns, so nothing that happens
        // between joining and snapshotting can fall in a gap.
        subscriptions.Join(Context.ConnectionId, canonical);

        await Groups.AddToGroupAsync(Context.ConnectionId, canonical);
    }

    /// <summary>Leaves a topic. Leaving one that was never joined does nothing.</summary>
    public async Task Unsubscribe(string topic)
    {
        var canonical = await CanonicalAsync(topic, mustBeRegistered: false);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, canonical);

        subscriptions.Leave(Context.ConnectionId, canonical);
    }

    /// <summary>
    /// The transport this connection actually negotiated: <c>WebSockets</c>, <c>ServerSentEvents</c> or
    /// <c>LongPolling</c>.
    /// </summary>
    /// <remarks>
    /// Asked for because a proxy that does not pass WebSocket upgrades does not break SignalR. It silently
    /// downgrades it to long polling, which works, and nobody notices that the "push" is a request held open and
    /// repeated. Making the transport something a client can read is how that gets noticed.
    /// </remarks>
    public string GetTransport() =>
        Context.Features.Get<IHttpTransportFeature>()?.TransportType.ToString() ?? "Unknown";

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR removes the connection from its groups itself. The subscription counts are this class's own.
        subscriptions.RemoveConnection(Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }

    private async Task<string> CanonicalAsync(string topic, bool mustBeRegistered = true)
    {
        if (RealtimeTopics.Fixed.Contains(topic, StringComparer.Ordinal))
        {
            return topic;
        }

        if (RealtimeTopics.IsDevice(topic))
        {
            var mac = topic[RealtimeTopics.DevicePrefix.Length..];

            if (!MacAddressValidator.TryNormalise(mac, out var canonicalMac))
            {
                throw new HubException($"'{mac}' is not a valid MAC address.");
            }

            // Canonical, so "device:aa-bb-..." and "device:AA:BB:..." are the same group.
            if (mustBeRegistered && await registry.FindAsync(canonicalMac) is null)
            {
                throw new HubException($"No sensor is registered with MAC address '{canonicalMac}'.");
            }

            return RealtimeTopics.Device(canonicalMac);
        }

        throw new HubException(
            $"'{topic}' is not a topic. Choose one of: {string.Join(", ", RealtimeTopics.Fixed)}, or {RealtimeTopics.DevicePrefix}{{mac}}.");
    }
}
