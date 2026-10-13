using System.Security.Cryptography;
using System.Threading.Channels;
using System.Text;
using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>What the suggestion service holds now.</summary>
/// <param name="Suggestions">Ranked, best first.</param>
/// <param name="Dismissed">Marked "Not useful" and not yet forgotten, newest first.</param>
/// <param name="ComputedAt">When it was worked out.</param>
internal sealed record SuggestionState(
    IReadOnlyList<Suggestion> Suggestions,
    IReadOnlyList<DismissedSuggestion> Dismissed,
    DateTimeOffset ComputedAt);

/// <summary>What happened when a suggestion was dismissed or restored.</summary>
internal enum SuggestionOutcome
{
    /// <summary>It was recorded.</summary>
    Done,

    /// <summary>No such suggestion is held.</summary>
    NotFound
}

/// <summary>
/// Works out what an operator is likely to want to do now, ranks it, and says why. No reference to SignalR or any transport.
/// </summary>
internal interface ISuggestionService
{
    /// <summary>The ranked suggestions and the recently dismissed ones.</summary>
    SuggestionState Current { get; }

    /// <summary>Raised when the set of suggestions or their order changed. Raised outside the service's lock.</summary>
    event Action? Changed;

    /// <summary>Marks a suggestion "Not useful": it goes away and its pattern's weight is lowered, within a bound.</summary>
    SuggestionOutcome Dismiss(string id);

    /// <summary>Takes a "Not useful" back: the suggestion returns and the weight is given back.</summary>
    SuggestionOutcome Restore(string id);

    /// <summary>
    /// Records that the operator is about to do what a suggestion says by accepting it, so the action that follows is counted
    /// as following advice and not as a habit.
    /// </summary>
    /// <returns><see langword="false"/> when no such suggestion is held.</returns>
    bool Accept(string id);

    /// <summary>
    /// Whether an action that is happening now is the one an accepted suggestion announced. Each acceptance is used once.
    /// </summary>
    bool TakeAcceptance(OperatorActionKind kind, string macAddress, string command);
}

/// <summary>
/// The suggestion engine's delivery half: from the patterns the engine has learned and the triggers that are live now, the
/// few actions most worth offering, ranked, explained, and withdrawn when done.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inputs.</b> The live triggers (those still inside the pattern window, which this service keeps from the engine's own
/// announcements), and for each the qualified patterns the engine has: the device's own pattern where it has enough
/// to go on, and the pattern for its kind in its zone where it does not (see <see cref="IPatternEngine.Predict"/>). A
/// pattern at the general level, "a pump in Zone 1", is turned into the actual pumps in that zone, because a button has to do
/// something to one device.
/// </para>
/// <para>
/// <b>Ranking.</b> Each candidate action is scored from 0 to 1:
/// </para>
/// <code>
/// score = weight × ( wL·L + wC·C + wS·S + wR·R ) / ( wL + wC + wS + wR )
///
///   L  lift, as ln(lift) / ln(LiftCap), capped at 1:   how much more likely than usual
///   C  confidence:                                      how reliably it follows the trigger
///   S  severity of the trigger, 0 to 1:                 a Fault is 1, a critical reading .9, an alert or a
///                                                       degrading node .6, a threshold crossing .4
///   R  recency, 1 - age / window:                       1 for a trigger that has just fired, 0 as it leaves the window
///   weight  what "Not useful" has done to this pattern: 1 down to a floor of .5, never zero
///
///   defaults:  wL = .35   wC = .25   wS = .25   wR = .15   LiftCap = 10
/// </code>
/// <para>
/// Lift is weighted most because it is what separates a habit from a coincidence. Recency is weighted least because it only
/// has to break ties: a suggestion does not become wrong as it ages, and the window ends it anyway. The weights are
/// configuration (<see cref="SuggestionOptions"/>) and are normalised, so changing one changes the balance and never the range.
/// </para>
/// <para>
/// <b>The top K is a <see cref="PriorityQueue{TElement, TPriority}"/>.</b> The same structure the ingestion pipeline's
/// critical lane is built on, used here for the opposite job: that one hands out the most urgent thing first, this one keeps the
/// best K. Every candidate is offered to a min-heap capped at K; when it holds more than K the lowest score is removed, so
/// what is left is the K best. A heap is the right shape because the thing to find at each step is the smallest of what is
/// kept, which is the root, and removing it is O(log K): the whole selection costs O(n log K), never a sort of everything.
/// </para>
/// <para>
/// <b>What is already done is not suggested.</b> Each live trigger has a <see cref="HashSet{T}"/> of the actions taken since it
/// fired. Opening Pump A, or sending it the command, puts the action in the set, and a candidate that is in the set is skipped.
/// Opening any pump in the zone also satisfies a pattern about "a pump in the zone".
/// </para>
/// <para>
/// <b>Feedback without a feedback loop.</b> "Not useful" removes the suggestion and multiplies its pattern's weight by a factor
/// (0.85), never below a floor (0.5), and giving it back is exact because the amount taken is remembered. Accepting a
/// suggestion is the opposite problem: an operator who clicks "Turn on Pump A" because the system said so has not shown
/// a habit of turning on Pump A, and counting the click would make the pattern stronger, which would rank it higher, which would
/// get it clicked more. So an accepted suggestion is registered here first, and the engine counts the action that follows at
/// a configurable weight that is zero by default (see <see cref="PatternOptions.AcceptedSuggestionWeight"/>).
/// </para>
/// <para>
/// <b>When it recomputes.</b> When a trigger fires, when an action is taken, when a suggestion is dismissed or restored, and on
/// a slow sweep that ends triggers that have aged out. Never per reading. <see cref="Changed"/> is raised only when the set or
/// the order changed, so a score drifting as a trigger ages sends nothing.
/// </para>
/// </remarks>
internal sealed class SuggestionService : BackgroundService, ISuggestionService
{
    private readonly Lock _gate = new();
    private readonly IPatternEngine _patterns;
    private readonly IDeviceDirectory _devices;
    private readonly SuggestionOptions _options;
    private readonly PatternOptions _patternOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<SuggestionService> _logger;

