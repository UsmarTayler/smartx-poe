using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Trust;

/// <summary>
/// Computes and maintains a recency-weighted trust score for every tracked node.
/// </summary>
/// <remarks>
/// <b>One lock, deliberately.</b> Every update touches three things that must agree: the node's own
/// state, the node dictionary, and the set of degraded nodes. A <c>ConcurrentDictionary</c> would
/// make each of those individually safe but could not make the change across all three atomic — a
/// reader could see a node in the fault band that the degraded set did not yet contain. A single
/// short lock makes each update all-or-nothing. It is held only for arithmetic over one node's
/// window; nothing inside it waits on I/O.
/// <para>
/// <b>Events are raised after the lock is released.</b> Transitions are detected inside the lock,
/// where the previous band can be compared and replaced atomically, then raised once it is free. A
/// subscriber that called back into the engine while the lock was held would otherwise be waiting
/// on the thread that is waiting on it.
/// </para>
/// </remarks>
internal sealed class TrustEngine : ITrustEngine
{
    // One slot per TrustOutcome member, so per-kind tallies fit in a stack-allocated span indexed by
    // the enum value rather than a dictionary allocated on every evaluation.
    private const int OutcomeKinds = 4;

    private readonly Lock _gate = new();

    // Keyed by the node's canonical MAC because the MAC is what every arriving packet carries. The
    // lookup happens on every single packet, so it has to be O(1): a hash of the key finds the
    // bucket directly, whether ten nodes are tracked or ten thousand. Scanning a list for the node
    // would make ingestion cost grow with the size of the mesh.
    private readonly Dictionary<string, NodeTrust> _nodes = new(StringComparer.Ordinal);

    // Nodes currently degrading or faulted. A set holds each member at most once, so a node that
    // fails on every packet for an hour occupies one entry, not one per failure — which is what
    // stops the alerting built on top of this from processing the same node thousands of times.
    private readonly HashSet<string> _degraded = new(StringComparer.Ordinal);

    private readonly TrustOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TrustEngine> _logger;

    public TrustEngine(IOptions<TrustOptions> options, TimeProvider time, ILogger<TrustEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        // Read once rather than per call. Changing the half-life or the weights mid-flight would
        // silently rescale every score already computed, so the values are fixed for the engine's
        // lifetime and a change takes effect on restart.
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public event Action<TrustTransition>? FaultEntered;

    /// <inheritdoc />
    public event Action<TrustTransition>? FaultCleared;

    /// <inheritdoc />
    public IReadOnlyCollection<string> DegradedNodes
    {
        get
        {
            // A copy, so a caller iterating it cannot collide with ingestion modifying the set.
            lock (_gate)
            {
                return [.. _degraded];
            }
        }
    }

    /// <inheritdoc />
    public void Track(string macAddress)
    {
        if (!MacAddressValidator.TryNormalise(macAddress, out var mac))
        {
            return;
        }

        lock (_gate)
        {
            GetOrTrack(mac, _time.GetUtcNow());
        }
    }

    /// <inheritdoc />
    public void Record(string macAddress, TrustOutcome outcome)
    {
        if (!MacAddressValidator.TryNormalise(macAddress, out var mac))
        {
            return;
        }

        TrustTransition? transition;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var node = GetOrTrack(mac, now);

            node.Outcomes.Add(new OutcomeRecord(outcome, now));

            // Any packet, good or bad, proves the node is not silent.
            node.LastSeen = now;
            node.PenalisedIntervals = 0;

            transition = Evaluate(node, now);
        }

        Raise(transition);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Penalises <i>intervals</i>, not sweeps. The number of whole intervals since the node was last
    /// seen is compared against how many have already been penalised, and only the difference is
    /// applied. A node silent for three intervals carries three penalties whether the monitor looked
    /// at it three times or three hundred.
    /// </remarks>
    public int CheckHeartbeat(string macAddress, TimeSpan expectedInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expectedInterval, TimeSpan.Zero);

        if (!MacAddressValidator.TryNormalise(macAddress, out var mac))
        {
            return 0;
        }

        int applied;
        TrustTransition? transition;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var node = GetOrTrack(mac, now);

            // A node that has never reported is measured from when tracking began, so a sensor that
            // registers and then never sends a thing is caught like any other silent node.
            var baseline = node.LastSeen ?? node.TrackedSince;
            var missed = (now - baseline).Ticks / expectedInterval.Ticks;
            var outstanding = missed - node.PenalisedIntervals;

            if (outstanding <= 0)
            {
                return 0;
            }

            // Only the most recent intervals can matter: anything older than the window's capacity
            // would be pushed straight back out by the newer penalties being added after it.
            applied = (int)Math.Min(outstanding, node.Outcomes.Capacity);

            // Each penalty is stamped with the moment its interval actually elapsed, oldest first,
            // so a node silent for an hour carries an hour of progressively older penalties rather
            // than sixty identical fresh ones.
            for (var interval = missed - applied + 1; interval <= missed; interval++)
            {
                var elapsedAt = baseline + TimeSpan.FromTicks(expectedInterval.Ticks * interval);
                node.Outcomes.Add(new OutcomeRecord(TrustOutcome.MissedHeartbeat, elapsedAt));
            }

            node.PenalisedIntervals = missed;
            transition = Evaluate(node, now);
        }

