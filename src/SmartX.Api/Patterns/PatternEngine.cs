using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>What a query for patterns can narrow by.</summary>
/// <param name="Trigger">Only patterns about this kind of trigger.</param>
/// <param name="MacAddress">Only patterns about this device, whether learned about it or about its kind in its zone.</param>
/// <param name="Level">Only patterns at this level.</param>
/// <param name="IncludeWeak">Also return associations that have not cleared the support and lift thresholds.</param>
internal sealed record PatternQuery(
    TriggerKind? Trigger = null,
    string? MacAddress = null,
    PatternLevel? Level = null,
    bool IncludeWeak = false);

/// <summary>
/// Learns which operator actions tend to follow which system events.
/// </summary>
/// <remarks>
/// An interface with no reference to SignalR or to any transport, like the other domain services: the engine is told what
/// happened and answers what it has learned, and who tells it and who listens is not its concern.
/// </remarks>
internal interface IPatternEngine
{
    /// <summary>Tells the engine something happened that an operator might respond to.</summary>
    void RecordTrigger(TriggerEvent trigger);

    /// <summary>Tells the engine an operator did something.</summary>
    void RecordAction(OperatorAction action);

    /// <summary>The learned associations that match a query, best first.</summary>
    IReadOnlyList<PatternAssociation> Associations(PatternQuery query, DateTimeOffset now);

    /// <summary>What tends to follow one kind of trigger on one device, preferring what was learned about the device itself.</summary>
    PatternAnswer Predict(TriggerKind kind, string detail, string macAddress, DateTimeOffset now);

    /// <summary>What tends to follow each kind of trigger that has been seen on a device or on its kind in its zone.</summary>
    IReadOnlyList<PatternAnswer> ForDevice(string macAddress, DateTimeOffset now);

    /// <summary>
    /// Learns from a history that happened in the past, in the order it happened, without touching the live window.
    /// </summary>
    /// <remarks>
    /// For the seeded operator history. The events are applied with their own times, so the counts fade from when each thing
    /// happened and not from when it was replayed, and they are paired only with each other: a replayed action must not be
    /// matched against a live trigger that came after it.
    /// </remarks>
    void Replay(IEnumerable<HistoricEvent> history);

    /// <summary>
    /// A number that goes up each time the engine learns something: an action that counted. Lets a caller that last looked
    /// at an unchanged engine skip asking again.
    /// </summary>
    long LearnedVersion { get; }

    /// <summary>Raised after an observed trigger was recorded. Not raised for replayed history.</summary>
    event Action<TriggerEvent>? TriggerRecorded;

    /// <summary>Raised after an observed operator action was recorded, whether or not the engine learned from it.</summary>
    event Action<OperatorAction>? ActionRecorded;

    /// <summary>The thresholds an association has to clear.</summary>
    PatternSettings Settings { get; }

    /// <summary>How many triggers are in the window now. For tests and the benchmark: the cost of an action is proportional to it.</summary>
    int TriggersInWindow { get; }
}

/// <summary>A count that fades with time, and is faded only when it is read.</summary>
/// <remarks>
/// <b>Lazy decay.</b> Fading every count on a timer would touch every entry every tick, whether anything had changed or not.
/// Instead each count remembers when it was last changed, and is scaled by how long it has been since, when somebody asks
/// for it. A count nobody reads costs nothing. A struct stored in the dictionary by reference (see
/// <see cref="System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault{TKey,TValue}"/>), so adding to one
/// allocates nothing.
/// </remarks>
internal struct DecayedCount
{
    public double Value;
    public DateTimeOffset At;

    /// <summary>When the thing counted last happened. Only used by coverage, which needs the gap since the last time.</summary>
    public DateTimeOffset Last;

    /// <summary>How many times it was counted, not faded: the number an operator can read as "7 times".</summary>
    public int Times;

    /// <summary>How many of those were part of the seeded history.</summary>
    public int SeededTimes;

    /// <summary>What the count is worth at a moment, after fading.</summary>
    public readonly double ValueAt(DateTimeOffset now, TimeSpan halfLife) =>
        Value <= 0 ? 0 : Value * TrustFormula.DecayWeight(now - At, halfLife);

