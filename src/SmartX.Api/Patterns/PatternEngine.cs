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

    /// <summary>What the count is worth at a moment, after fading.</summary>
    public readonly double ValueAt(DateTimeOffset now, TimeSpan halfLife) =>
        Value <= 0 ? 0 : Value * TrustFormula.DecayWeight(now - At, halfLife);

    /// <summary>Fades the count to a moment, then adds to it.</summary>
    public void Add(double amount, DateTimeOffset now, TimeSpan halfLife)
    {
        Value = ValueAt(now, halfLife) + amount;
        At = now;
    }

    /// <summary>
    /// Adds the stretch of time an occurrence at this moment newly accounts for: the window, or the gap since the last
    /// occurrence if that is shorter. The first occurrence accounts for the whole window.
    /// </summary>
    public void AddCoverage(TimeSpan window, DateTimeOffset now, TimeSpan halfLife)
    {
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
/// <b>The window is a queue.</b> Recent triggers are held in a <see cref="Queue{T}"/> in the order they happened, which is
/// the same FIFO structure the ingestion pipeline's standard lane uses, put to a different job. When an action arrives,
/// everything older than the window is removed from the front, which costs one comparison per entry removed because the
/// oldest is always first, and the action is then paired with every trigger still in the queue. Nothing is searched for.
/// <para>
/// <b>Each pairing is counted once.</b> An operator who opens Pump A four times after one moisture drop has responded
/// once. A <see cref="HashSet{T}"/> of <c>(trigger instance, action)</c> pairs already counted makes the second to fourth
/// opening add nothing; <c>Add</c> returning false is the whole decision. Entries are removed when their trigger leaves the
/// window, so the set is bounded by the window too.
/// </para>
/// <para>
/// <b>Lift is what separates a habit from a coincidence.</b> If an operator has Pump A open all day, it follows nearly every
/// trigger, and confidence alone would recommend it everywhere. Lift divides confidence by the chance of the action in any
/// window of the same length, and only a lift above one means the trigger made the difference. That chance is the share of
/// time that falls within a window of an occurrence of the action (see <see cref="BaselineOf"/>), counted so that a burst is
/// not many occurrences and constant activity covers everything, over the faded length of time the engine has been watching,
/// so the two share one clock and one decay.
/// </para>
/// <para>
/// <b>Two levels.</b> Every event updates a specific key (this device) and a general one (this kind of device, in this zone).
/// The specific level learns slowly and exactly; the general level learns quickly and loosely, and answers for a device that
/// has never had an incident of its own.
/// </para>
/// <para>
/// One lock. Every operation is a few dictionary lookups and a walk of the window, which is bounded, so the lock is held for
/// microseconds.
/// </para>
/// </remarks>
internal sealed class PatternEngine : IPatternEngine
{
    // The cap on a lift that has no baseline to divide by. An action that has never happened except after this trigger has
    // no "anyway" rate; it is capped so the number stays finite and sortable, not because 100 means anything exact.
    private const double MaximumLift = 100;

    // Support is a faded count, so three pairings a minute ago add up to 2.9998 and not 3. Without a little give, "three" would
    // mean "three, all of them brand new", and a pattern that had earned its place would drop out in the time it takes to
    // make a cup of tea. Two percent is about three hours of fading at a week's half-life: a count that has faded by less than
    // that is still the count it was.
    private const double SupportTolerance = 0.98;

    private readonly Lock _gate = new();
    private readonly PatternOptions _options;
    private readonly IDeviceDirectory _devices;

    // The recent triggers, oldest first. A Queue<T> because the only operations are "add to the back" and "remove from the
    // front while old", and both are O(1).
    private readonly Queue<TriggerInstance> _window = new();

    // Pairings already counted: (trigger instance, action). A set, so that counting the same one twice is impossible.
    private readonly HashSet<(long Instance, ActionKey Action)> _paired = [];

    // How often each action followed each trigger, how often each trigger happened, and how much time each action covers
    // with its windows (the baseline for lift). Doubles, because they fade.
    private readonly Dictionary<TriggerKey, Dictionary<ActionKey, DecayedCount>> _followed = [];
    private readonly Dictionary<TriggerKey, DecayedCount> _triggers = [];
    private readonly Dictionary<ActionKey, DecayedCount> _actions = [];

    // Words for the keys, kept as they are first met, so a response can say "pump" and "Zone 1" and not a key.
    private readonly Dictionary<string, string> _scopeLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _targetLabels = new(StringComparer.Ordinal);

    private long _nextInstance;
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

    public void RecordTrigger(TriggerEvent trigger)
    {
        var known = _devices.TryDescribe(trigger.MacAddress, out var profile);

        lock (_gate)
        {
            var now = Advance(trigger.At);
            Expire(now);

            var specific = new TriggerKey(PatternLevel.Specific, trigger.Kind, trigger.MacAddress, trigger.Detail);
            TriggerKey? general = null;

            Count(_triggers, specific, 1, now);
            _scopeLabels.TryAdd(specific.Scope, known ? profile.Room : trigger.MacAddress);

            if (known)
            {
                var scope = GeneralScope(profile);

                general = new TriggerKey(PatternLevel.General, trigger.Kind, scope, trigger.Detail);
                Count(_triggers, general.Value, 1, now);
                _scopeLabels.TryAdd(scope, $"{profile.ClassLabel} in {profile.GeneralZoneName}");
            }

            // Bounded. Past the cap the oldest triggers are dropped as if they had aged out, so a flood of triggers makes
            // the window shorter, not an action slower.
            while (_window.Count >= _options.MaxTriggersInWindow)
            {
                Remove(_window.Dequeue());
            }

            _window.Enqueue(new TriggerInstance(++_nextInstance, now, specific, general, known ? profile.GeneralZoneId : null));
        }
    }

    public void RecordAction(OperatorAction action)
    {
        var known = _devices.TryDescribe(action.MacAddress, out var profile);
        var weight = WeightOf(action.Kind);

        lock (_gate)
        {
            var now = Advance(action.At);
            Expire(now);

            var specific = new ActionKey(PatternLevel.Specific, action.Kind, action.MacAddress, action.Command);
            ActionKey? general = null;

            Cover(_actions, specific, now);
            _targetLabels.TryAdd(specific.Target, known ? profile.Room : action.MacAddress);

            if (known)
            {
                general = new ActionKey(PatternLevel.General, action.Kind, profile.Class, action.Command);
                Cover(_actions, general.Value, now);
                _targetLabels.TryAdd(profile.Class, profile.ClassLabel);
            }

            // Every trigger still in the window is a candidate cause. foreach on a Queue<T> uses its struct enumerator,
            // so the walk allocates nothing.
            foreach (var instance in _window)
            {
                Pair(instance, instance.Specific, specific, weight, now);

                // The general pattern is about a kind of device in the same zone. An action on a device somewhere else
                // is not evidence for it, though it can still be evidence at the specific level.
                if (general is { } generalAction
                    && instance.General is { } generalTrigger
                    && instance.GeneralZone == profile.GeneralZoneId)
                {
                    Pair(instance, generalTrigger, generalAction, weight, now);
                }
            }
        }
    }

    private double WeightOf(OperatorActionKind kind) => kind == OperatorActionKind.Searched ? _options.SearchWeight : 1;

    /// <summary>Counts an action as following a trigger, once per trigger instance.</summary>
    private void Pair(TriggerInstance instance, TriggerKey trigger, ActionKey action, double weight, DateTimeOffset now)
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

        if (Count(actions, action, weight, now) && ++_pairs > Math.Max(_options.MaxLearnedPairs, _pruneAfter))
        {
            Prune(now);
        }
    }

    /// <returns>Whether this was a count that did not exist before.</returns>
    private bool Count<TKey>(Dictionary<TKey, DecayedCount> counts, TKey key, double amount, DateTimeOffset now)
        where TKey : notnull
    {
        // A reference into the dictionary's own storage: the struct is changed where it lives, with no copy and no
        // allocation, which is the point of a struct here.
        ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, key, out var existed);

        count.Add(amount, now, _options.HalfLife);

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

    private static string GeneralScope(DeviceProfile profile) => $"{profile.Class}@{profile.GeneralZoneId}";

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
                    var association = Score(trigger, action, support.ValueAt(now, _options.HalfLife), now);

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
                var association = Score(trigger, action, support.ValueAt(now, _options.HalfLife), now);

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
    private PatternAssociation Score(TriggerKey trigger, ActionKey action, double support, DateTimeOffset now)
    {
        var triggerCount = _triggers.TryGetValue(trigger, out var counted) ? counted.ValueAt(now, _options.HalfLife) : 0;

        // Capped at one. The two counts fade from different moments, so their ratio can stray a hair above one, and a
        // probability above one is not a thing to show.
        var confidence = triggerCount > 0 ? Math.Min(1, support / triggerCount) : 0;
        var baseline = BaselineOf(action, now);
        var lift = baseline > 0 ? Math.Min(MaximumLift, confidence / baseline) : confidence > 0 ? MaximumLift : 0;

        var qualified = support >= _options.MinimumSupport * SupportTolerance && lift >= _options.MinimumLift;

        return new PatternAssociation(
            Describe(trigger),
            DescribeAction(action),
            trigger.Level,
            support,
            confidence,
            lift,
            triggerCount,
            baseline,
            qualified);
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
