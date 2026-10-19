using Microsoft.Extensions.Options;
using SmartX.Api.History;
using SmartX.Api.Search;
using SmartX.Shared;

namespace SmartX.Api.Alerts;

/// <summary>What happened when a condition fired.</summary>
internal enum RaiseOutcome
{
    /// <summary>The alert was not active, so it is now.</summary>
    Raised,

    /// <summary>The alert was already active. It was counted and not raised again.</summary>
    Suppressed
}

/// <summary>What changed about an alert.</summary>
internal enum AlertChangeKind
{
    /// <summary>The alert was not active and now is.</summary>
    Raised,

    /// <summary>The condition fired again while the alert was already active. Counted, not raised.</summary>
    Suppressed,

    /// <summary>An operator acknowledged the alert.</summary>
    Acknowledged,

    /// <summary>The alert's condition ended.</summary>
    Cleared
}

/// <summary>An alert changing, as announced by <see cref="AlertTracker.Changed"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Key">Which alert. A consumer that wants its current state asks the tracker for it.</param>
internal readonly record struct AlertChange(AlertChangeKind Kind, AlertKey Key);

/// <summary>What happened when an operator acknowledged an alert.</summary>
internal enum AcknowledgeOutcome
{
    /// <summary>The alert is now acknowledged.</summary>
    Acknowledged,

    /// <summary>It was already acknowledged. Nothing changed.</summary>
    AlreadyAcknowledged,

    /// <summary>There is no such active alert, so there is nothing to acknowledge.</summary>
    NotActive
}