    /// <summary>
    /// Adds to the count as of the moment the thing happened, which need not be the latest moment the count has seen.
    /// </summary>
    /// <remarks>
    /// <b>Decay runs from event time.</b> The value is kept as of <see cref="At"/>. An addition at or after it fades the
    /// existing value forward and adds the amount at full weight, as before. An addition from before it, which is what
    /// replaying a seeded history after something live has been counted looks like, instead fades the <i>amount</i> forward
    /// to <see cref="At"/>, so a habit seen five days ago is worth what a habit seen five days ago is worth, whenever the
    /// engine was told about it. Nothing here reads the wall clock.
    /// </remarks>
    public void Add(double amount, DateTimeOffset moment, TimeSpan halfLife, bool seeded = false)
    {
        if (moment >= At)
        {
            Value = ValueAt(moment, halfLife) + amount;
            At = moment;
        }
        else
        {
            Value += amount * TrustFormula.DecayWeight(At - moment, halfLife);
        }

        if (amount > 0)
        {
            Times++;

            if (seeded)
            {
                SeededTimes++;
            }
        }
    }

    /// <summary>
    /// Adds the stretch of time an occurrence at this moment newly accounts for: the window, or the gap since the last
    /// occurrence if that is shorter. The first occurrence accounts for the whole window.
    /// </summary>
    public void AddCoverage(TimeSpan window, DateTimeOffset now, TimeSpan halfLife)
    {
        // An occurrence from before the last one is already inside the stretch that one accounts for, up to the window.
        if (now < Last)
        {
            return;
        }

        var gap = now - Last;

        Add((gap < window ? gap : window).TotalSeconds, now, halfLife);
        Last = now;
    }
}

/// <summary>
/// The pattern engine: windowed pairing of triggers and actions, counted once per pairing, scored by support, confidence and
/// lift, faded with a half-life, and learned at two levels of generality.
/// </summary>
/// <remarks>
/// <b>The window is a queue.</b> Recent triggers sit in a <see cref="Queue{T}"/> in time order, the same FIFO the ingestion lane
/// uses, for a different job. An action removes everything older than the window from the front (one comparison per entry,
/// because the oldest is first) and is paired with every trigger left. Nothing is searched for.
/// <para>
/// <b>Each pairing is counted once.</b> Opening Pump A four times after one moisture drop is one response. A
/// <see cref="HashSet{T}"/> of <c>(trigger instance, action)</c> pairs makes the second to fourth opening add nothing, since
/// <c>Add</c> returning false is the whole decision. Entries leave when their trigger leaves the window, so the set is bounded.
/// </para>
/// <para>
/// <b>Lift separates a habit from a coincidence.</b> If Pump A is open all day it follows nearly every trigger, and confidence
/// alone would recommend it everywhere. Lift divides confidence by the chance of the action in any window of the same length;
/// only a lift above one means the trigger made the difference. That chance is the share of time within a window of an
/// occurrence of the action (see <see cref="BaselineOf"/>), so a burst counts once and constant activity covers everything.
/// </para>
/// <para>
/// <b>Two levels.</b> Every event updates a specific key (this device) and a general one (this kind of device in this zone). The
/// specific level learns slowly and exactly, the general level quickly and loosely, and it answers for a device with no incident
/// of its own. One lock covers it all: each operation is a few lookups and a walk of the bounded window.
/// </para>
/// </remarks>
internal sealed class PatternEngine : IPatternEngine
{
    // The cap on a lift that has no baseline to divide by. An action that has never happened except after this trigger has
    // no "anyway" rate; it is capped so the number stays finite and sortable, not because 100 means anything exact.
    private const double MaximumLift = 100;

    private readonly Lock _gate = new();
    private readonly PatternOptions _options;
    private readonly IDeviceDirectory _devices;

    // The recent triggers, oldest first. A Queue<T> because the only operations are "add to the back" and "remove from the
    // front while old", and both are O(1).
    private Queue<TriggerInstance> _window = new();

    // Pairings already counted: (trigger instance, action). A set, so that counting the same one twice is impossible.
    private HashSet<(long Instance, ActionKey Action)> _paired = [];

    // How often each action followed each trigger, how often each trigger happened, and how much time each action covers
    // with its windows (the baseline for lift). Doubles, because they fade.
    private readonly Dictionary<TriggerKey, Dictionary<ActionKey, DecayedCount>> _followed = [];
    private readonly Dictionary<TriggerKey, DecayedCount> _triggers = [];
    private readonly Dictionary<ActionKey, DecayedCount> _actions = [];

