using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.History;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>
/// Stores each node's trust score into the history store at a steady spacing, so there is a series to fit a line to.
/// </summary>
/// <remarks>
/// <b>Why not use the node's sparkline.</b> The trust engine keeps a short ring of points for drawing, bounded by count, and
/// only the latest few travel with a snapshot. The forecast wants a few minutes of samples, in time order, for every node,
/// and the history store is the structure built to answer "what happened between these two moments". A score is a measurement
/// like a reading, so it lives beside the node's readings under its own kind.
/// <para>
/// Throttled per node, because the trust engine re-scores a node on every packet and a sample per packet would be a second
/// copy of the reading stream. One every <see cref="ForecastOptions.SampleInterval"/> is enough to see a trend and costs a
/// dictionary lookup per evaluation the rest of the time. Subscribes in the constructor, like the other subscribers to
/// domain events, so nothing is missed between construction and start.
/// </para>
/// </remarks>
internal sealed class TrustSampler : IHostedService, IDisposable
{
    private readonly ITrustEngine _trust;
    private readonly IHistoryStore _history;
    private readonly ArrivalSequence _arrival;
    private readonly ForecastOptions _options;
    private readonly TimeProvider _time;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public TrustSampler(ITrustEngine trust, IHistoryStore history, ArrivalSequence arrival, IOptions<ForecastOptions> options, TimeProvider time)
    {
        _trust = trust;
        _history = history;
        _arrival = arrival;
        _options = options.Value;
        _time = time;

        if (_options.Enabled)
        {
            _trust.NodeEvaluated += OnEvaluated;
        }
    }

    private void OnEvaluated(string macAddress)
    {
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (_last.TryGetValue(macAddress, out var last) && now - last < _options.SampleInterval)
            {
                return;
            }

            _last[macAddress] = now;
        }

        if (_trust.TryGet(macAddress, out var snapshot))
        {
            _history.Append(HistoryEntry.FromTrustSample(_arrival.Next(), snapshot.EvaluatedAt, macAddress, snapshot.Score));
        }
    }

    // Hosted only so that the host builds it when it starts: it subscribes in its constructor, and a singleton nobody asks for is
    // never built.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _trust.NodeEvaluated -= OnEvaluated;
}

/// <summary>
/// The devices to look at before anyone searches for them: what is already wrong, and what is about to be.
/// </summary>
/// <remarks>
/// <para>
/// <b>Current.</b> A device in Fault, with an open investigation, or with an active alert, ranked by how severe the worst of
/// those is. These come from structures that already exist (the trust engine, the investigations, the alert set) and need no
/// new computation: listing them is the point, because an operator should not have to search to find what is broken.
/// </para>
/// <para>
/// <b>Anticipated.</b> Reporting what is already broken is not anticipating. For each node that is not in Fault, a straight line
/// is fitted to its recent trust samples (see <see cref="TrustForecaster"/>); if it is falling steeply enough, fits well
/// enough, and reaches the fault threshold within the horizon, an insight says so, with the time. A node already in Fault is
/// current and is not forecast.
/// </para>
/// <para>
/// <b>It is withdrawn when the trend reverses</b>, and with some give: an insight is raised at one bar and withdrawn at a lower
/// one, so a fit hovering at the edge does not appear and disappear every pass. It is withdrawn at once if the node enters
/// Fault, where it is replaced by the current insight.
/// </para>
/// <para>
/// <b>The samples come from the history store's <c>GetRange</c></b>, the sorted structure whose job is exactly "this node
/// between these two moments". The cost of a pass over the whole fleet is measured in the benchmark: the store walks to the start
/// of a node's range, which is why it is a measured number and not an assumed one.
/// </para>
/// <para>
/// Announces a change when the set of insights changes, or a projected time moves by more than <see cref="ForecastOptions.EtaChange"/>,
/// and not otherwise: the countdown on the page is worked out on the page from the projected moment.
/// </para>
/// </remarks>
internal sealed class InsightService : BackgroundService
{
    private readonly ITrustEngine _trust;
    private readonly IHistoryStore _history;
    private readonly AlertTracker _alerts;
    private readonly InvestigationService _investigations;
    private readonly IDeviceDirectory _devices;
    private readonly ForecastOptions _options;
    private readonly TrustOptions _trustOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<InsightService> _logger;

