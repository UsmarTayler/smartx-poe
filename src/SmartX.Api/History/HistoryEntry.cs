using SmartX.Shared;

namespace SmartX.Api.History;

/// <summary>
/// One thing that happened, to one node or to the mesh.
/// </summary>
/// <remarks>
/// One type for every kind of entry, told apart by <see cref="Kind"/>, so a single store and a single
/// retention rule cover everything. The two shapes of entry are very different in volume though, and the type
/// reflects it: a reading is the overwhelming majority of what is stored and carries only its value and unit,
/// while everything else — a command, an alert, a trust transition, a search — carries a
/// <see cref="HistoryEventDetails"/> that a reading leaves null. A reading therefore costs a handful of
/// fields rather than the fifteen an event needs.
/// </remarks>
internal sealed record HistoryEntry
{
    /// <summary>The scope used for entries that belong to the mesh rather than to a node, such as a search.</summary>
    public const string MeshScope = "mesh";

    /// <summary>Arrival number from <see cref="ArrivalSequence"/>. Orders entries that share a timestamp.</summary>
    public required long Sequence { get; init; }

    /// <summary>When it happened. For a reading, when the device took it.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The node it happened to, or <see cref="MeshScope"/>.</summary>
    public required string MacAddress { get; init; }

    /// <summary>What sort of entry this is.</summary>
    public required HistoryKind Kind { get; init; }

    /// <summary>For a reading, the value. A state is 1 for engaged and 0 for released.</summary>
    public double Value { get; init; }

    /// <summary>For a reading, its unit.</summary>
    public string? Unit { get; init; }

    /// <summary>Everything an event carries. Null for a reading.</summary>
    public HistoryEventDetails? Event { get; init; }

    /// <summary>The key this entry is stored under.</summary>
    public HistoryKey Key => new(Timestamp, Sequence);

    /// <summary>True for everything except a reading.</summary>
    public bool IsEvent => Kind != HistoryKind.Reading;

    /// <summary>A reading as it was accepted.</summary>
    public static HistoryEntry FromReading(long sequence, ITelemetryPacket packet)
    {
        // A state has no numeric value, so it is recorded as 1 or 0. The timeline reports a state sensor as
        // transitions, which only needs the two values to differ.
        var value = packet is StatePacket state
            ? state.Value ? 1d : 0d
            : packet.TryGetNumericValue(out var number) ? number : double.NaN;

        return new HistoryEntry
        {
            Sequence = sequence,
            Timestamp = packet.Timestamp,
            MacAddress = packet.NodeId,
            Kind = HistoryKind.Reading,
            Value = value,
            Unit = packet.Unit,
        };
    }

    /// <summary>An event of any non-reading kind.</summary>
    public static HistoryEntry FromEvent(
        long sequence,
        DateTimeOffset timestamp,
        string macAddress,
        HistoryKind kind,
        HistoryEventDetails details)
    {
        if (kind == HistoryKind.Reading)
        {
            throw new ArgumentException("A reading is not an event. Use FromReading.", nameof(kind));
        }

        return new HistoryEntry
        {
            Sequence = sequence,
            Timestamp = timestamp,
            MacAddress = macAddress,
            Kind = kind,
            Event = details,
        };
    }
}

/// <summary>What a non-reading history entry carries. Which members are set depends on the entry's kind.</summary>
/// <param name="Summary">A short phrase for a list: "ValveClose issued", "Disconnected raised".</param>
/// <param name="Detail">More, when there is more.</param>
/// <param name="CommandId">A command entry: which command.</param>
/// <param name="CommandEvent">A command entry: what happened to it.</param>
/// <param name="Band">
/// A command entry: the target's trust band at that moment. A trust transition: the band it entered.
/// </param>
/// <param name="PreviousBand">A trust transition: the band it left.</param>
/// <param name="Alert">An alert entry: which kind.</param>
/// <param name="ActiveAlerts">
/// A command or search entry: the alerts active across the mesh at that moment. Recorded at the time because it
/// cannot be reconstructed afterwards.
/// </param>
/// <param name="Filters">A search entry: what was asked for.</param>
/// <param name="ResultCount">A search entry: how many devices matched.</param>
internal sealed record HistoryEventDetails(
    string Summary,
    string? Detail = null,
    Guid? CommandId = null,
    CommandEvent? CommandEvent = null,
    TrustBand? Band = null,
    TrustBand? PreviousBand = null,
    AlertKind? Alert = null,
    IReadOnlyList<AlertKey>? ActiveAlerts = null,
    DeviceSearchFilters? Filters = null,
    int? ResultCount = null);
