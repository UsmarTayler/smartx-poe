namespace SmartX.Shared;

/// <summary>
/// The topics a client can ask the hub for. Each is a SignalR group: a client receives a topic's messages only
/// if it joined that topic.
/// </summary>
/// <remarks>
/// One place for the names, used by the hub, the broadcaster and the client, so a typo is a compile error and
/// not a client that quietly never hears anything.
/// </remarks>
public static class RealtimeTopics
{
    /// <summary>Where the hub is mapped. The client builds its connection from this.</summary>
    public const string HubPath = "/hubs/telemetry";

    /// <summary>Trust scores and bands. Coalesced.</summary>
    public const string Trust = "trust";

    /// <summary>Alerts raised, cleared and acknowledged, and critical readings. Immediate.</summary>
    public const string Alerts = "alerts";

    /// <summary>Command state changes. Immediate.</summary>
    public const string Commands = "commands";

    /// <summary>Ingestion queue statistics. Periodic.</summary>
    public const string Pipeline = "pipeline";

    /// <summary>Every accepted reading across the mesh. Batched.</summary>
    public const string Stream = "stream";

    /// <summary>Suggested actions and automated insights. Immediate when something new appears, and never per reading.</summary>
    public const string Suggestions = "suggestions";

    /// <summary>The mesh: a new revision of the topology, and the route changes since the last message. Immediate, coalesced.</summary>
    public const string Topology = "topology";

    /// <summary>The prefix of a per-device topic, followed by the device's MAC address.</summary>
    public const string DevicePrefix = "device:";

    /// <summary>The topic for one device's readings, for a timeline view.</summary>
    public static string Device(string macAddress) => DevicePrefix + macAddress;

    /// <summary>Whether a topic is a per-device topic.</summary>
    public static bool IsDevice(string topic) => topic.StartsWith(DevicePrefix, StringComparison.Ordinal);

    /// <summary>The topics every client may join by name.</summary>
    public static IReadOnlyList<string> Fixed { get; } = [Trust, Alerts, Commands, Pipeline, Stream, Suggestions, Topology];
}

/// <summary>
/// The names of the methods the server calls on a client, and the client calls on the server.
/// </summary>
/// <remarks>
/// The server side declares these as a typed interface, and a test checks the interface's member names against
/// these constants, so the two cannot drift. A mismatch would not fail loudly: SignalR simply finds no handler
/// of that name and drops the message.
/// </remarks>
public static class RealtimeMethods
{
    // Server to client.
    public const string TrustUpdated = nameof(TrustUpdated);
    public const string AlertsUpdated = nameof(AlertsUpdated);
    public const string PriorityAlerted = nameof(PriorityAlerted);
    public const string CommandChanged = nameof(CommandChanged);
    public const string PipelineUpdated = nameof(PipelineUpdated);
    public const string ReadingsArrived = nameof(ReadingsArrived);
    public const string SuggestionsUpdated = nameof(SuggestionsUpdated);
    public const string TopologyUpdated = nameof(TopologyUpdated);

    // Client to server.
    public const string Subscribe = nameof(Subscribe);
    public const string Unsubscribe = nameof(Unsubscribe);
    public const string GetTransport = nameof(GetTransport);
}

/// <summary>
/// A message that carries a sequence number.
/// </summary>
/// <remarks>
/// <b>Why every message is numbered.</b> A push that happens while the connection is down is lost, and
/// reconnecting alone does not bring it back. A client therefore subscribes, fetches a snapshot over HTTP, and
/// keeps pushing from there. The snapshot is taken at one moment and pushes keep flowing, so a message sent a
/// moment before the snapshot can arrive a moment after it. Applying it would overwrite newer state with older.
/// The sequence number is how the client tells: the snapshot says what sequence it is as of, and a pushed
/// message at or below that is already in the snapshot and is discarded.
/// <para>
/// Sequences are per topic, strictly increasing, and assigned by the one component that sends. Messages of one
/// topic therefore have a total order that does not depend on which thread raised the event.
/// </para>
/// </remarks>
public interface IRealtimeMessage
{
    /// <summary>The message's place in its topic's order.</summary>
    long Sequence { get; }
}

