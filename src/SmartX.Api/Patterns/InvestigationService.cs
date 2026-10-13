using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.History;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>What happened when a verdict was given.</summary>
internal enum VerdictOutcome
{
    /// <summary>The verdict was recorded.</summary>
    Recorded,

    /// <summary>No investigation has that id.</summary>
    NotFound,

    /// <summary>The verdict is not complete: a relabel needs a cause.</summary>
    Invalid
}

/// <summary>The result of giving a verdict.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Investigation">The investigation as it is now, when the verdict was recorded.</param>
/// <param name="Error">Why it was refused.</param>
internal sealed record VerdictResult(VerdictOutcome Outcome, InvestigationDetails? Investigation, string? Error);

/// <summary>
/// Closes the loop from the Part 1 research: a node that falls into fault gets an investigation of its own, and the operator's
/// verdict on it feeds back into detection.
/// </summary>
/// <remarks>
/// The research chose trust-weighted event escalation with an operator feedback loop. The trust engine has raised
/// <see cref="ITrustEngine.FaultEntered"/> since Part 1 and nothing but the alert set has listened. This is the subscriber the
/// seam was built for.
/// <para>
/// <b>What an investigation holds</b> is everything an operator would otherwise go and gather: the score and the reason, the
/// decay window (from the last moment the node was nominal to the crossing, which is what an operator correlates with a power
/// cut or a firmware push), the alerts active on the node, its trust samples and its readings in that window, and what the
/// pattern engine knows about faults like it.
/// </para>
/// <para>
/// <b>What a verdict changes.</b> Confirm changes nothing about detection. Dismiss makes that node a little less sensitive to
/// the kind of failure that caused the fault, by a fixed factor and never below a floor, for that node alone; reversing the
/// verdict gives the sensitivity back. Relabel stores the cause the operator gives. Every verdict is also recorded as an
/// operator action, so the pattern engine learns what operators do about faults.
/// </para>
/// <para>
/// A hosted service so it is subscribed before the API takes requests. One lock: the store is small and bounded, and every
/// operation is a dictionary lookup.
/// </para>
/// </remarks>
internal sealed class InvestigationService(
    ITrustEngine trust,
    AlertTracker alerts,
    IHistoryStore history,
    IPatternEngine patterns,
    IDeviceDirectory devices,
    TimeProvider time,
    IOptions<InvestigationOptions> options) : IHostedService
{
    private readonly Lock _gate = new();
    private readonly InvestigationOptions _options = options.Value;

    // Newest last. A list for order and a dictionary for lookup by id; both bounded by MaxRetained.
    private readonly List<Record> _ordered = [];
    private readonly Dictionary<Guid, Record> _byId = [];

    /// <summary>
    /// Raised when an investigation opens, resolves, or is given a verdict. Outside the service's lock, so a subscriber may
    /// ask the service a question. For whatever shows investigations live, which this class does not know exists.
    /// </summary>
    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        trust.FaultEntered += OnFaultEntered;
        trust.FaultCleared += OnFaultCleared;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        trust.FaultEntered -= OnFaultEntered;
        trust.FaultCleared -= OnFaultCleared;

        return Task.CompletedTask;
    }

    /// <summary>Investigations, newest first.</summary>
    /// <param name="status">Only these, or all of them when null.</param>
    /// <param name="limit">The most to return.</param>
    public IReadOnlyList<InvestigationDetails> List(InvestigationStatus? status, int limit)
    {
        List<Record> snapshot;

        lock (_gate)
        {
            snapshot = [.. Enumerable.Reverse(_ordered).Where(record => status is null || record.Status == status).Take(limit)];
        }

        return [.. snapshot.Select(Details)];
    }

    /// <summary>The open investigations, without the detail a page of one needs. For a list that only has to say which devices.</summary>
    public IReadOnlyList<(Guid Id, string MacAddress, DateTimeOffset OpenedAt)> Open()
    {
        lock (_gate)
        {
            return [.. _ordered.Where(record => record.Status == InvestigationStatus.Open).Select(record => (record.Id, record.Mac, record.OpenedAt))];
        }
    }

    /// <summary>One investigation.</summary>
    public InvestigationDetails? Get(Guid id)
    {
        Record? record;

        lock (_gate)
        {
            _byId.TryGetValue(id, out record);
        }

        return record is null ? null : Details(record);
    }

    /// <summary>Records an operator's verdict, replacing an earlier one and undoing what it changed.</summary>
    public VerdictResult GiveVerdict(Guid id, VerdictRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Verdict == Verdict.Relabel && request.Cause is null)
        {
            return new VerdictResult(VerdictOutcome.Invalid, null, "A relabel needs a cause: Hardware, Network, Power or Environmental.");
        }

        Record record;

        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out record!))
            {
                return new VerdictResult(VerdictOutcome.NotFound, null, null);
            }
        }

        var now = time.GetUtcNow();

        lock (_gate)
        {
            // A changed mind gives back what the earlier verdict took, so Dismiss then Confirm leaves the node as it was.
            if (record.SensitivityChange is { } earlier)
            {
                trust.RestoreSensitivity(record.Mac, earlier.Outcome, earlier.Before - earlier.After);
                record.SensitivityChange = null;
            }

            record.Verdict = request.Verdict;
            record.Cause = request.Verdict == Verdict.Relabel ? request.Cause : null;
            record.VerdictAt = now;
            record.Status = InvestigationStatus.Resolved;
            record.ResolvedAt ??= now;

            // Dismiss: a false alarm, so this node is made a little less sensitive to what caused it. Bounded by the floor,
            // so it can be dulled and never silenced, and only if the cause is known.
            if (request.Verdict == Verdict.Dismiss && record.CausedBy is { } outcome)
            {
                record.SensitivityChange = trust.AdjustSensitivity(record.Mac, outcome, _options.DismissFactor, _options.SensitivityFloor);
            }
        }

        // The operator did something about a fault, which is exactly what the pattern engine exists to learn.
        patterns.RecordAction(new OperatorAction(OperatorActionKind.Verdict, record.Mac, request.Verdict.ToString(), now));
        Changed?.Invoke();

        return new VerdictResult(VerdictOutcome.Recorded, Details(record), null);
    }

    // ---- the trust engine's events ----------------------------------------------------------------------------------

    private void OnFaultEntered(TrustTransition transition)
    {
        var mac = transition.MacAddress;
        var now = time.GetUtcNow();
        var readings = GatherReadings(mac, transition.WindowStart, transition.WindowEnd);

        trust.TryGetHistory(mac, out var samples);

        var record = new Record(Guid.NewGuid(), mac)
        {
            Room = devices.TryDescribe(mac, out var profile) ? profile.Room : mac,
            OpenedAt = transition.WindowEnd,
            Score = transition.Score,
            Reason = transition.Reason,
            CausedBy = trust.DominantFailure(mac),
            WindowStart = transition.WindowStart,
            WindowEnd = transition.WindowEnd,
            ActiveAlerts = [.. alerts.Active().Where(alert => alert.Key.Mac == mac).Select(alert => alert.Key)],
            TrustHistory = [.. (samples ?? []).Where(point => point.Timestamp >= transition.WindowStart && point.Timestamp <= transition.WindowEnd)],
            ReadingCount = readings.Count,
            Readings = [.. readings.TakeLast(_options.MaxReadings)],
        };

        lock (_gate)
        {
            // A node that fell into fault again without the last episode having been announced as cleared (its evidence
            // faded to nothing, which is not a recovery) leaves an open investigation behind. It is closed as superseded.
            foreach (var open in _ordered.Where(other => other.Mac == mac && other.Status == InvestigationStatus.Open))
            {
                open.Status = InvestigationStatus.Resolved;
                open.ResolvedAt ??= now;
            }

            _ordered.Add(record);
            _byId.Add(record.Id, record);

            // Bounded. The oldest closed ones go first; an open one is never dropped to make room.
            while (_ordered.Count > _options.MaxRetained && _ordered.FindIndex(other => other.Status == InvestigationStatus.Resolved) is var index and >= 0)
            {
                _byId.Remove(_ordered[index].Id);
                _ordered.RemoveAt(index);
            }
        }

        Changed?.Invoke();
    }

    private void OnFaultCleared(TrustTransition transition)
    {
        var now = time.GetUtcNow();
        var resolved = false;

        lock (_gate)
        {
            // Resolved automatically, unless the operator got there first: a verdict already closed it, and the moment
            // they gave it stays as the moment it was resolved.
            foreach (var record in _ordered.Where(other => other.Mac == transition.MacAddress && other.Status == InvestigationStatus.Open))
            {
                record.Status = InvestigationStatus.Resolved;
                record.ResolvedAt = now;
                resolved = true;
            }
        }

        if (resolved)
        {
            Changed?.Invoke();
        }
    }

    private List<InvestigationReading> GatherReadings(string mac, DateTimeOffset from, DateTimeOffset to)
    {
        // The range is end-exclusive, and the crossing itself belongs to the window.
        return
        [
            .. history.GetRange(mac, from, to.AddTicks(1))
                .Where(entry => entry.Kind == HistoryKind.Reading && !double.IsNaN(entry.Value))
                .Select(entry => new InvestigationReading(entry.Timestamp, entry.Value, entry.Unit)),
        ];
    }

    private InvestigationDetails Details(Record record)
    {
        lock (_gate)
        {
            return new InvestigationDetails(
                record.Id,
                record.Mac,
                record.Room,
                record.Status,
                record.OpenedAt,
                record.ResolvedAt,
                record.Score,
                record.Reason,
                record.CausedBy,
                record.WindowStart,
                record.WindowEnd,
                record.ActiveAlerts,
                record.TrustHistory,
                record.ReadingCount,
                record.Readings,
                // Asked when read and not when opened, so the precedent reflects what has been learned since.
                patterns.Predict(TriggerKind.TrustBand, nameof(TrustBand.Fault), record.Mac, time.GetUtcNow()),
                record.Verdict,
                record.Cause,
                record.VerdictAt,
                record.SensitivityChange,
                trust.Sensitivity(record.Mac, _options.SensitivityFloor));
        }
    }

    /// <summary>One investigation as it is held: mutable, internal, and only ever read out as an immutable record.</summary>
    private sealed class Record(Guid id, string mac)
    {
        public Guid Id { get; } = id;

        public string Mac { get; } = mac;

        public string Room { get; init; } = mac;

        public InvestigationStatus Status { get; set; } = InvestigationStatus.Open;

        public DateTimeOffset OpenedAt { get; init; }

        public DateTimeOffset? ResolvedAt { get; set; }

        public double Score { get; init; }

        public string Reason { get; init; } = string.Empty;

        public TrustOutcome? CausedBy { get; init; }

        public DateTimeOffset WindowStart { get; init; }

        public DateTimeOffset WindowEnd { get; init; }

        public IReadOnlyList<AlertKey> ActiveAlerts { get; init; } = [];

        public IReadOnlyList<TrustHistoryPoint> TrustHistory { get; init; } = [];

        public int ReadingCount { get; init; }

        public IReadOnlyList<InvestigationReading> Readings { get; init; } = [];

        public Verdict? Verdict { get; set; }

        public FaultCause? Cause { get; set; }

        public DateTimeOffset? VerdictAt { get; set; }

        public SensitivityChange? SensitivityChange { get; set; }
    }
}
