using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// What a history entry records. One entry type carries all of these, told apart by this discriminator.
/// </summary>
/// <remarks>
/// One store and one entry type rather than four stores, because every question asked of history is the same
/// question: what happened to this node, or to the mesh, between two moments. Four stores would need four
/// retention rules, four eviction passes and a merge to answer "show me everything about this pump".
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<HistoryKind>))]
public enum HistoryKind
{
    /// <summary>An accepted telemetry reading.</summary>
    Reading,

    /// <summary>Something happened to a command: issued, delivered, acknowledged, timed out, undone.</summary>
    Command,

    /// <summary>An alert condition began and the alert was raised.</summary>
    AlertRaised,

    /// <summary>The alert condition ended and the alert was cleared.</summary>
    AlertCleared,

    /// <summary>A node's trust moved from one band to another.</summary>
    TrustTransition,

    /// <summary>An operator searched for devices.</summary>
    Search,

    /// <summary>An operator opened a device in the Command Stream.</summary>
    Selection
}