/// <summary>
/// The set of alerts that are on right now, and the rules that stop one problem from being reported a thousand times.
/// </summary>
/// <remarks>
/// <para>
/// <b>Duplicate prevention is a set.</b> A node that stays disconnected is "disconnected" on every sweep, and one that keeps
/// sending out-of-range readings is out of range on every packet. A firing condition asks a <see cref="HashSet{T}"/> to
/// <see cref="HashSet{T}.Add"/> its <see cref="AlertKey"/>: <c>true</c> is a new alert, <c>false</c> is a repeat, which is counted
/// and nothing more. The count ("Disconnected on MOIST-02, suppressed ×14") is what makes the prevention visible.
/// </para>
/// <para>
/// <b>The sets.</b> <c>_active</c> is the alerts that are on. <c>_acknowledged</c> is the ones an operator has seen;
/// acknowledging never clears the condition. <c>_disconnected</c> is the nodes the heartbeat monitor considers silent. When a
/// condition clears its key leaves the set, so the next occurrence is a new alert.
/// </para>
/// <para>
/// <b>One lock, borrowed.</b> Everything runs inside the device index's lock, so an alert becoming active and its device becoming
/// findable by it are one step; a second lock would let a search see one without the other. Fault alerts come from the trust
/// engine's own events and the heartbeat monitor decides what is silent: this class holds the consequences and recomputes neither.
/// </para>
/// </remarks>
internal sealed class AlertTracker(
    DeviceIndex index,
    IHistoryStore history,
    ArrivalSequence arrival,
    IOptions<AlertOptions> options,
    TimeProvider time)
{
    private readonly AlertOptions _options = options.Value;

    private readonly HashSet<AlertKey> _active = [];
    private readonly Dictionary<AlertKey, AlertDetail> _details = [];
    private readonly HashSet<AlertKey> _acknowledged = [];
    private readonly HashSet<string> _disconnected = new(StringComparer.Ordinal);

    // Consecutive valid readings since an out-of-range or wrong-type alert last fired, for nodes that have one.
    private readonly Dictionary<string, int> _validStreak = new(StringComparer.Ordinal);

    private long _raised;
    private long _suppressed;
    private long _cleared;

    private Lock Gate => index.Gate;

    /// <summary>
    /// Raised whenever an alert is raised, repeated, acknowledged or cleared.
    /// </summary>
    /// <remarks>
    /// A domain event, so whatever shows alerts live can follow them without this class knowing it exists. It is
    /// raised <i>inside</i> the tracker's lock, which is what keeps the events in the order the changes were
    /// made: two threads cannot interleave and announce a clear before the raise it clears. The price is that a
    /// handler must be quick and must never call back into the tracker's lock-taking members from another
    /// thread; the realtime broadcaster only queues the change and returns. A handler that throws is ignored, so
    /// a faulty subscriber can never break alerting.
    /// </remarks>
    public event Action<AlertChange>? Changed;

    private void Announce(AlertChangeKind kind, AlertKey key)
    {
        var handlers = Changed;

        if (handlers is null)
        {
            return;
        }

        var change = new AlertChange(kind, key);

        foreach (Action<AlertChange> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch
            {
                // Deliberately swallowed. See the remarks on Changed.
            }
        }
    }

    /// <summary>What an active alert remembers besides its key.</summary>
    private sealed class AlertDetail(DateTimeOffset raisedAt, string? detail)
    {
        public DateTimeOffset RaisedAt { get; } = raisedAt;

        public long Suppressed { get; set; }

        public string? Detail { get; set; } = detail;
    }

    // ---- the condition says it is firing ---------------------------------------------------------------

    /// <summary>A condition has fired. Raises the alert if it is new, and counts it if it is a repeat.</summary>
    /// <remarks>
    /// For every kind except <see cref="AlertKind.Disconnected"/>, which has its own entry points
    /// (<see cref="NodeSilent"/> and <see cref="NodeHeard"/>) so that the disconnected set and the alert cannot
    /// be changed separately.
    /// </remarks>
    public RaiseOutcome Raise(AlertKey key, string? detail = null)
    {
        lock (Gate)
        {
            return RaiseLocked(key, detail);
        }
    }

    private RaiseOutcome RaiseLocked(AlertKey key, string? detail)
    {
        // The check and the decision are one call. Add returns false when the key is already in the set, which
        // is exactly "this is a repeat", so there is no separate Contains that another thread could race.
        if (!_active.Add(key))
        {
            var existing = _details[key];
            existing.Suppressed++;
            existing.Detail = detail ?? existing.Detail;
            _suppressed++;
            Announce(AlertChangeKind.Suppressed, key);

            return RaiseOutcome.Suppressed;
        }

        var now = time.GetUtcNow();

        _details.Add(key, new AlertDetail(now, detail));
        _raised++;
        index.SetAlertLocked(key.Mac, key.Kind, active: true);

        history.Append(HistoryEntry.FromEvent(
            arrival.Next(),
            now,
            key.Mac,
            HistoryKind.AlertRaised,
            new HistoryEventDetails($"{key.Kind} raised", detail, Alert: key.Kind)));

        Announce(AlertChangeKind.Raised, key);

        return RaiseOutcome.Raised;
    }

    /// <summary>The condition has ended. Removes the alert, so the next occurrence raises it again.</summary>
    /// <returns><see langword="false"/> when the alert was not active.</returns>
    public bool Clear(AlertKey key)
    {
        lock (Gate)
        {
            return ClearLocked(key);
        }
    }

    private bool ClearLocked(AlertKey key)
    {
        if (!_active.Remove(key))
        {
            return false;
        }

        _details.Remove(key);

        // Acknowledgement belongs to one occurrence. The next time this alert is raised an operator has not yet
        // seen it, so the old acknowledgement must not carry over.
        _acknowledged.Remove(key);

        if (key.Kind == AlertKind.Disconnected)
        {
            _disconnected.Remove(key.Mac);
        }

        _cleared++;
        index.SetAlertLocked(key.Mac, key.Kind, active: false);

        history.Append(HistoryEntry.FromEvent(
            arrival.Next(),
            time.GetUtcNow(),
            key.Mac,
            HistoryKind.AlertCleared,
            new HistoryEventDetails($"{key.Kind} cleared", Alert: key.Kind)));

        Announce(AlertChangeKind.Cleared, key);

        return true;
    }

    // ---- the heartbeat monitor -------------------------------------------------------------------------

    /// <summary>The heartbeat monitor found the node silent. Called on every sweep while it stays silent.</summary>
    /// <returns><see langword="true"/> when the node was not already considered disconnected.</returns>
    public bool NodeSilent(string macAddress, string detail)
    {
        lock (Gate)
        {
            // Same shape as the alert set: Add says whether this is news. The two sets stay in step, so the
            // monitor can reason in terms of connectivity while operators see it as an alert.
            var isNew = _disconnected.Add(macAddress);
            RaiseLocked(new AlertKey(macAddress, AlertKind.Disconnected), detail);

            return isNew;
        }
    }

    /// <summary>A node is reporting, so it is connected. Ends a Disconnected alert if there was one.</summary>
    public void NodeHeard(string macAddress)
    {
        lock (Gate)
        {
            NodeHeardLocked(macAddress);
        }
    }

    private void NodeHeardLocked(string macAddress)
    {
        if (_disconnected.Remove(macAddress))
        {
            ClearLocked(new AlertKey(macAddress, AlertKind.Disconnected));
        }
    }

    /// <summary>The nodes the heartbeat monitor currently considers disconnected.</summary>
    public IReadOnlyCollection<string> DisconnectedNodes
    {
        get
        {
            lock (Gate)
            {
                return [.. _disconnected.Order(StringComparer.Ordinal)];
            }
        }
    }

    // ---- readings --------------------------------------------------------------------------------------

    /// <summary>A reading was refused for being out of range or the wrong type.</summary>
    public RaiseOutcome ReadingRejected(string macAddress, TrustOutcome outcome, string reason)
    {
        var kind = outcome == TrustOutcome.SchemaFailure ? AlertKind.TypeViolation : AlertKind.OutOfRange;

        lock (Gate)
        {
            // Another bad reading restarts the count towards clearing: the node is still doing it.
            _validStreak[macAddress] = 0;

            return RaiseLocked(new AlertKey(macAddress, kind), reason);
        }
    }

    /// <summary>A valid reading was accepted from the node.</summary>
    /// <remarks>
    /// It proves the node is connected straight away, and counts towards ending an out-of-range or wrong-type
    /// alert. The streak is only tracked for nodes that have such an alert, so the common case — a healthy node
    /// — costs two failed lookups under the lock.
    /// </remarks>
    public void ReadingAccepted(string macAddress)
    {
        lock (Gate)
        {
            NodeHeardLocked(macAddress);

            if (_validStreak.Count == 0 || !_validStreak.TryGetValue(macAddress, out var streak))
            {
                return;
            }

            streak++;

            if (streak < _options.ClearAfterValidReadings)
            {
                _validStreak[macAddress] = streak;

                return;
            }

            _validStreak.Remove(macAddress);
            ClearLocked(new AlertKey(macAddress, AlertKind.OutOfRange));
            ClearLocked(new AlertKey(macAddress, AlertKind.TypeViolation));
        }
    }

    // ---- the operator ----------------------------------------------------------------------------------

    /// <summary>An operator has seen an alert. The condition itself is untouched.</summary>
    public AcknowledgeOutcome Acknowledge(AlertKey key)
    {
        lock (Gate)
        {
            if (!_active.Contains(key))
            {
                return AcknowledgeOutcome.NotActive;
            }

            if (!_acknowledged.Add(key))
            {
                return AcknowledgeOutcome.AlreadyAcknowledged;
            }

            Announce(AlertChangeKind.Acknowledged, key);

            return AcknowledgeOutcome.Acknowledged;
        }
    }

    // ---- reading the set -------------------------------------------------------------------------------

    /// <summary>The alerts that are on, oldest first.</summary>
    public IReadOnlyList<ActiveAlert> Active()
    {
        lock (Gate)
        {
            return
            [
                .. _active
                    .Select(key =>
                    {
                        var detail = _details[key];

                        return new ActiveAlert(
                            key,
                            index.RoomOfLocked(key.Mac),
                            detail.RaisedAt,
                            detail.Suppressed,
                            _acknowledged.Contains(key),
                            detail.Detail);
                    })
                    .OrderBy(alert => alert.RaisedAt)
                    .ThenBy(alert => alert.Key.Mac, StringComparer.Ordinal)
                    .ThenBy(alert => alert.Key.Kind),
            ];
        }
    }

    /// <summary>One active alert as it is now.</summary>
    /// <returns><see langword="false"/> when the alert is not active.</returns>
    public bool TryGet(AlertKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ActiveAlert? alert)
    {
        lock (Gate)
        {
            if (!_active.Contains(key))
            {
                alert = null;

                return false;
            }

            var detail = _details[key];

            alert = new ActiveAlert(
                key,
                index.RoomOfLocked(key.Mac),
                detail.RaisedAt,
                detail.Suppressed,
                _acknowledged.Contains(key),
                detail.Detail);

            return true;
        }
    }

    /// <summary>
    /// The keys of every active alert, in a stable order. Recorded alongside commands and searches as the state of
    /// the mesh at that moment.
    /// </summary>
    public IReadOnlyList<AlertKey> ActiveKeys()
    {
        lock (Gate)
        {
            return
            [
                .. _active
                    .OrderBy(key => key.Mac, StringComparer.Ordinal)
                    .ThenBy(key => key.Kind),
            ];
        }
    }

    /// <summary>Running totals since start-up.</summary>
    public AlertStats Stats()
    {
        lock (Gate)
        {
            return new AlertStats(_raised, _suppressed, _cleared, _active.Count, _acknowledged.Count);
        }
    }

    /// <summary>
    /// Whether the two sets that describe disconnection agree. For tests: the invariant is that a node is in the
    /// disconnected set exactly when it has an active Disconnected alert.
    /// </summary>
    internal bool DisconnectedSetMatchesAlerts()
    {
        lock (Gate)
        {
            var fromAlerts = _active.Where(key => key.Kind == AlertKind.Disconnected).Select(key => key.Mac).ToHashSet(StringComparer.Ordinal);

            return fromAlerts.SetEquals(_disconnected);
        }
    }
}