    // Words for the keys, kept as they are first met, so a response can say "pump" and "Zone 1" and not a key.
    private readonly Dictionary<string, string> _scopeLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _targetLabels = new(StringComparer.Ordinal);

    // Reused by every action, under the lock: the latest trigger of each kind in the window. See LearnAction.
    private readonly Dictionary<TriggerKey, TriggerInstance> _latestSpecific = [];
    private readonly Dictionary<TriggerKey, TriggerInstance> _latestGeneral = [];

    private long _nextInstance;
    private long _learnedVersion;
    private int _pairs;
    private int _pruneAfter;
    private DateTimeOffset? _watchingSince;
    private DateTimeOffset _latest;

    public PatternEngine(IOptions<PatternOptions> options, IDeviceDirectory devices)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(devices);

        _options = options.Value;
        _devices = devices;

        Settings = new PatternSettings(_options.Window, _options.MinimumSupport, _options.MinimumLift, _options.HalfLife);
    }

    public PatternSettings Settings { get; }

    public int TriggersInWindow
    {
        get
        {
            lock (_gate)
            {
                return _window.Count;
            }
        }
    }

    // ---- learning ---------------------------------------------------------------------------------------------------

    public event Action<TriggerEvent>? TriggerRecorded;

    public event Action<OperatorAction>? ActionRecorded;

    public long LearnedVersion => Interlocked.Read(ref _learnedVersion);

    public void RecordTrigger(TriggerEvent trigger)
    {
        var known = _devices.TryDescribe(trigger.MacAddress, out var profile);

        lock (_gate)
        {
            LearnTrigger(trigger, known, profile);
        }

        // Announced outside the lock, and not for a replay: a subscriber that recomputes something from here must be able to
        // ask the engine a question without waiting for the engine to finish telling it.
        if (trigger.Source == EventSource.Observed)
        {
            TriggerRecorded?.Invoke(trigger);
        }
    }

    public void RecordAction(OperatorAction action)
    {
        var known = _devices.TryDescribe(action.MacAddress, out var profile);

        lock (_gate)
        {
            LearnAction(action, known, profile);
        }

        if (action.Source == EventSource.Observed)
        {
            ActionRecorded?.Invoke(action);
        }
    }

    public void Replay(IEnumerable<HistoricEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        // Oldest first, and triggers before actions that share a moment, so a response is never counted before its cause.
        var ordered = history.OrderBy(item => item.At).ThenBy(item => item.Trigger is null ? 1 : 0).ToList();

        if (ordered.Count == 0)
        {
            return;
        }

        // Devices are looked up before the lock is taken, as for a live event, so the engine's lock is never held while
        // another component's lock is asked for.
        Dictionary<string, DeviceProfile?> profiles = new(StringComparer.Ordinal);

        foreach (var item in ordered)
        {
            var mac = item.Trigger?.MacAddress ?? item.Action!.Value.MacAddress;

            if (!profiles.ContainsKey(mac))
            {
                profiles[mac] = _devices.TryDescribe(mac, out var found) ? found : null;
            }
        }

        lock (_gate)
        {
            // The live window and its set of counted pairs are put aside and restored afterwards. History is paired with
            // itself, in a window of its own, so that a replayed action cannot be matched against a live trigger that
            // happened after it, and a replayed trigger cannot be answered by a live action.
            var liveWindow = _window;
            var livePaired = _paired;
            var liveLatest = _latest;

            _window = new Queue<TriggerInstance>();
            _paired = [];
            _latest = DateTimeOffset.MinValue;

            // The engine has been "watching" since the history began, which is what the baseline for lift is measured over.
            if (_watchingSince is not { } since || ordered[0].At < since)
            {
                _watchingSince = ordered[0].At;
            }

            try
            {
                foreach (var item in ordered)
                {
                    if (item.Trigger is { } trigger)
                    {
                        var profile = profiles[trigger.MacAddress];
                        LearnTrigger(trigger, profile is not null, profile);
                    }
                    else if (item.Action is { } action)
                    {
                        var profile = profiles[action.MacAddress];
                        LearnAction(action, profile is not null, profile);
                    }
                }
            }
            finally
            {
                _window = liveWindow;
                _paired = livePaired;
                _latest = liveLatest;
            }
        }
    }

    /// <summary>Counts a trigger and puts it in the window. Caller holds the lock.</summary>
    private void LearnTrigger(TriggerEvent trigger, bool known, DeviceProfile? profile)
    {
        var seeded = trigger.Source == EventSource.Seeded;
        var now = Advance(trigger.At);
        Expire(now);

        var specific = new TriggerKey(PatternLevel.Specific, trigger.Kind, trigger.MacAddress, trigger.Detail);
        TriggerKey? general = null;

        Count(_triggers, specific, 1, now, seeded);
        _scopeLabels.TryAdd(specific.Scope, known ? profile!.Room : trigger.MacAddress);

        if (known)
        {
            var scope = GeneralScope(profile!);

            general = new TriggerKey(PatternLevel.General, trigger.Kind, scope, trigger.Detail);
            Count(_triggers, general.Value, 1, now, seeded);
            _scopeLabels.TryAdd(scope, $"{profile!.ClassLabel} in {profile.GeneralZoneName}");
        }

        // Bounded. Past the cap the oldest triggers are dropped as if they had aged out, so a flood of triggers makes
        // the window shorter, not an action slower.
        while (_window.Count >= _options.MaxTriggersInWindow)
        {
            Remove(_window.Dequeue());
        }

        _window.Enqueue(new TriggerInstance(++_nextInstance, now, specific, general, known ? profile!.GeneralZoneId : null));
    }

    /// <summary>Counts an action against every trigger in the window. Caller holds the lock.</summary>
    private void LearnAction(OperatorAction action, bool known, DeviceProfile? profile)
    {
        var weight = WeightOf(action);

        // An action taken on the system's own advice teaches it nothing by default (see AcceptedSuggestionWeight): it is
        // neither a response to the trigger nor one more occurrence of the action, since counting it as the second would
        // raise the baseline and make a genuine habit look weaker.
        if (weight <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _learnedVersion);

        var seeded = action.Source == EventSource.Seeded;
        var now = Advance(action.At);
        Expire(now);

        var specific = new ActionKey(PatternLevel.Specific, action.Kind, action.MacAddress, action.Command);
        ActionKey? general = null;

        Cover(_actions, specific, now);
        _targetLabels.TryAdd(specific.Target, known ? profile!.Room : action.MacAddress);

        if (known)
        {
            // A kind of device in a zone, so "a pump in Zone 1" has its own baseline: how often a pump in that zone is
            // opened, and not how often any pump anywhere is. Otherwise a pump opened all day in another zone would make
            // every pump look as if it follows everything.
            var target = GeneralScope(profile!);

            general = new ActionKey(PatternLevel.General, action.Kind, target, action.Command);
            Cover(_actions, general.Value, now);
            _targetLabels.TryAdd(target, $"{profile!.ClassLabel} in {profile.GeneralZoneName}");
        }

        // Every trigger still in the window is a candidate cause, but an action answers each kind of trigger once. A sensor that
        // fires a dozen times in ten minutes, and an operator who does one thing in that time, is one response to a noisy sensor
        // and not twelve: crediting the action to every firing would make a single click look like a habit that held twelve times
        // out of twelve. So the latest firing of each kind is found (the queue is oldest first, so a later one replaces an earlier
        // one in the scratch dictionary, which is reused so the walk allocates nothing once it is warm) and the action is paired
        // with those. Every firing is still counted as a trigger, which is what makes the noisy sensor's confidence low, as it should be.
        _latestSpecific.Clear();
        _latestGeneral.Clear();

        foreach (var instance in _window)
        {
            _latestSpecific[instance.Specific] = instance;

            // The general pattern is about a kind of device in the same zone. An action on a device somewhere else
            // is not evidence for it, though it can still be evidence at the specific level.
            if (general is not null && instance.General is { } generalTrigger && instance.GeneralZone == profile!.GeneralZoneId)
            {
                _latestGeneral[generalTrigger] = instance;
            }
        }

        foreach (var (trigger, instance) in _latestSpecific)
        {
            Pair(instance, trigger, specific, weight, now, seeded);
        }

        foreach (var (trigger, instance) in _latestGeneral)
        {
            Pair(instance, trigger, general!.Value, weight, now, seeded);
        }
    }

    private double WeightOf(OperatorAction action)
    {
        var weight = action.Kind == OperatorActionKind.Searched ? _options.SearchWeight : 1;

        return action.FromSuggestion ? weight * _options.AcceptedSuggestionWeight : weight;
    }

    /// <summary>Counts an action as following a trigger, once per trigger instance.</summary>
    private void Pair(TriggerInstance instance, TriggerKey trigger, ActionKey action, double weight, DateTimeOffset now, bool seeded)
    {
        if (!_paired.Add((instance.Id, action)))
        {
            return;
        }

        instance.Paired.Add(action);

        if (!_followed.TryGetValue(trigger, out var actions))
        {
            actions = [];
            _followed.Add(trigger, actions);
        }

        if (Count(actions, action, weight, now, seeded) && ++_pairs > Math.Max(_options.MaxLearnedPairs, _pruneAfter))
        {
            Prune(now);
        }
    }

    /// <returns>Whether this was a count that did not exist before.</returns>
    private bool Count<TKey>(Dictionary<TKey, DecayedCount> counts, TKey key, double amount, DateTimeOffset now, bool seeded = false)
        where TKey : notnull
    {
        // A reference into the dictionary's own storage: the struct is changed where it lives, with no copy and no
        // allocation, which is the point of a struct here.
        ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, key, out var existed);

        count.Add(amount, now, _options.HalfLife, seeded);

        return !existed;
    }

    /// <summary>Records that an action happened, as the stretch of time it newly puts a window around. See <see cref="BaselineOf"/>.</summary>
    private void Cover(Dictionary<ActionKey, DecayedCount> coverage, ActionKey key, DateTimeOffset now)
    {
        ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(coverage, key, out _);

        count.AddCoverage(_options.Window, now, _options.HalfLife);
    }

    /// <summary>Forgets the most faded pairs until the table fits its bound, if enough of them have faded. Caller holds the lock.</summary>
    /// <remarks>
    /// Starts with pairs worth almost nothing and widens the net by a factor of ten each pass, up to a tenth of one pairing.
    /// Anything worth more than that is never forgotten to make room, so a burst of fresh pairs can take the table past its
    /// bound for a while rather than lose what has just been learned. After a pass that did not get under the bound the next
    /// one waits for a tenth of the bound's worth of new pairs, so a table that cannot shrink does not search itself on every
    /// event.
    /// </remarks>
    private void Prune(DateTimeOffset now)
    {
        for (var floor = 0.001; _pairs > _options.MaxLearnedPairs && floor <= 0.1; floor *= 10)
        {
            foreach (var trigger in _followed.Keys.ToList())
            {
                var actions = _followed[trigger];

                foreach (var action in actions.Keys.ToList())
                {
                    if (actions[action].ValueAt(now, _options.HalfLife) < floor)
                    {
                        actions.Remove(action);
                        _pairs--;
                    }
                }

                if (actions.Count == 0)
                {
                    _followed.Remove(trigger);
                }
            }
        }

        _pruneAfter = _pairs > _options.MaxLearnedPairs ? _pairs + Math.Max(1, _options.MaxLearnedPairs / 10) : 0;
    }

    /// <summary>Moves the engine's clock forward. A moment earlier than the latest seen is treated as the latest.</summary>
    private DateTimeOffset Advance(DateTimeOffset at)
    {
        _watchingSince ??= at;

        if (at > _latest)
        {
            _latest = at;
        }

        return _latest;
    }

    /// <summary>Removes the triggers that have aged out of the window, oldest first.</summary>
    private void Expire(DateTimeOffset now)
    {
        while (_window.Count > 0 && now - _window.Peek().At > _options.Window)
        {
            Remove(_window.Dequeue());
        }
    }

    private void Remove(TriggerInstance instance)
    {
        foreach (var action in instance.Paired)
        {
            _paired.Remove((instance.Id, action));
        }
    }

    private static string GeneralScope(DeviceProfile profile) => profile.GeneralScope;

    // ---- answering --------------------------------------------------------------------------------------------------

    public IReadOnlyList<PatternAssociation> Associations(PatternQuery query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);

        string? generalScope = null;

        if (query.MacAddress is { } mac && _devices.TryDescribe(mac, out var profile))
        {
            generalScope = GeneralScope(profile);
        }

        lock (_gate)
        {
            var found = new List<PatternAssociation>();

            foreach (var (trigger, actions) in _followed)
            {
                if (!Matches(trigger, query, generalScope))
                {
                    continue;
                }

                foreach (var (action, support) in actions)
                {
                    var association = Score(trigger, action, support, now);

                    if (query.IncludeWeak || association.Qualified)
                    {
                        found.Add(association);
                    }
                }
            }

            found.Sort(BestFirst);

            return found;
        }
    }

    private static bool Matches(TriggerKey trigger, PatternQuery query, string? generalScope)
    {
        if (query.Trigger is { } kind && trigger.Kind != kind)
        {
            return false;
        }

        if (query.Level is { } level && trigger.Level != level)
        {
            return false;
        }

        if (query.MacAddress is not { } mac)
        {
            return true;
        }

        return trigger.Level == PatternLevel.Specific
            ? string.Equals(trigger.Scope, mac, StringComparison.Ordinal)
            : generalScope is not null && string.Equals(trigger.Scope, generalScope, StringComparison.Ordinal);
    }

    public PatternAnswer Predict(TriggerKind kind, string detail, string macAddress, DateTimeOffset now)
    {
        var known = _devices.TryDescribe(macAddress, out var profile);

        lock (_gate)
        {
            var specific = new TriggerKey(PatternLevel.Specific, kind, macAddress, detail);
            var asked = Describe(specific);

            // The specific pattern answers when it has enough to go on. Only then is it preferred: a device with one
            // coincidental pairing falls through to what is known about its kind.
            var fromDevice = Qualified(specific, now);

            if (fromDevice.Count > 0)
            {
                return new PatternAnswer(asked, PatternLevel.Specific, fromDevice);
            }

            if (known)
            {
                var general = new TriggerKey(PatternLevel.General, kind, GeneralScope(profile), detail);
                var fromKind = Qualified(general, now);

                if (fromKind.Count > 0)
                {
                    return new PatternAnswer(Describe(general), PatternLevel.General, fromKind);
                }
            }

            return new PatternAnswer(asked, null, []);
        }
    }

    public IReadOnlyList<PatternAnswer> ForDevice(string macAddress, DateTimeOffset now)
    {
        var known = _devices.TryDescribe(macAddress, out var profile);
        var generalScope = known ? GeneralScope(profile) : null;
        HashSet<(TriggerKind Kind, string Detail)> seen = [];

        lock (_gate)
        {
            foreach (var trigger in _triggers.Keys)
            {
                var ofThisDevice = trigger.Level == PatternLevel.Specific
                    ? string.Equals(trigger.Scope, macAddress, StringComparison.Ordinal)
                    : generalScope is not null && string.Equals(trigger.Scope, generalScope, StringComparison.Ordinal);

                if (ofThisDevice)
                {
                    seen.Add((trigger.Kind, trigger.Detail));
                }
            }
        }

        // Asked one trigger at a time, so each gets the specific answer where there is one and the general one where there
        // is not, and says which.
        return
        [
            .. seen
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Detail, StringComparer.Ordinal)
                .Select(item => Predict(item.Kind, item.Detail, macAddress, now))
                .Where(answer => answer.AnsweredBy is not null),
        ];
    }

    private List<PatternAssociation> Qualified(TriggerKey trigger, DateTimeOffset now)
    {
        var found = new List<PatternAssociation>();

        if (_followed.TryGetValue(trigger, out var actions))
        {
            foreach (var (action, support) in actions)
            {
                var association = Score(trigger, action, support, now);

                if (association.Qualified)
                {
                    found.Add(association);
                }
            }
        }

        found.Sort(BestFirst);

        return found;
    }

    /// <summary>Support, confidence and lift for one trigger and one action. Caller holds the lock.</summary>
    private PatternAssociation Score(TriggerKey trigger, ActionKey action, DecayedCount followed, DateTimeOffset now)
    {
        var support = followed.ValueAt(now, _options.HalfLife);
        var triggerCount = _triggers.TryGetValue(trigger, out var counted) ? counted.ValueAt(now, _options.HalfLife) : 0;
        var triggerTimes = counted.Times;
        var seededTriggerTimes = counted.SeededTimes;

        // Capped at one. The two counts fade from different moments, so their ratio can stray a hair above one, and a
        // probability above one is not a thing to show.
        var confidence = triggerCount > 0 ? Math.Min(1, support / triggerCount) : 0;
        var baseline = BaselineOf(action, now);
        var lift = baseline > 0 ? Math.Min(MaximumLift, confidence / baseline) : confidence > 0 ? MaximumLift : 0;

        var qualified = support >= _options.MinimumSupport * _options.SupportTolerance && lift >= _options.MinimumLift;

        return new PatternAssociation(
            Describe(trigger),
            DescribeAction(action),
            trigger.Level,
            support,
            confidence,
            lift,
            triggerCount,
            baseline,
            qualified,
            followed.Times,
            followed.SeededTimes,
            triggerTimes,
            seededTriggerTimes);
    }

    /// <summary>
    /// The chance that an action happens in a window of the configured length, whatever the trigger: how likely it is
    /// "anyway".
    /// </summary>
    /// <remarks>
    /// The chance that a window starting at a random moment contains the action is the share of time that is within a window
    /// of an occurrence. An action at time t is inside the window of any trigger in the stretch from t minus the window to
    /// t, so the time covered is the union of those stretches. Counted as the occurrences arrive in order, each adds the
    /// window, or the gap since the last one if that is shorter, because the rest was already covered. So a device opened
    /// three times in ten seconds covers one window plus a few seconds, not three windows; a device opened every two minutes
    /// covers all of the time; and one opened now and then covers a window each time.
    /// <para>
    /// The covered time and the watching time both fade with the same half-life, so they stay comparable. The watching time is
    /// never taken as shorter than one window, so an engine that has only just started does not conclude that a few
    /// events are everything that ever happens.
    /// </para>
    /// </remarks>
    private double BaselineOf(ActionKey action, DateTimeOffset now)
    {
        if (!_actions.TryGetValue(action, out var covered) || _watchingSince is not { } since)
        {
            return 0;
        }

        var seconds = Math.Max(EffectiveSpan(now - since).TotalSeconds, _options.Window.TotalSeconds);

        return Math.Min(1, covered.ValueAt(now, _options.HalfLife) / seconds);
    }

    /// <summary>The length of an elapsed period as the fading counts see it: each moment weighted by how faded it is now.</summary>
    private TimeSpan EffectiveSpan(TimeSpan elapsed)
    {
        // The integral of 2^(-t/H) for t from 0 to the elapsed time, which is (H / ln 2) x (1 - 2^(-elapsed/H)).
        var half = _options.HalfLife.TotalSeconds;

        return TimeSpan.FromSeconds(half / Math.Log(2) * (1 - Math.Pow(0.5, elapsed.TotalSeconds / half)));
    }

    private static int BestFirst(PatternAssociation left, PatternAssociation right)
    {
        // Ones that count come before ones that do not. Lift alone cannot put them in that order: it is a ratio of two counts
        // that fade together, so a habit that has all but vanished keeps its lift while its support goes to nothing.
        var byQualified = right.Qualified.CompareTo(left.Qualified);

        if (byQualified != 0)
        {
            return byQualified;
        }

        var byLift = right.Lift.CompareTo(left.Lift);

        if (byLift != 0)
        {
            return byLift;
        }

        var byConfidence = right.Confidence.CompareTo(left.Confidence);

        return byConfidence != 0 ? byConfidence : right.Support.CompareTo(left.Support);
    }

    private PatternTrigger Describe(TriggerKey key) =>
        new(key.Kind, key.Detail, key.Scope, _scopeLabels.GetValueOrDefault(key.Scope, key.Scope));

    private PatternAction DescribeAction(ActionKey key) =>
        new(key.Kind, key.Target, _targetLabels.GetValueOrDefault(key.Target, key.Target), key.Command);

    /// <summary>A trigger waiting in the window, with the actions already counted against it.</summary>
    private sealed class TriggerInstance(long id, DateTimeOffset at, TriggerKey specific, TriggerKey? general, string? generalZone)
    {
        public long Id { get; } = id;

        public DateTimeOffset At { get; } = at;

        public TriggerKey Specific { get; } = specific;

        public TriggerKey? General { get; } = general;

        public string? GeneralZone { get; } = generalZone;

        /// <summary>The actions counted against this trigger, so their entries can leave the set when it ages out.</summary>
        public List<ActionKey> Paired { get; } = [];
    }
}