        Raise(transition);

        return applied;
    }

    /// <inheritdoc />
    public void Reevaluate()
    {
        List<TrustTransition>? transitions = null;

        lock (_gate)
        {
            var now = _time.GetUtcNow();

            foreach (var node in _nodes.Values)
            {
                if (Evaluate(node, now) is { } transition)
                {
                    (transitions ??= []).Add(transition);
                }
            }
        }

        if (transitions is not null)
        {
            foreach (var transition in transitions)
            {
                Raise(transition);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<NodeTrustSnapshot> Snapshot()
    {
        List<NodeTrustSnapshot> snapshots;

        lock (_gate)
        {
            snapshots = new List<NodeTrustSnapshot>(_nodes.Count);

            foreach (var node in _nodes.Values)
            {
                snapshots.Add(node.ToSnapshot());
            }
        }

        // Sorted after the lock is released. Ordering is the dashboard's concern, and it should not
        // lengthen the time ingestion spends waiting.
        snapshots.Sort(WorstFirst);

        return snapshots;
    }

    /// <inheritdoc />
    public bool TryGet(string macAddress, [NotNullWhen(true)] out NodeTrustSnapshot? snapshot)
    {
        snapshot = null;

        if (!MacAddressValidator.TryNormalise(macAddress, out var mac))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_nodes.TryGetValue(mac, out var node))
            {
                return false;
            }

            snapshot = node.ToSnapshot();

            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetHistory(string macAddress, [NotNullWhen(true)] out IReadOnlyList<TrustHistoryPoint>? history)
    {
        history = null;

        if (!MacAddressValidator.TryNormalise(macAddress, out var mac))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_nodes.TryGetValue(mac, out var node))
            {
                return false;
            }

            // Copied out under the lock, oldest first, so the series cannot shift while serialising.
            history = node.History.ToList();

            return true;
        }
    }

    /// <inheritdoc />
    public TrustSummary Summary()
    {
        lock (_gate)
        {
            var provenTotal = 0.0;
            var proven = 0;
            NodeTrust? worst = null;
            int nominal = 0, unproven = 0, degrading = 0, fault = 0;

            foreach (var node in _nodes.Values)
            {
                switch (node.Band)
                {
                    case TrustBand.Nominal: nominal++; break;
                    case TrustBand.Unproven: unproven++; break;
                    case TrustBand.Degrading: degrading++; break;
                    default: fault++; break;
                }

                // Counted, but not averaged. An unproven node's 0.5 is the absence of evidence, and
                // folding it into the mean would pull a healthy mesh toward 0.5 on every registration.
                if (node.Band == TrustBand.Unproven)
                {
                    continue;
                }

                provenTotal += node.Score;
                proven++;

                // Among proven nodes the band order and the score order agree, so the most urgent
                // node is also the one with the lowest score.
                if (worst is null || CompareWorstFirst(node.Band, node.Score, node.MacAddress, worst.Band, worst.Score, worst.MacAddress) < 0)
                {
                    worst = node;
                }
            }

            return worst is null
                ? new TrustSummary(null, null, null, _nodes.Count, nominal, unproven, degrading, fault)
                : new TrustSummary(
                    provenTotal / proven, worst.Score, worst.MacAddress, _nodes.Count, nominal, unproven, degrading, fault);
        }
    }

    /// <summary>Finds a node, tracking it as unproven if it has not been seen before.</summary>
    private NodeTrust GetOrTrack(string mac, DateTimeOffset now)
    {
        if (_nodes.TryGetValue(mac, out var node))
        {
            return node;
        }

        // A node with no outcomes has nothing to judge, so it starts unproven whatever the
        // thresholds say about its 0.5. That keeps it out of the degraded set, and means tracking a
        // node can never be what raises an event about it.
        node = new NodeTrust(mac, now, TrustBand.Unproven, _options.OutcomeWindow, _options.HistoryLength);

        _nodes.Add(mac, node);
        UpdateDegradedSet(node);
        SampleHistory(node, now, bandChanged: true);

        return node;
    }

    /// <summary>Keeps the degraded set in step with the node's band.</summary>
    /// <remarks>
    /// <see cref="HashSet{T}.Add"/> on a member already present returns false and changes nothing.
    /// That is the whole mechanism by which a node failing on every packet stays one entry: the
    /// second and every later failure finds it already there.
    /// <para>
    /// Unproven nodes are kept out alongside nominal ones. This set is what alerting will read, and
    /// a node nobody has heard from yet has not failed.
    /// </para>
    /// </remarks>
    private void UpdateDegradedSet(NodeTrust node)
    {
        if (node.Band is TrustBand.Nominal or TrustBand.Unproven)
        {
            _degraded.Remove(node.MacAddress);
        }
        else
        {
            _degraded.Add(node.MacAddress);
        }
    }

    private double WeightOf(TrustOutcome outcome) => outcome switch
    {
        TrustOutcome.RangeViolation => _options.RangeViolationWeight,
        TrustOutcome.SchemaFailure => _options.SchemaFailureWeight,
        TrustOutcome.MissedHeartbeat => _options.MissedHeartbeatWeight,
        _ => 0
    };

    private static int WorstFirst(NodeTrustSnapshot left, NodeTrustSnapshot right) =>
        CompareWorstFirst(left.Band, left.Score, left.MacAddress, right.Band, right.Score, right.MacAddress);

    /// <summary>Orders by urgency of band, then by score ascending, then by MAC.</summary>
    /// <remarks>
    /// Band comes first because score alone gets unproven nodes wrong: an unproven 0.5 is
    /// numerically below a degrading 0.6, so a plain score sort would rank a node nobody has heard
    /// from above one that is actually slipping. The MAC tie-break makes the order total, so two
    /// nodes on the same score cannot swap places between refreshes and visibly shuffle the rows.
    /// </remarks>
    private static int CompareWorstFirst(
        TrustBand leftBand, double leftScore, string leftMac,
        TrustBand rightBand, double rightScore, string rightMac)
    {
        var bySeverity = TrustFormula.Severity(leftBand).CompareTo(TrustFormula.Severity(rightBand));

        if (bySeverity != 0)
        {
            return bySeverity;
        }

        var byScore = leftScore.CompareTo(rightScore);

        return byScore != 0 ? byScore : string.CompareOrdinal(leftMac, rightMac);
    }

    /// <summary>
    /// Recomputes one node's score, band and reason, and reports whether it crossed the fault boundary.
    /// </summary>
    /// <returns>The transition, if the node entered or left the fault band; otherwise null.</returns>
    /// <remarks>
    /// <b>Why this fires on a transition and never per packet.</b> The band the node held before
    /// this evaluation is read from the node, the new band is computed, and the node is overwritten —
    /// all inside the lock the caller holds. A transition is produced only when those two bands differ
    /// across the fault boundary. A node already in fault that receives another bad packet computes
    /// fault again, finds its stored band is already fault, and produces nothing. Because the compare
    /// and the overwrite share one lock, two packets arriving at the same instant cannot both read
    /// "not yet in fault" and both raise the event.
    /// <para>
    /// A property of the maths worth knowing: decay alone can never push a node into fault or recover
    /// it from fault. Ageing multiplies <c>s</c> and <c>f</c> by the same factor, and <c>T &lt; 0.5</c>
    /// holds exactly when <c>s &lt; f</c>, which scaling both cannot change. So every fault transition
    /// corresponds to a real new outcome. Decay can carry a nominal node back to degrading as its good
    /// evidence goes stale, and once evidence of any kind has faded to nothing it leaves the node
    /// unproven — which is not a recovery, and is never announced as one.
    /// </para>
    /// </remarks>
    private TrustTransition? Evaluate(NodeTrust node, DateTimeOffset now)
    {
        Span<int> counts = stackalloc int[OutcomeKinds];
        Span<double> failureWeights = stackalloc double[OutcomeKinds];

        var successes = 0.0;
        var failures = 0.0;

        // "Recent", for the counts in the reason, means still weighing at least a quarter.
        var recentWindow = _options.HalfLife * 2;

        // foreach binds to the ring buffer's struct enumerator, so scoring a node allocates nothing.
        foreach (var record in node.Outcomes)
        {
            var age = now - record.ObservedAt;
            var decay = TrustFormula.DecayWeight(age, _options.HalfLife);
            var kind = (int)record.Outcome;

            if (record.Outcome == TrustOutcome.Success)
            {
                successes += _options.SuccessWeight * decay;
            }
            else
            {
                var weight = WeightOf(record.Outcome) * decay;
                failures += weight;
                failureWeights[kind] += weight;
            }

            if (age <= recentWindow)
            {
                counts[kind]++;
            }
        }

        var previousBand = node.Band;
        var score = TrustFormula.Score(successes, failures);

        // Unproven when there is no evidence to judge: either no outcomes at all, or outcomes so old
        // their decayed weight has all but vanished. The second case guards a rounding failure — once
        // s and f drop below about 1e-16, (s + 1) rounds to exactly 1 and (s + f + 2) to exactly 2, so
        // the score lands on exactly 0.5 and a faulted node would seem to recover into degrading with
        // nothing having happened. See TrustFormula.MinimumEvidence.
        var band = successes + failures < TrustFormula.MinimumEvidence
            ? TrustBand.Unproven
            : TrustFormula.Classify(score, _options.NominalThreshold, _options.FaultThreshold);

        // Penalised intervals are only non-zero while at least one whole heartbeat has passed since
        // the last packet, so this is "currently silent" rather than merely "quiet for a moment".
        TimeSpan? silence = node.PenalisedIntervals > 0 ? now - (node.LastSeen ?? node.TrackedSince) : null;

        node.Score = score;
        node.Band = band;
        node.EvaluatedAt = now;
        node.RecentSuccesses = counts[(int)TrustOutcome.Success];
        node.RecentFailures = counts[(int)TrustOutcome.RangeViolation]
            + counts[(int)TrustOutcome.SchemaFailure]
            + counts[(int)TrustOutcome.MissedHeartbeat];
        node.Reason = TrustReasons.Describe(counts, failureWeights, node.Outcomes.Count, silence);

        SampleHistory(node, now, bandChanged: band != previousBand);
        UpdateDegradedSet(node);

        TrustTransition? transition = null;

        if (band == TrustBand.Fault && previousBand != TrustBand.Fault)
        {
            transition = new TrustTransition(
                node.MacAddress, score, previousBand, band, node.Reason,
                node.LastNominalAt ?? node.TrackedSince, now);

            node.FaultEnteredAt = now;
        }
        else if (previousBand == TrustBand.Fault && band == TrustBand.Unproven)
        {
            // Evidence decayed to nothing, which is not a recovery. Raising FaultCleared here would
            // announce a repair nobody made, so the fault episode ends without an event.
            node.FaultEnteredAt = null;
        }
        else if (band != TrustBand.Fault && previousBand == TrustBand.Fault)
        {
            transition = new TrustTransition(
                node.MacAddress, score, previousBand, band, node.Reason,
                node.FaultEnteredAt ?? node.TrackedSince, now);

            node.FaultEnteredAt = null;
        }

        if (band == TrustBand.Nominal)
        {
            node.LastNominalAt = now;
        }

        return transition;
    }

    /// <summary>Adds a sparkline point if one is due, or if the band just changed.</summary>
    private void SampleHistory(NodeTrust node, DateTimeOffset now, bool bandChanged)
    {
        if (node.History.Capacity == 0)
        {
            return;
        }

        var due = node.History.IsEmpty
            || now - node.History.Newest.Timestamp >= _options.HistorySampleInterval;

        if (bandChanged || due)
        {
            node.History.Add(new TrustHistoryPoint(now, node.Score, node.Band));
        }
    }

    /// <summary>Delivers a transition to its subscribers, isolating each from the others.</summary>
    /// <remarks>
    /// Always called with the lock released. Each subscriber is invoked separately and a failure in
    /// one is logged rather than propagated: whatever subscribes later is not allowed to break
    /// ingestion, and one broken subscriber must not stop the rest hearing about the transition.
    /// </remarks>
    private void Raise(TrustTransition? transition)
    {
        if (transition is null)
        {
            return;
        }

        var handlers = transition.Band == TrustBand.Fault ? FaultEntered : FaultCleared;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<TrustTransition> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(transition);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception, "A trust transition subscriber threw while handling node {Node}.", transition.MacAddress);
            }
        }
    }
}