    // The live triggers, oldest first. A List and not a Queue because the sweep removes from the front but a bounded flood
    // removes from the front too and neither needs anything else; the order is the order they fired.
    private readonly List<LiveTrigger> _live = [];

    // What "Not useful" has done to each pattern: weight, by pattern. Absent means untouched, which is one.
    private readonly Dictionary<PatternRef, double> _weights = [];

    // Dismissed suggestions, newest last, bounded. The ids are also kept so a dismissed suggestion is not offered again.
    private readonly List<Dismissal> _dismissals = [];
    // What each dismissed suggestion's trigger was when it was dismissed, by id. A suggestion is hidden while its trigger is no
    // newer than that: the same trigger firing again is a new occurrence and is offered again, with the pattern's weight lowered.
    private readonly Dictionary<string, DateTimeOffset> _dismissedAt = new(StringComparer.Ordinal);

    // Acceptances waiting for the action they announced: kind, device, command, and when they lapse.
    private readonly Dictionary<(OperatorActionKind Kind, string Mac, string Command), DateTimeOffset> _accepted = [];

    // What the engine has told us and nobody has processed yet. See OnTrigger.
    private readonly Channel<Action> _inbox = Channel.CreateBounded<Action>(new BoundedChannelOptions(InboxCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private readonly bool _inline;

    private const int InboxCapacity = 20_000;

    private SuggestionState _state;
    private Dictionary<string, Candidate> _candidates = new(StringComparer.Ordinal);
    private long _nextTrigger;

    public SuggestionService(
        IPatternEngine patterns,
        IDeviceDirectory devices,
        IOptions<SuggestionOptions> options,
        IOptions<PatternOptions> patternOptions,
        TimeProvider time,
        ILogger<SuggestionService> logger,
        bool processInline = false)
    {
        _inline = processInline;
        _patterns = patterns;
        _devices = devices;
        _options = options.Value;
        _patternOptions = patternOptions.Value;
        _time = time;
        _logger = logger;
        _state = new SuggestionState([], [], time.GetUtcNow());

        _patterns.TriggerRecorded += OnTrigger;
        _patterns.ActionRecorded += OnAction;
    }

    public event Action? Changed;

    public SuggestionState Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    // ---- what the engine tells us -------------------------------------------------------------------------------

    // The engine raises these on whatever thread told it, and that thread may be holding a lock of someone else's. The alert set
    // raises its changes inside the lock it shares with the device index, and an alert is a trigger, so the engine can be told
    // from inside that lock. Ranking needs that same index (to name a device and to find the pumps in a zone) while it holds this
    // service's lock. If the two met, one thread would hold the index and wait for this service while the other held this
    // service and waited for the index, and both would wait for ever. So the handlers only post the event to an inbox and return,
    // holding nothing, and one loop works through it. A deadlock needs a thread that holds one lock and asks for another; the
    // thread that raised the event never asks for anything here, and the loop holds no lock of anyone else's.
    private void OnTrigger(TriggerEvent trigger) => Post(() => ApplyTrigger(trigger));

    private void OnAction(OperatorAction action) => Post(() => ApplyAction(action));

    private void Post(Action work)
    {
        if (_inline)
        {
            work();

            return;
        }

        if (!_inbox.Writer.TryWrite(work))
        {
            _logger.LogWarning("The suggestion inbox is full ({Capacity}); an event was dropped. The next sweep will catch up.", InboxCapacity);
        }
    }

    private void ApplyTrigger(TriggerEvent trigger)
    {
        bool changed;

        lock (_gate)
        {
            if (_live.Count >= _options.MaxLiveTriggers)
            {
                _live.RemoveAt(0);
            }

            // The same sensor crossing the same level again, with nothing done about the last time, is the same incident and not a
            // second one: a reading that hovers about a level fires more than once, and a suggestion that was replaced by an
            // identical one each time would flash "New" for ever. The existing entry takes the later time.
            var live = _live.Find(existing => existing.Taken.Count == 0 && existing.Key == trigger.Key());

            if (live is null)
            {
                live = new LiveTrigger(++_nextTrigger, trigger);
                _live.Add(live);
            }
            else
            {
                live.Event = trigger;
                live.EmptyAtVersion = -1;
            }

            // Most triggers have no pattern, and a trigger with none cannot change what is suggested. Asking once says so, and the
            // rest of the live triggers are not walked for it. The sweep brings the ranking up to date as triggers age.
            var version = _patterns.LearnedVersion;

            if (_patterns.Predict(trigger.Kind, trigger.Detail, trigger.MacAddress, _time.GetUtcNow()).Associations.Count == 0)
            {
                live.EmptyAtVersion = version;

                return;
            }

            changed = RecomputeLocked();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void ApplyAction(OperatorAction action)
    {
        // A search is weaker than opening a device and does not satisfy "open it".
        if (action.Kind is not (OperatorActionKind.Viewed or OperatorActionKind.Command))
        {
            return;
        }

        var known = _devices.TryDescribe(action.MacAddress, out var profile);
        bool changed;

        lock (_gate)
        {
            foreach (var live in _live)
            {
                if (action.At < live.Event.At)
                {
                    continue;
                }

                live.Taken.Add(new ActionKey(PatternLevel.Specific, action.Kind, action.MacAddress, action.Command));

                if (known)
                {
                    live.Taken.Add(new ActionKey(PatternLevel.General, action.Kind, profile.GeneralScope, action.Command));
                }
            }

            changed = RecomputeLocked();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    // ---- sweeping -----------------------------------------------------------------------------------------------

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunInboxAsync(stoppingToken), RunSweepAsync(stoppingToken));

    /// <summary>Works through what the engine has told this service, one at a time, in the order it was told.</summary>
    private async Task RunInboxAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in _inbox.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    work();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "A suggestion could not be worked out. The next event will be tried.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Sweep();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "The suggestion sweep failed. The next one will be tried.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Ends triggers that have aged out of the window and refreshes the ranking. Public to the tests so they need not wait for the timer.</summary>
    internal void Sweep()
    {
        bool changed;

        lock (_gate)
        {
            changed = RecomputeLocked();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    // ---- feedback -----------------------------------------------------------------------------------------------

    public SuggestionOutcome Dismiss(string id)
    {
        bool changed;

        lock (_gate)
        {
            if (!_candidates.TryGetValue(id, out var candidate))
            {
                return SuggestionOutcome.NotFound;
            }

            var before = _weights.GetValueOrDefault(candidate.Pattern, 1);
            var after = Math.Max(_options.NotUsefulFloor, before * _options.NotUsefulFactor);
            var original = before;

            // Dismissed before, on an earlier occurrence: the two are one entry that remembers where it began, so taking it back
            // gives back everything both took, and the list never shows one suggestion twice.
            var earlier = _dismissals.FindIndex(dismissal => dismissal.Id == id);

            if (earlier >= 0)
            {
                original = _dismissals[earlier].Before;
                _dismissals.RemoveAt(earlier);
            }

            _weights[candidate.Pattern] = after;
            _dismissedAt[id] = candidate.Suggestion.TriggeredAt;
            _dismissals.Add(new Dismissal(id, candidate.Pattern, candidate.Suggestion with { Weight = after }, _time.GetUtcNow(), original, after));

            while (_dismissals.Count > _options.MaxDismissedRetained)
            {
                // Forgetting the oldest only forgets that it can be taken back. What it took stays taken, bounded by the floor.
                _dismissedAt.Remove(_dismissals[0].Id);
                _dismissals.RemoveAt(0);
            }

            changed = RecomputeLocked(force: true);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return SuggestionOutcome.Done;
    }

    public SuggestionOutcome Restore(string id)
    {
        bool changed;

        lock (_gate)
        {
            var index = _dismissals.FindIndex(dismissal => dismissal.Id == id);

            if (index < 0)
            {
                return SuggestionOutcome.NotFound;
            }

            var dismissal = _dismissals[index];

            // Exactly what was taken, so dismissing then restoring leaves the pattern as it was.
            var restored = Math.Min(1, _weights.GetValueOrDefault(dismissal.Pattern, 1) + (dismissal.Before - dismissal.After));

            if (restored >= 1)
            {
                _weights.Remove(dismissal.Pattern);
            }
            else
            {
                _weights[dismissal.Pattern] = restored;
            }

            _dismissals.RemoveAt(index);
            _dismissedAt.Remove(id);
            changed = RecomputeLocked(force: true);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return SuggestionOutcome.Done;
    }

    public bool Accept(string id)
    {
        lock (_gate)
        {
            if (!_candidates.TryGetValue(id, out var candidate))
            {
                return false;
            }

            var action = candidate.Suggestion.Action;
            var kind = action.Kind == SuggestedActionKind.Command ? OperatorActionKind.Command : OperatorActionKind.Viewed;
            var command = action.Command?.ToString() ?? string.Empty;
            var now = _time.GetUtcNow();

            Prune(now);
            _accepted[(kind, action.MacAddress, command)] = now + _options.AcceptanceExpiry;

            return true;
        }
    }

    public bool TakeAcceptance(OperatorActionKind kind, string macAddress, string command)
    {
        lock (_gate)
        {
            if (_accepted.Count == 0)
            {
                return false;
            }

            Prune(_time.GetUtcNow());

            return _accepted.Remove((kind, macAddress, command));
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _accepted.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
        {
            _accepted.Remove(key);
        }
    }

    // ---- ranking ------------------------------------------------------------------------------------------------

    /// <summary>Works out the suggestions again. Caller holds the lock.</summary>
    /// <param name="force">Whether to report a change even if the ids and order are the same, because a dismissal changed what the list says.</param>
    /// <returns>Whether the set of suggestions, their order, or the dismissed list changed.</returns>
    private bool RecomputeLocked(bool force = false)
    {
        var now = _time.GetUtcNow();

        // Triggers that have left the window end here, oldest first, so the cost is one comparison per trigger removed.
        _live.RemoveAll(live => now - live.Event.At > _patternOptions.Window);

        var best = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        var version = _patterns.LearnedVersion;

        foreach (var live in _live)
        {
            // A trigger that had no pattern has none until the engine learns something, and most triggers are of that kind:
            // asking again for each of them on every event is the cost this avoids.
            if (live.EmptyAtVersion == version)
            {
                continue;
            }

            var answer = _patterns.Predict(live.Event.Kind, live.Event.Detail, live.Event.MacAddress, now);

            if (answer.Associations.Count == 0)
            {
                live.EmptyAtVersion = version;

                continue;
            }

            foreach (var association in answer.Associations)
            {
                foreach (var candidate in Candidates(live, association, now))
                {
                    // The same action offered by two triggers is one suggestion, at the better of the two scores.
                    if (!best.TryGetValue(candidate.Identity, out var existing) || candidate.Suggestion.Score > existing.Suggestion.Score)
                    {
                        best[candidate.Identity] = candidate;
                    }
                }
            }
        }

        // The best K, by a min-heap of size K: offer each, and when there are K + 1 drop the lowest. Ties are broken by id so
        // the order is the same every time.
        var top = new PriorityQueue<Candidate, (double Score, string Id)>();

        foreach (var candidate in best.Values)
        {
            if (IsDismissed(candidate.Suggestion))
            {
                continue;
            }

            top.Enqueue(candidate, (candidate.Suggestion.Score, candidate.Suggestion.Id));

            if (top.Count > _options.MaxSuggestions)
            {
                top.Dequeue();
            }
        }

        var ranked = new Candidate[top.Count];

        for (var index = ranked.Length - 1; index >= 0; index--)
        {
            ranked[index] = top.Dequeue();
        }

        // Same suggestions, in the same order, about the same occurrences. A trigger firing again changes the time a suggestion is
        // about and is worth saying, though it is not a new suggestion.
        var sameOrder = _state.Suggestions.Select(suggestion => (suggestion.Id, suggestion.TriggeredAt))
            .SequenceEqual(ranked.Select(candidate => (candidate.Suggestion.Id, candidate.Suggestion.TriggeredAt)));

        // Every candidate, dismissed or not, so a dismissal can find the suggestion it was asked about and a restore can bring it back.
        _candidates = best.Where(pair => !IsDismissed(pair.Value.Suggestion))
            .ToDictionary(pair => pair.Value.Suggestion.Id, pair => pair.Value, StringComparer.Ordinal);

        var dismissed = _dismissals
            .AsEnumerable()
            .Reverse()
            .Select(dismissal => new DismissedSuggestion(dismissal.Suggestion, dismissal.At, dismissal.Before, dismissal.After))
            .ToList();

        var dismissedChanged = !_state.Dismissed.Select(item => item.Suggestion.Id).SequenceEqual(dismissed.Select(item => item.Suggestion.Id), StringComparer.Ordinal);

        _state = new SuggestionState([.. ranked.Select(candidate => candidate.Suggestion)], dismissed, now);

        return force || !sameOrder || dismissedChanged;
    }

    /// <summary>The concrete things an association says to do about one live trigger, scored and explained.</summary>
    private IEnumerable<Candidate> Candidates(LiveTrigger live, PatternAssociation association, DateTimeOffset now)
    {
        if (association.Action.Kind == OperatorActionKind.Verdict)
        {
            yield break;
        }

        var kind = association.Action.Kind == OperatorActionKind.Command ? OperatorActionKind.Command : OperatorActionKind.Viewed;
        var command = association.Action.Command;
        var targets = Targets(live, association);

        foreach (var target in targets)
        {
            // Done already, by this device or by any of its kind in the zone: not suggested.
            var specific = new ActionKey(PatternLevel.Specific, kind, target.MacAddress, command);
            var general = new ActionKey(PatternLevel.General, kind, target.GeneralScope, command);

            if (live.Taken.Contains(specific) || live.Taken.Contains(general))
            {
                continue;
            }

            var pattern = new PatternRef(
                association.Level,
                association.Trigger.Kind,
                association.Trigger.Detail,
                association.Trigger.Scope,
                kind,
                association.Action.Target,
                command);

            var weight = _weights.GetValueOrDefault(pattern, 1);

            // The occurrence of the trigger that is happening now is counted by the engine, and it is not yet one of the times the
            // operator did or did not respond. "7 of the last 8" would count the one still in front of them as a miss.
            var ofTimes = Math.Max(association.Times, association.TriggerTimes - 1);
            var action = BuildAction(kind, command, target);
            var triggerRoom = _devices.TryDescribe(live.Event.MacAddress, out var triggerProfile) ? triggerProfile.Room : live.Event.MacAddress;
            var identity = $"{action.Kind}:{action.MacAddress}:{action.Command}";
            var id = Identify($"{live.Event.Kind}|{live.Event.MacAddress}|{live.Event.Detail}", identity);

            var suggestion = new Suggestion(
                id,
                action.Label,
                Describe(live.Event, triggerProfile, triggerRoom),
                new SuggestionEvidence(
                    association.Times,
                    ofTimes,
                    association.SeededTimes,
                    association.Confidence,
                    association.Lift,
                    Summarise(association, ofTimes)),
                association.Level,
                association.Level == PatternLevel.Specific
                    ? "This device's own pattern"
                    : $"Similar devices: a {association.Trigger.ScopeLabel}",
                Score(association, live, now, weight),
                weight,
                action,
                live.Event.Kind,
                live.Event.MacAddress,
                triggerRoom,
                live.Event.At,
                live.Event.At + _patternOptions.Window,
                now);

            yield return new Candidate(identity, pattern, suggestion);
        }
    }

    /// <summary>The devices an association is about: the one, or for a pattern about a kind of device, the first few of that kind in the zone.</summary>
    private IEnumerable<DeviceProfile> Targets(LiveTrigger live, PatternAssociation association)
    {
        if (association.Level == PatternLevel.Specific)
        {
            if (_devices.TryDescribe(association.Action.Target, out var one))
            {
                yield return one;
            }

            yield break;
        }

        // A general target is "class@zone". The class itself has a slash and never an @, so the last @ is the split.
        var split = association.Action.Target.LastIndexOf('@');

        if (split < 0)
        {
            yield break;
        }

        var shown = 0;

        foreach (var profile in _devices.Similar(association.Action.Target[..split], association.Action.Target[(split + 1)..]))
        {
            // The device the trigger happened to is not offered as the answer to itself.
            if (profile.MacAddress == live.Event.MacAddress)
            {
                continue;
            }

            yield return profile;

            if (++shown >= _options.MaxDevicesPerGeneralSuggestion)
            {
                yield break;
            }
        }
    }

    private static SuggestedAction BuildAction(OperatorActionKind kind, string command, DeviceProfile device)
    {
        if (kind == OperatorActionKind.Command
            && Enum.TryParse<CommandAction>(command, out var parsed)
            && Enum.IsDefined(parsed)
            && SuggestionWording.Verb(parsed) is { } verb)
        {
            return new SuggestedAction(SuggestedActionKind.Command, device.MacAddress, device.Room, parsed, null, $"{verb} {device.Room}");
        }

        // A level has no level to suggest, so the operator is taken to the device to choose one. A search, a click, anything else: open it.
        return new SuggestedAction(SuggestedActionKind.OpenDevice, device.MacAddress, device.Room, null, null, $"Open {device.Room}");
    }

    private double Score(PatternAssociation association, LiveTrigger live, DateTimeOffset now, double weight)
    {
        var o = _options;
        var lift = Math.Clamp(Math.Log(Math.Max(1, association.Lift)) / Math.Log(o.LiftCap), 0, 1);
        var confidence = Math.Clamp(association.Confidence, 0, 1);
        var severity = SuggestionWording.Severity(live.Event, o.Severity);
        var recency = Math.Clamp(1 - (now - live.Event.At) / _patternOptions.Window, 0, 1);

        var total = o.LiftWeight + o.ConfidenceWeight + o.SeverityWeight + o.RecencyWeight;
        var raw = (o.LiftWeight * lift + o.ConfidenceWeight * confidence + o.SeverityWeight * severity + o.RecencyWeight * recency) / total;

        return raw * weight;
    }

    private static string Summarise(PatternAssociation association, int ofTimes)
    {
        var seeded = association.SeededTimes > 0 ? $" ({association.SeededTimes} from seeded history)" : string.Empty;
        var who = association.Level == PatternLevel.General ? "Operators did this for similar devices" : "Operators did this";

        // The engine caps lift at 100 so it stays finite; say so and not "100×", which would read as measured.
        var lift = association.Lift >= 100 ? "Over 100×" : $"{association.Lift:0.#}×";

        return $"{who} {association.Times} of the last {ofTimes} times{seeded}. {lift} more likely than usual.";
    }

    private string Describe(TriggerEvent trigger, DeviceProfile? profile, string room)
    {
        switch (trigger.Kind)
        {
            case TriggerKind.ThresholdCrossing when profile is not null && !profile.IsState:
                var low = trigger.Detail == "Low";
                var fraction = low ? _patternOptions.WatchLowFraction : _patternOptions.WatchHighFraction;
                var level = profile.RangeMinimum + fraction * (profile.RangeMaximum - profile.RangeMinimum);
                var unit = profile.Class[(profile.Class.IndexOf('/') + 1)..];

                return $"{room} {(low ? "dropped below" : "rose above")} {level:0.##} {unit}";

            case TriggerKind.ThresholdCrossing:
                return $"{room} crossed its {trigger.Detail.ToLowerInvariant()} level";

            case TriggerKind.AlertRaised:
                return $"{room} raised a {SuggestionWording.Words(trigger.Detail)} alert";

            case TriggerKind.TrustBand:
                return $"{room}'s trust fell into {trigger.Detail}";

            case TriggerKind.CriticalReading:
                return $"{room} reported a critical reading ({trigger.Detail})";

            default:
                return $"{room}: {trigger.Kind}";
        }
    }

    private bool IsDismissed(Suggestion suggestion) =>
        _dismissedAt.TryGetValue(suggestion.Id, out var at) && suggestion.TriggeredAt <= at;

    /// <summary>
    /// A short id for one suggestion: what it is about (this sensor crossing this level) and what it says to do. The same for every
    /// occurrence, so a trigger that fires again does not make a new suggestion, and a client can tell new from repeated. Hashed so it
    /// is safe in a URL.
    /// </summary>
    private static string Identify(string trigger, string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{trigger}|{identity}")))[..16].ToLowerInvariant();

    public override void Dispose()
    {
        _patterns.TriggerRecorded -= OnTrigger;
        _patterns.ActionRecorded -= OnAction;

        base.Dispose();
    }

    // ---- what is held -------------------------------------------------------------------------------------------

    private sealed class LiveTrigger(long id, TriggerEvent trigger)
    {
        public long Id { get; } = id;

        public TriggerEvent Event { get; set; } = trigger;

        /// <summary>What it is about, apart from when: the sensor, the kind of trigger and which one.</summary>
        public (TriggerKind Kind, string Mac, string Detail) Key => Event.Key();

        /// <summary>The engine's learned version when this trigger last had no qualified pattern, or -1 if it has not been found without one.</summary>
        public long EmptyAtVersion { get; set; } = -1;

        /// <summary>The actions taken since this trigger fired. A set, so "already done" is one lookup.</summary>
        public HashSet<ActionKey> Taken { get; } = [];
    }

    /// <summary>
    /// A pattern, for the purpose of remembering what was said about it: which trigger it is about, and which action follows.
    /// A value type with value equality, like the engine's keys, so it is a dictionary key without being boxed.
    /// </summary>
    private readonly record struct PatternRef(
        PatternLevel Level,
        TriggerKind Trigger,
        string Detail,
        string TriggerScope,
        OperatorActionKind Action,
        string ActionTarget,
        string Command);

    private sealed record Candidate(string Identity, PatternRef Pattern, Suggestion Suggestion);

    private sealed record Dismissal(string Id, PatternRef Pattern, Suggestion Suggestion, DateTimeOffset At, double Before, double After);
}

/// <summary>The words and numbers that go with suggestions and insights.</summary>
internal static class SuggestionWording
{
    /// <summary>What a trigger is about, apart from when it happened.</summary>
    public static (TriggerKind Kind, string Mac, string Detail) Key(this TriggerEvent trigger) => (trigger.Kind, trigger.MacAddress, trigger.Detail);

    /// <summary>What to say on a button for a command, or null for one that cannot be sent without more input.</summary>
    public static string? Verb(CommandAction action) => action switch
    {
        CommandAction.PumpOn => "Turn on",
        CommandAction.PumpOff => "Turn off",
        CommandAction.RelayOn => "Switch on",
        CommandAction.RelayOff => "Switch off",
        CommandAction.ValveOpen => "Open",
        CommandAction.ValveClose => "Close",
        _ => null,
    };

    /// <summary>How urgent a trigger is, from 0 to 1.</summary>
    public static double Severity(TriggerEvent trigger, SeverityOptions options) => trigger.Kind switch
    {
        TriggerKind.CriticalReading => options.CriticalReading,
        TriggerKind.AlertRaised => options.AlertRaised,
        TriggerKind.TrustBand => string.Equals(trigger.Detail, nameof(TrustBand.Fault), StringComparison.Ordinal) ? options.Fault : options.Degrading,
        _ => options.ThresholdCrossing,
    };

    /// <summary>"OutOfRange" as "out-of-range", for a sentence.</summary>
    public static string Words(string name)
    {
        var builder = new StringBuilder();

        foreach (var letter in name)
        {
            if (char.IsUpper(letter) && builder.Length > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(letter));
        }

        return builder.ToString();
    }
}