/// <summary>Every node's trust, as of a sequence number. Replaces what a client holds.</summary>
/// <param name="Sequence">The topic's sequence when the snapshot was taken.</param>
/// <param name="Summary">Mesh-wide confidence and per-band counts.</param>
/// <param name="Nodes">Every tracked node, worst first.</param>
public sealed record TrustSnapshot(long Sequence, TrustSummary Summary, IReadOnlyList<NodeTrustSnapshot> Nodes) : IRealtimeMessage;

/// <summary>The nodes whose trust changed since the last update, each at its latest state.</summary>
/// <param name="Sequence">Its place in the trust topic's order.</param>
/// <param name="Summary">The mesh summary at the moment of sending.</param>
/// <param name="Nodes">Only the nodes that changed. A client merges them into what it holds by MAC address.</param>
/// <remarks>
/// <b>Coalesced.</b> If a node changes ten times inside one flush window, this carries it once, as it is now.
/// Ten messages would cost ten sends and ten re-renders to arrive at the same picture.
/// </remarks>
public sealed record TrustUpdate(long Sequence, TrustSummary Summary, IReadOnlyList<NodeTrustSnapshot> Nodes) : IRealtimeMessage;

/// <summary>Every active alert as of a sequence number, with the most recent critical readings.</summary>
/// <param name="Sequence">The alerts topic's sequence when the snapshot was taken.</param>
/// <param name="Alerts">The active alerts, oldest first.</param>
/// <param name="Stats">Raised, suppressed and cleared totals.</param>
/// <param name="RecentPriority">The latest critical readings, oldest first.</param>
public sealed record AlertSnapshot(
    long Sequence,
    IReadOnlyList<ActiveAlert> Alerts,
    AlertStats Stats,
    IReadOnlyList<PriorityAlert> RecentPriority) : IRealtimeMessage;

/// <summary>Alerts that appeared, changed or ended.</summary>
/// <param name="Sequence">Its place in the alerts topic's order.</param>
/// <param name="Upserted">Alerts raised, acknowledged, or whose repeat count moved, each as it is now.</param>
/// <param name="Cleared">Alerts that ended.</param>
/// <param name="Stats">The totals at the moment of sending.</param>
public sealed record AlertUpdate(
    long Sequence,
    IReadOnlyList<ActiveAlert> Upserted,
    IReadOnlyList<AlertKey> Cleared,
    AlertStats Stats) : IRealtimeMessage;

/// <summary>A reading that crossed a critical threshold, announced the moment it was processed.</summary>
/// <param name="Sequence">Its place in the alerts topic's order.</param>
/// <param name="MacAddress">The node.</param>
/// <param name="Reason">Why it is critical, from the rule that matched.</param>
/// <param name="Severity">How severe. Lower is more severe, as in the ingestion queue.</param>
/// <param name="Value">The reading.</param>
/// <param name="Unit">Its unit.</param>
/// <param name="Timestamp">When the device took it.</param>
/// <param name="AcceptedAt">When the API accepted it. The start of the clock for how fast it was announced.</param>
public sealed record PriorityAlert(
    long Sequence,
    string MacAddress,
    string Reason,
    int Severity,
    double Value,
    string Unit,
    DateTimeOffset Timestamp,
    DateTimeOffset AcceptedAt) : IRealtimeMessage;

/// <summary>Recent commands as of a sequence number.</summary>
/// <param name="Sequence">The commands topic's sequence when the snapshot was taken.</param>
/// <param name="Commands">The most recent commands, newest first.</param>
public sealed record CommandSnapshot(long Sequence, IReadOnlyList<Command> Commands) : IRealtimeMessage;

/// <summary>A command changed state.</summary>
/// <param name="Sequence">Its place in the commands topic's order.</param>
/// <param name="Command">The command as it is now.</param>
/// <param name="Event">What just happened to it.</param>
public sealed record CommandUpdate(long Sequence, Command Command, CommandEvent Event) : IRealtimeMessage;

/// <summary>The ingestion pipeline's statistics. Used both as a snapshot and as the periodic push.</summary>
/// <param name="Sequence">Its place in the pipeline topic's order.</param>
/// <param name="Stats">Depth, throughput and wait time per lane.</param>
public sealed record PipelineUpdate(long Sequence, PipelineStats Stats) : IRealtimeMessage;

