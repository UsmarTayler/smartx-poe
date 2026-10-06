using System.Diagnostics.CodeAnalysis;
using SmartX.Shared;

namespace SmartX.Api.Trust;

/// <summary>
/// Maintains a continuously recomputed trust score for every tracked node.
/// </summary>
/// <remarks>
/// An interface because three consumers will depend on it: the dashboard endpoints now, and the
/// predictive engine and routing weights later. Each of those should depend on what trust
/// <i>means</i>, not on how the current implementation stores it.
/// <para>
/// Synchronous, unlike the storage interfaces. Those are async because they will become database
/// calls; this is arithmetic over in-memory state under a lock held for microseconds. Wrapping it in
/// a <see cref="Task"/> would add an allocation to every packet on the ingestion path and promise
/// an asynchrony that does not exist.
/// </para>
/// </remarks>
public interface ITrustEngine
{
    /// <summary>
    /// Raised when a node's trust falls into the fault band from any band above it.
    /// </summary>
    /// <remarks>
    /// The escalation seam. Raised once per crossing, never once per packet while the node stays in
    /// fault. <see cref="Action{T}"/> rather than <see cref="EventHandler{TEventArgs}"/> because the
    /// transition carries everything a subscriber needs, and the conventional untyped sender
    /// parameter would add nothing but an <c>object</c>.
    /// </remarks>
    event Action<TrustTransition>? FaultEntered;

    /// <summary>Raised when a node in the fault band recovers into a band above it.</summary>
    /// <remarks>
    /// Not raised when a faulted node's evidence decays away entirely and it becomes unproven. That is
    /// the absence of information, not a recovery, and announcing it as one would clear an alert for a
    /// node nobody has repaired.
    /// </remarks>
    event Action<TrustTransition>? FaultCleared;

    /// <summary>Raised whenever a node moves from one trust band to another, whichever bands they are.</summary>
    /// <remarks>
    /// Broader than <see cref="FaultEntered"/> and <see cref="FaultCleared"/>, which announce only the fault
    /// boundary and are unchanged. This one is for consumers that follow every band: the history of transitions
    /// and the device search index. Raised once per change, after the engine's lock is released, and not raised
    /// when a node is first tracked, because that is a start and not a change.
    /// </remarks>
    event Action<TrustTransition>? BandChanged;

    /// <summary>The nodes currently degrading or faulted.</summary>
    IReadOnlyCollection<string> DegradedNodes { get; }

    /// <summary>Starts tracking a node, at the prior score, if it is not tracked already.</summary>
    void Track(string macAddress);

    /// <summary>Records one outcome for a node and recomputes its trust.</summary>
    void Record(string macAddress, TrustOutcome outcome);

    /// <summary>Records one outcome as observed at a given moment, and recomputes the node's trust.</summary>
    /// <remarks>
    /// For replaying history rather than live ingestion: the seeder uses it to backfill a node's
    /// evidence and sparkline with the timestamps the readings would have had. A moment in the future is
    /// treated as now, and replay is expected to run oldest first.
    /// </remarks>
    void Record(string macAddress, TrustOutcome outcome, DateTimeOffset observedAt);

    /// <summary>
    /// Applies a penalty for each whole reporting interval the node has been silent that has not
    /// already been penalised.
    /// </summary>
    /// <returns>How many new penalties were applied.</returns>
    int CheckHeartbeat(string macAddress, TimeSpan expectedInterval);

    /// <summary>
    /// Recomputes every node against the current time, so decay alone can move a score across a
    /// threshold.
    /// </summary>
    void Reevaluate();

    /// <summary>Every tracked node, worst score first.</summary>
    IReadOnlyList<NodeTrustSnapshot> Snapshot();

    /// <summary>One node's trust.</summary>
    /// <returns><see langword="false"/> when the node is not tracked.</returns>
    bool TryGet(string macAddress, [NotNullWhen(true)] out NodeTrustSnapshot? snapshot);

    /// <summary>One node's sampled trust series, oldest first.</summary>
    /// <returns><see langword="false"/> when the node is not tracked.</returns>
    bool TryGetHistory(string macAddress, [NotNullWhen(true)] out IReadOnlyList<TrustHistoryPoint>? history);

    /// <summary>Aggregate confidence across the mesh.</summary>
    TrustSummary Summary();
}