    private readonly Lock _gate = new();

    // Held for a whole pass or refresh, from reading the world to publishing what was read. Without it a refresh that read the world a
    // moment ago could publish after a pass that read it later, and put the older picture back. Taken only by the timer and the
    // refresh worker, never from inside another component's lock, so it adds no order anyone else has to respect.
    private readonly Lock _passGate = new();
    private readonly Dictionary<string, Anticipation> _anticipated = new(StringComparer.Ordinal);
    // Problems that were only alerts and have just cleared, kept on the list for a little while. See ForecastOptions.CurrentHold.
    private readonly Lock _holdGate = new();
    private readonly Dictionary<string, (Insight Insight, DateTimeOffset At)> _held = new(StringComparer.Ordinal);
    private IReadOnlyList<Insight> _current = [];
    private IReadOnlyList<Insight> _published = [];

    public InsightService(
        ITrustEngine trust,
        IHistoryStore history,
        AlertTracker alerts,
        InvestigationService investigations,
        IDeviceDirectory devices,
        IOptions<ForecastOptions> options,
        IOptions<TrustOptions> trustOptions,
        TimeProvider time,
        ILogger<InsightService> logger)
    {
        _trust = trust;
        _history = history;
        _alerts = alerts;
        _investigations = investigations;
        _devices = devices;
        _options = options.Value;
        _trustOptions = trustOptions.Value;
        _time = time;
        _logger = logger;

        // What is wrong now changes when an alert, a fault or an investigation does, and those are rare, so it is refreshed on
        // each of them and not left for the next pass.
        _alerts.Changed += OnSomethingChanged;
        _trust.FaultEntered += OnTransition;
        _trust.FaultCleared += OnTransition;
        _investigations.Changed += OnInvestigationChanged;
    }

    /// <summary>Raised when the set of insights changed. Outside the service's lock.</summary>
    public event Action? Changed;

    /// <summary>The insights: anticipated first, soonest first, then current, most severe first.</summary>
    public IReadOnlyList<Insight> Current
    {
        get
        {
            lock (_gate)
            {
                return _published;
            }
        }
    }

    private void OnSomethingChanged(AlertChange change) => RefreshSoon();

    private void OnTransition(TrustTransition transition) => RefreshSoon();

    private void OnInvestigationChanged() => RefreshSoon();

    private int _refreshQueued;