/// <summary>One reading, as pushed to a client.</summary>
/// <param name="MacAddress">The node.</param>
/// <param name="Timestamp">When the device took it.</param>
/// <param name="Value">The reading as a number. A state is 1 for engaged and 0 for released.</param>
/// <param name="Unit">Its unit.</param>
/// <param name="IsState">Whether it is an on/off state and not a measurement.</param>
/// <param name="AcceptedAt">When the API accepted it, so a client can measure how long the push took.</param>
public readonly record struct ReadingPush(
    string MacAddress,
    DateTimeOffset Timestamp,
    double Value,
    string Unit,
    bool IsState,
    DateTimeOffset AcceptedAt)
{
    /// <summary>Describes a packet as a pushed reading. A state is 1 for engaged and 0 for released.</summary>
    public static ReadingPush From(ITelemetryPacket packet, DateTimeOffset acceptedAt)
    {
        var value = packet is StatePacket state
            ? state.Value ? 1d : 0d
            : packet.TryGetNumericValue(out var number) ? number : 0d;

        return new ReadingPush(packet.NodeId, packet.Timestamp, value, packet.Unit, packet is StatePacket, acceptedAt);
    }
}

/// <summary>Readings accepted since the last flush, sent as one message.</summary>
/// <param name="Sequence">Its place in its topic's order.</param>
/// <param name="Readings">The readings, in the order they were processed.</param>
/// <param name="Dropped">
/// How many readings the server discarded because more arrived in one window than it is willing to hold. Zero in
/// normal running. Reported so an overloaded stream says so instead of silently thinning.
/// </param>
/// <param name="Topic">
/// The topic this batch was sent on: <c>stream</c> for the whole mesh, or <c>device:{mac}</c> for one device. Both are
/// delivered by the same client method, so a client joined to both has to be able to tell them apart. Their sequence
/// numbers are separate, and a message applied to the wrong feed would corrupt that feed's ordering.
/// </param>
/// <remarks>
/// <b>Batched.</b> A message per reading would be thousands of sends a second at scale, each with its own framing
/// and its own client re-render. One message every flush window costs the same however many readings it holds.
/// </remarks>
public sealed record ReadingsBatch(
    long Sequence,
    IReadOnlyList<ReadingPush> Readings,
    int Dropped,
    string Topic = RealtimeTopics.Stream) : IRealtimeMessage;

/// <summary>One device's recent readings, as of a sequence number. The snapshot behind a <c>device:{mac}</c> topic.</summary>
/// <param name="Sequence">The device topic's sequence when the snapshot was taken.</param>
/// <param name="MacAddress">The device.</param>
/// <param name="Readings">Its most recent readings, oldest first.</param>
public sealed record DeviceSnapshot(long Sequence, string MacAddress, IReadOnlyList<ReadingPush> Readings) : IRealtimeMessage;

/// <summary>The latest reading from each node, as of a sequence number.</summary>
/// <param name="Sequence">The stream topic's sequence when the snapshot was taken.</param>
/// <param name="Latest">One reading per node that has reported.</param>
public sealed record StreamSnapshot(long Sequence, IReadOnlyList<ReadingPush> Latest) : IRealtimeMessage;

/// <summary>The mesh changed: which revision of it is current, and the routes that healed since the last message.</summary>
/// <param name="Sequence">Its place in the topology topic's order.</param>
/// <param name="Revision">
/// The revision of the mesh drawing. It moves when something a person would see changes (a device joins, leaves or changes band, a
/// link appears or goes, a link enters or leaves the spanning tree), not when a score drifts, so a client that refetches on a new
/// revision is not refetching all day.
/// </param>
/// <param name="BuiltAt">When that revision was built.</param>
/// <param name="NewChanges">Route changes recorded since the previous message, newest first. In a snapshot, the most recent ones.</param>
/// <remarks>
/// <b>Small on purpose.</b> It carries the revision number and not the mesh. The mesh is a hundred nodes and links, and sending it on
/// every change would be most of the traffic; a client that sees a new revision asks for it over HTTP, which is the same snapshot
/// endpoint it started from. The route changes ride along because they are tiny and are the news.
/// </remarks>
public sealed record TopologyUpdate(long Sequence, long Revision, DateTimeOffset BuiltAt, IReadOnlyList<RouteChangeView> NewChanges) : IRealtimeMessage;