    /// <summary>
    /// Asks for a refresh on another thread, once however many things changed. The alert set raises its event inside its own lock,
    /// and a refresh reads the alert set: doing it there would take two locks in an order the pass takes them the other way.
    /// </summary>
    private void RefreshSoon()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            });
        }
    }

    // ---- the loop -----------------------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Pass();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "The insight pass failed. The next one will be tried.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One forecast over every node, and a refresh of what is wrong now. Public to the tests and the benchmark.</summary>
    internal void Pass()
    {
        lock (_passGate)
        {
            PassLocked();
        }
    }

    private void PassLocked()
    {
        var now = _time.GetUtcNow();
        var nodes = _trust.Snapshot();
        var forecast = _options.Enabled ? Forecast(nodes, now) : [];

        // Worked out before the lock is taken: it reads the alert set and the investigations, which have locks of their own.
        var current = Currently(nodes, now);
        bool changed;

        lock (_gate)
        {
            changed = ApplyForecast(forecast);
            changed |= PublishLocked(current);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Refreshes what is wrong now, without refitting any lines.</summary>
    private void Refresh()
    {
        bool changed;

        lock (_passGate)
        {
            var current = Currently(_trust.Snapshot(), _time.GetUtcNow());

            lock (_gate)
            {
                changed = PublishLocked(current);
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    // ---- the forecast -------------------------------------------------------------------------------------------

    private Dictionary<string, (TrendForecast Forecast, NodeTrustSnapshot Node)> Forecast(IReadOnlyList<NodeTrustSnapshot> nodes, DateTimeOffset now)
    {
        Dictionary<string, (TrendForecast, NodeTrustSnapshot)> found = new(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            // A node in Fault is a current problem. A node with no evidence has a score that is a prior and not a trend.
            if (node.Band is TrustBand.Fault or TrustBand.Unproven)
            {
                continue;
            }

            // The sorted store is asked for this node between two moments. One tick past now, because the range is end-exclusive.
            var samples = _history
                .GetRange(node.MacAddress, now - _options.Lookback, now.AddTicks(1))
                .Where(entry => entry.Kind == HistoryKind.TrustSample)
                .Select(entry => (entry.Timestamp, entry.Value))
                .ToList();

            if (samples.Count < 2 || TrustForecaster.Fit(samples) is not { } fit)
            {
                continue;
            }

            var span = samples[^1].Timestamp - samples[0].Timestamp;
            var raisedBefore = false;

            _logger.LogDebug(
                "Forecast for {Mac}: {Samples} samples over {Span:0} s, score {Score:0.000}, falling {Fall:0.0000} a minute (recent {Recent:0.0000}), R2 {RSquared:0.000}.",
                node.MacAddress,
                samples.Count,
                span.TotalSeconds,
                node.Score,
                fit.FallPerMinute,
                fit.RecentFallPerMinute,
                fit.RSquared);

            lock (_gate)
            {
                raisedBefore = _anticipated.ContainsKey(node.MacAddress);
            }

            // Raised at the full bars and withdrawn at looser ones, so a fit at the edge does not flap.
            var forecast = TrustForecaster.Forecast(samples, _trustOptions.FaultThreshold, _options, raisedBefore);

            if (forecast is { } projected)
            {
                found[node.MacAddress] = (projected, node);
            }
        }

        return found;
    }

    /// <summary>Brings the held forecasts in line with this pass. Caller holds the lock.</summary>
    private bool ApplyForecast(Dictionary<string, (TrendForecast Forecast, NodeTrustSnapshot Node)> found)
    {
        var changed = false;

        foreach (var mac in _anticipated.Keys.Where(mac => !found.ContainsKey(mac)).ToList())
        {
            _anticipated.Remove(mac);
            changed = true;
        }

        foreach (var (mac, (forecast, node)) in found)
        {
            if (_anticipated.TryGetValue(mac, out var existing)
                && (forecast.ProjectedFaultAt - existing.Forecast.ProjectedFaultAt).Duration() <= _options.EtaChange)
            {
                // The same insight, a little later in its countdown. Kept as it was announced.
                continue;
            }

            _anticipated[mac] = new Anticipation(forecast, node);
            changed = true;
        }

        return changed;
    }

    // ---- what is wrong now --------------------------------------------------------------------------------------

    private List<Insight> Currently(IReadOnlyList<NodeTrustSnapshot> nodes, DateTimeOffset now)
    {
        Dictionary<string, Builder> byDevice = new(StringComparer.Ordinal);

        Builder For(string mac) =>
            byDevice.TryGetValue(mac, out var existing) ? existing : byDevice[mac] = new Builder(mac);

        foreach (var node in nodes)
        {
            if (node.Band == TrustBand.Fault)
            {
                var builder = For(node.MacAddress);

                builder.Band = node.Band;
                builder.Score = node.Score;
                builder.Add($"In Fault: {node.Reason}", 1.0, node.EvaluatedAt);
            }
        }

        foreach (var alert in _alerts.Active())
        {
            // A Fault alert is the trust engine's own fault, already listed from the band.
            if (alert.Key.Kind == AlertKind.Fault)
            {
                continue;
            }

            var builder = For(alert.Key.Mac);
            builder.Add(
                $"{SuggestionWording.Words(alert.Key.Kind.ToString())} alert{(alert.Acknowledged ? ", acknowledged" : string.Empty)}",
                AlertSeverity(alert.Key.Kind) * (alert.Acknowledged ? 0.7 : 1),
                alert.RaisedAt);
        }

        foreach (var investigation in _investigations.Open())
        {
            var builder = For(investigation.MacAddress);

            builder.InvestigationId = investigation.Id;
            builder.Add($"Investigation open since {investigation.OpenedAt:HH:mm}", 0.85, investigation.OpenedAt);
        }

        var insights = new List<Insight>(byDevice.Count);
        var scores = nodes.ToDictionary(node => node.MacAddress, StringComparer.Ordinal);

        foreach (var builder in byDevice.Values)
        {
            var name = DeviceName(builder.Mac);
            var band = scores.TryGetValue(builder.Mac, out var node) ? node.Band : TrustBand.Unproven;
            var score = scores.TryGetValue(builder.Mac, out node) ? node.Score : 0;
            var reasons = builder.Reasons.OrderByDescending(reason => reason.Severity).Select(reason => reason.Text).ToList();

            insights.Add(new Insight(
                $"current:{builder.Mac}",
                InsightKind.Current,
                builder.Mac,
                Room(builder.Mac),
                $"{name}: {Headline(builder, band)}",
                string.Join(". ", reasons) + ".",
                reasons,
                builder.Reasons.Max(reason => reason.Severity),
                band,
                score,
                SinceOf(builder.Mac, builder.Reasons.Min(reason => reason.Since)),
                null,
                null,
                builder.InvestigationId));
        }

        HoldRecentlyCleared(insights, now);

        return [.. insights.OrderByDescending(insight => insight.Severity).ThenBy(insight => insight.Since).ThenBy(insight => insight.MacAddress, StringComparer.Ordinal).Take(_options.MaxCurrentInsights)];
    }

    // When each device first appeared in the list of current problems. A fault's own time is when it was last scored, which is
    // every packet, and a list ordered by it would reshuffle itself on every pass and announce each shuffle.
    private readonly Dictionary<string, DateTimeOffset> _firstListed = new(StringComparer.Ordinal);

    private DateTimeOffset SinceOf(string mac, DateTimeOffset seen)
    {
        lock (_holdGate)
        {
            if (!_firstListed.TryGetValue(mac, out var first))
            {
                _firstListed[mac] = first = seen;
            }

            return first;
        }
    }

    /// <summary>Keeps a problem that was only an alert on the list for a while after it clears, and forgets it when the time is up.</summary>
    private void HoldRecentlyCleared(List<Insight> insights, DateTimeOffset now)
    {
        if (_options.CurrentHold <= TimeSpan.Zero)
        {
            return;
        }

        lock (_holdGate)
        {
            var present = new HashSet<string>(insights.Select(insight => insight.MacAddress), StringComparer.Ordinal);

            // A device that has gone from the list starts again the next time it comes back.
            foreach (var mac in _firstListed.Keys.Where(mac => !present.Contains(mac) && !_held.ContainsKey(mac)).ToList())
            {
                _firstListed.Remove(mac);
            }

            foreach (var insight in insights)
            {
                if (insight.Band != TrustBand.Fault && insight.InvestigationId is null)
                {
                    _held[insight.MacAddress] = (insight, now);
                }
                else
                {
                    _held.Remove(insight.MacAddress);
                }
            }

            foreach (var (mac, held) in _held.ToList())
            {
                if (present.Contains(mac))
                {
                    continue;
                }

                if (now - held.At < _options.CurrentHold)
                {
                    insights.Add(held.Insight);
                }
                else
                {
                    _held.Remove(mac);
                }
            }
        }
    }

    private static string Headline(Builder builder, TrustBand band)
    {
        List<string> parts = [];

        if (band == TrustBand.Fault)
        {
            parts.Add("in Fault");
        }

        var alerts = builder.Reasons.Count(reason => reason.Text.Contains(" alert", StringComparison.Ordinal));

        if (alerts > 0)
        {
            parts.Add(alerts == 1 ? "1 alert" : $"{alerts} alerts");
        }

        if (builder.InvestigationId is not null)
        {
            parts.Add("investigation open");
        }

        return string.Join(", ", parts);
    }

    private static double AlertSeverity(AlertKind kind) => kind switch
    {
        AlertKind.Disconnected => 0.75,
        AlertKind.OutOfRange => 0.7,
        AlertKind.TypeViolation => 0.5,
        AlertKind.CommandTimedOut => 0.5,
        _ => 0.6,
    };

    // ---- publishing ---------------------------------------------------------------------------------------------

    /// <summary>Puts the anticipated and the current insights together and says whether they differ from what was published. Caller holds the lock.</summary>
    private bool PublishLocked(List<Insight> current)
    {
        _current = current;

        var anticipated = _anticipated.Values
            .OrderBy(held => held.Forecast.ProjectedFaultAt)
            .Take(_options.MaxAnticipatedInsights)
            .Select(Anticipated)
            .ToList();

        List<Insight> all = [.. anticipated, .. _current];

        var same = all.Count == _published.Count && all.Zip(_published).All(pair => Same(pair.First, pair.Second));

        if (same)
        {
            return false;
        }

        _published = all;

        return true;
    }

    /// <summary>
    /// Whether two insights say the same thing to an operator: the same device, the same trouble, the same investigation. The
    /// wording of the detail is left out on purpose, because it carries counts and scores that move with every reading, and a
    /// message for each of them would be the noise the panel exists to cut through. A projected time that has drifted is handled
    /// where forecasts are held, which keeps the one that was announced until it has moved by more than the threshold.
    /// </summary>
    private static bool Same(Insight left, Insight right) =>
        left.Id == right.Id
        && left.Headline == right.Headline
        && left.Band == right.Band
        && left.Severity == right.Severity
        && left.InvestigationId == right.InvestigationId
        && left.ProjectedFaultAt == right.ProjectedFaultAt
        && left.Reasons.Count == right.Reasons.Count;

    private Insight Anticipated(Anticipation held)
    {
        var node = held.Node;
        var forecast = held.Forecast;
        var name = DeviceName(node.MacAddress);
        var minutes = forecast.TimeToFault.TotalMinutes;

        var detail = minutes < 1
            ? "Projected to reach Fault in under a minute."
            : $"Projected to reach Fault in about {Math.Round(minutes):0} {(Math.Round(minutes) == 1 ? "minute" : "minutes")}.";

        List<string> reasons =
        [
            $"Trust {forecast.Score:0.00}, falling about {forecast.Fit.FallPerMinute:0.000} a minute.",
            $"Reaches the Fault threshold ({_trustOptions.FaultThreshold:0.00}) about {forecast.ProjectedFaultAt:HH:mm}.",
            $"A straight line fits {forecast.Fit.Samples} samples over {forecast.Fit.Span.TotalMinutes:0.#} minutes with R² {forecast.Fit.RSquared:0.00}.",
        ];

        return new Insight(
            $"anticipated:{node.MacAddress}",
            InsightKind.Anticipated,
            node.MacAddress,
            Room(node.MacAddress),
            $"{name}: trust falling",
            detail,
            reasons,
            Math.Clamp(1 - forecast.TimeToFault / _options.Horizon, 0, 1) * 0.8,
            node.Band,
            forecast.Score,
            forecast.ProjectedFaultAt - forecast.TimeToFault,
            forecast.ProjectedFaultAt,
            forecast.Fit.RSquared,
            null);
    }

    // ---- naming -------------------------------------------------------------------------------------------------

    private string Room(string mac) => _devices.TryDescribe(mac, out var profile) ? profile.Room : mac;

    /// <summary>"Rack Row 3 temperature sensor": the room and what kind of device it is, unless the room already says.</summary>
    private string DeviceName(string mac)
    {
        if (!_devices.TryDescribe(mac, out var profile))
        {
            return mac;
        }

        var kind = KindWord(profile);

        return kind is null || profile.Room.Contains(kind, StringComparison.OrdinalIgnoreCase) || profile.Room.Contains("sensor", StringComparison.OrdinalIgnoreCase)
            ? profile.Room
            : $"{profile.Room} {kind}";
    }

    private static string? KindWord(DeviceProfile profile)
    {
        if (profile.Class.StartsWith("Actuator/", StringComparison.Ordinal))
        {
            return null;
        }

        return profile.Class[(profile.Class.IndexOf('/') + 1)..] switch
        {
            "°C" => "temperature sensor",
            "%RH" => "humidity sensor",
            "%" => "moisture sensor",
            "ppm" => "CO₂ sensor",
            "W" or "kW" => "power meter",
            "mS/cm" => "conductivity sensor",
            var unit => $"{unit} sensor",
        };
    }

    public override void Dispose()
    {
        _alerts.Changed -= OnSomethingChanged;
        _trust.FaultEntered -= OnTransition;
        _trust.FaultCleared -= OnTransition;
        _investigations.Changed -= OnInvestigationChanged;

        base.Dispose();
    }

    private sealed record Anticipation(TrendForecast Forecast, NodeTrustSnapshot Node);

    private sealed class Builder(string mac)
    {
        public string Mac { get; } = mac;

        public TrustBand Band { get; set; } = TrustBand.Unproven;

        public double Score { get; set; }

        public Guid? InvestigationId { get; set; }

        public List<(string Text, double Severity, DateTimeOffset Since)> Reasons { get; } = [];

        public void Add(string text, double severity, DateTimeOffset since) => Reasons.Add((text, severity, since));
    }
}
