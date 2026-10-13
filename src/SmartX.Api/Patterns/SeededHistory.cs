using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>Settings for the synthetic operator history replayed at start-up.</summary>
/// <remarks>
/// Off unless switched on: the tests build their own history, and nothing synthetic should be in an engine that did not ask
/// for it. The Docker stack switches it on, because a freshly started engine has observed nothing, lift needs hours of
/// observation to mean anything, and a suggestion engine that shows nothing on first run reads as one that does not work.
/// </remarks>
public sealed class SeededHistoryOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "SeededHistory";

    /// <summary>Whether to replay the history at start-up.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many days back the history reaches.</summary>
    /// <remarks>
    /// Five. Long enough that decay has something to do (at a seven-day half-life the oldest habit is worth about 60 % of the
    /// newest) and short enough that it is plausibly a few days of a real operator's shift.
    /// </remarks>
    public int Days { get; set; } = 5;

    /// <summary>The seed for the history's randomness, so the same configuration always replays the same history.</summary>
    public int RandomSeed { get; set; } = 7312;

    /// <summary>How many times Moisture Sensor B dropped below 20 % in the history.</summary>
    public int BriefTriggers { get; set; } = 8;

    /// <summary>How many of those the operator answered by opening and turning on Pump A.</summary>
    /// <remarks>One fewer than the triggers by default: "7 of the last 8 times" is a habit, and "8 of 8" would be a rule.</remarks>
    public int BriefResponses { get; set; } = 7;

    /// <summary>How many times the other habits (the cold-store check and the power-spike inspection) happened.</summary>
    public int OtherHabitTriggers { get; set; } = 6;

    /// <summary>How many devices were opened at random times, as the background of a working day.</summary>
    public int RandomOpens { get; set; } = 80;

    /// <summary>How many times Pump A was opened as part of ordinary rounds, whether or not anything had happened.</summary>
    /// <remarks>
    /// What makes the example honest. A pump that was only ever opened after the sensor dropped would follow it a hundred times
    /// more often than chance, and the evidence would read as a certainty. A pump that is looked at on rounds as well, about ten
    /// times a day, follows the drop many times more often than chance and not infinitely: the lift is a ratio, and it is shown.
    /// </remarks>
    public int PumpARounds { get; set; } = 48;

    /// <summary>How many times Pump A was turned on on a schedule, nothing having happened.</summary>
    /// <remarks>The same reason as <see cref="PumpARounds"/>, for the command: a pump that is also run on a timetable is not turned on only when a sensor drops.</remarks>
    public int PumpAScheduledRuns { get; set; } = 40;

    /// <summary>How many threshold crossings happened that nobody responded to.</summary>
    public int UnansweredTriggers { get; set; } = 30;

    /// <summary>How often the one device that is opened all the time is opened, over the last day and a half.</summary>
    public TimeSpan ConstantOpenEvery { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>How long to wait for the devices the history is about to register before replaying what can be.</summary>
    public TimeSpan WaitForDevices { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How often to check whether they have registered.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>Checks the seeded history options at startup.</summary>
internal static class SeededHistoryValidation
{
    public static IEnumerable<string> Problems(SeededHistoryOptions options)
    {
        if (options.Days < 1)
        {
            yield return "SeededHistory:Days must be at least 1.";
        }

        if (options.BriefTriggers < 1 || options.BriefResponses < 0 || options.BriefResponses > options.BriefTriggers)
        {
            yield return "SeededHistory:BriefResponses must be between 0 and BriefTriggers, and BriefTriggers at least 1.";
        }

        if (options.OtherHabitTriggers < 0 || options.RandomOpens < 0 || options.UnansweredTriggers < 0 || options.PumpARounds < 0 || options.PumpAScheduledRuns < 0)
        {
            yield return "SeededHistory counts cannot be negative.";
        }

        if (options.ConstantOpenEvery <= TimeSpan.Zero || options.PollInterval <= TimeSpan.Zero)
        {
            yield return "SeededHistory:ConstantOpenEvery and PollInterval must be positive.";
        }
    }
}

/// <summary>
/// Builds the synthetic operator history: what an operator might have done over the last few days.
/// </summary>
/// <remarks>
/// <b>Why it exists.</b> The pattern engine learns from observation, and lift needs hours of it. On a first run nothing has
/// been observed, so without this the suggestion panel is empty until someone has used the system for a shift, which is
/// indistinguishable from a feature that is not there.
/// <para>
/// <b>What is in it.</b> The brief's own example several times (Moisture Sensor B drops below 20 %, and Pump A is opened and
/// turned on), two other habits (a chiller's temperature rises and its valve is checked; the compressor's power spikes and the
/// conveyor relay is inspected), background noise (devices opened at random, and threshold crossings nobody answered), and
/// one device opened all the time, which is what lift exists to filter out.
/// </para>
/// <para>
/// <b>It is labelled.</b> Every event carries <see cref="EventSource.Seeded"/>, the engine counts seeded and observed apart,
/// and the evidence shown to an operator says how much of it is seeded. Synthetic data does not pass for observed data.
/// </para>
/// <para>
/// A pure function of the clock and the options, with its own seeded random, so a test can build the same history twice and
/// get the same thing.
/// </para>
/// </remarks>
internal static class SeededHistory
{
    private static readonly string MoistureB = DemoDevices.Hydroponics.MoistureSensorB;
    private static readonly string PumpA = DemoDevices.Hydroponics.PumpA;
    private static readonly string PumpB = DemoDevices.Hydroponics.PumpB;
    private static readonly string Chiller1 = DemoDevices.Mac(0x1A);
    private static readonly string ChillerValveA = DemoDevices.Mac(0x2B);
    private static readonly string Compressor = DemoDevices.Mac(0x0C);
    private static readonly string LineOneConveyor = DemoDevices.Mac(0x0A);

    /// <summary>The devices the habits are about. The seeder waits for these before replaying.</summary>
    public static IReadOnlyList<string> HabitDevices { get; } =
        [MoistureB, PumpA, PumpB, Chiller1, ChillerValveA, Compressor, LineOneConveyor];

    /// <summary>Builds the history, ending at <paramref name="now"/>.</summary>
    public static IReadOnlyList<HistoricEvent> Build(DateTimeOffset now, SeededHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var random = new Random(options.RandomSeed);
        List<HistoricEvent> history = [];

        // The brief's example. Spread over the days with a little jitter, the last one a few hours before the engine
        // starts, so the habit is recent as well as old. Some triggers are left unanswered, spread through the history and
        // not bunched at one end, so the habit is "most of the time" and not "every time".
        var unanswered = Enumerable.Range(0, options.BriefTriggers - options.BriefResponses)
            .Select(step => (step * 2 + options.BriefTriggers / 2) % options.BriefTriggers)
            .ToHashSet();

        for (var index = 0; index < options.BriefTriggers; index++)
        {
            var at = SpreadOver(now, options, index, options.BriefTriggers, random);

            history.Add(Trigger(MoistureB, "Low", at));

            if (!unanswered.Contains(index))
            {
                var after = TimeSpan.FromSeconds(30 + random.Next(0, 150));

                history.Add(View(PumpA, at + after));
                history.Add(Command(PumpA, CommandAction.PumpOn, at + after + TimeSpan.FromSeconds(10 + random.Next(0, 30))));
            }
        }

        // The cold-store habit: a chiller's temperature climbs past its high level, and the operator checks its valve.
        for (var index = 0; index < options.OtherHabitTriggers; index++)
        {
            var at = SpreadOver(now, options, index, options.OtherHabitTriggers, random) + TimeSpan.FromMinutes(random.Next(0, 90));

            history.Add(Trigger(Chiller1, "High", at));
            history.Add(View(ChillerValveA, at + TimeSpan.FromSeconds(45 + random.Next(0, 120))));
        }

        // The power-spike habit: the compressor spikes, and the operator inspects the conveyor relay beside it.
        for (var index = 0; index < options.OtherHabitTriggers; index++)
        {
            var at = SpreadOver(now, options, index, options.OtherHabitTriggers, random) + TimeSpan.FromMinutes(random.Next(90, 180));

            history.Add(Trigger(Compressor, "High", at));
            history.Add(View(LineOneConveyor, at + TimeSpan.FromSeconds(30 + random.Next(0, 150))));
        }

        // Background: crossings nobody answered, on sensors that are not part of any habit.
        var sensors = DemoDevices.All
            .Where(device => device.Registration.Category != SensorCategory.Actuator && !HabitDevices.Contains(device.MacAddress))
            .Select(device => device.MacAddress)
            .ToList();

        for (var index = 0; index < options.UnansweredTriggers && sensors.Count > 0; index++)
        {
            history.Add(Trigger(sensors[random.Next(sensors.Count)], random.Next(2) == 0 ? "Low" : "High", RandomTime(now, options, random)));
        }

        // Background: devices opened at random times. Pump A is among them, as it would be on any round of the bay, which
        // is why its lift is a ratio and not a certainty. Pump B is left out because it is opened constantly, below.
        var everything = DemoDevices.All.Select(device => device.MacAddress).Where(mac => mac != PumpB).ToList();

        for (var index = 0; index < options.RandomOpens; index++)
        {
            history.Add(View(everything[random.Next(everything.Count)], RandomTime(now, options, random)));
        }

        for (var index = 0; index < options.PumpARounds; index++)
        {
            history.Add(View(PumpA, RandomTime(now, options, random)));
        }

        for (var index = 0; index < options.PumpAScheduledRuns; index++)
        {
            history.Add(Command(PumpA, CommandAction.PumpOn, RandomTime(now, options, random)));
        }

        // The device that is opened all the time: every few minutes for a day and a half. After any trigger it follows with
        // a confidence near one, and a confidence near one is exactly what lift is there to distrust.
        for (var at = now - TimeSpan.FromHours(36); at < now - TimeSpan.FromMinutes(1); at += options.ConstantOpenEvery)
        {
            history.Add(View(PumpB, at));
        }

        return history;
    }

    /// <summary>Where the index'th of a number of occurrences falls in the history: evenly spread, then nudged.</summary>
    private static DateTimeOffset SpreadOver(DateTimeOffset now, SeededHistoryOptions options, int index, int count, Random random)
    {
        // From just before the history's start to a few hours before now, oldest first.
        var span = TimeSpan.FromDays(options.Days) - TimeSpan.FromHours(4);
        var slot = count <= 1 ? 1d : (double)index / (count - 1);
        var jitter = TimeSpan.FromMinutes(random.Next(-120, 120));
        var at = now - TimeSpan.FromDays(options.Days) + TimeSpan.FromHours(2) + span * slot + jitter;

        return at < now - TimeSpan.FromHours(3) ? at : now - TimeSpan.FromHours(3);
    }

    private static DateTimeOffset RandomTime(DateTimeOffset now, SeededHistoryOptions options, Random random) =>
        now - TimeSpan.FromMinutes(random.Next(10, (int)TimeSpan.FromDays(options.Days).TotalMinutes));

    private static HistoricEvent Trigger(string mac, string detail, DateTimeOffset at) =>
        new(new TriggerEvent(TriggerKind.ThresholdCrossing, mac, detail, at, EventSource.Seeded), null);

    private static HistoricEvent View(string mac, DateTimeOffset at) =>
        new(null, new OperatorAction(OperatorActionKind.Viewed, mac, string.Empty, at, EventSource.Seeded));

    private static HistoricEvent Command(string mac, CommandAction action, DateTimeOffset at) =>
        new(null, new OperatorAction(OperatorActionKind.Command, mac, action.ToString(), at, EventSource.Seeded));
}

/// <summary>
/// Replays the seeded history into the pattern engine once the devices it is about have registered.
/// </summary>
/// <remarks>
/// <b>It waits for the devices.</b> The history is about particular devices, and the engine learns at two levels: the
/// specific one needs only an address, but the general one ("a pump in Zone 6") needs to know what kind of device and which
/// zone, which is known only once the device has registered. The simulator registers over HTTP after the API is up, so the
/// seeder checks until the devices the habits are about are all known, then replays. If they never are (the fleet was cut
/// short, or the simulator is not running) it replays what it can after a time limit and says so, and an event about a device
/// that is not known is skipped, so nothing is learned about a device that does not exist.
/// <para>
/// It does not matter that readings may already be flowing by then. The engine pairs replayed events only with each other,
/// and fades them from when they happened (see <see cref="IPatternEngine.Replay"/>).
/// </para>
/// </remarks>
internal sealed class PatternSeeder(
    IPatternEngine patterns,
    IDeviceDirectory devices,
    IOptions<SeededHistoryOptions> options,
    TimeProvider time,
    ILogger<PatternSeeder> logger) : BackgroundService
{
    private readonly SeededHistoryOptions _options = options.Value;

    /// <summary>How many events were replayed. Zero until it has run.</summary>
    public int Replayed { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var deadline = time.GetUtcNow() + _options.WaitForDevices;

        using var timer = new PeriodicTimer(_options.PollInterval, time);

        try
        {
            while (!AllKnown() && time.GetUtcNow() < deadline)
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        Replayed = Seed();
    }

    /// <summary>Replays the history now, for the devices that are known. Public to the tests so they can seed without waiting.</summary>
    /// <returns>How many events were replayed.</returns>
    internal int Seed()
    {
        var history = SeededHistory.Build(time.GetUtcNow(), _options);
        var known = history.Where(item => devices.TryDescribe(item.Trigger?.MacAddress ?? item.Action!.Value.MacAddress, out _)).ToList();

        patterns.Replay(known);

        if (known.Count < history.Count)
        {
            logger.LogWarning(
                "Seeded {Replayed} of {Total} operator events. The rest are about devices that are not registered.",
                known.Count,
                history.Count);
        }
        else
        {
            logger.LogInformation("Seeded {Replayed} operator events covering {Days} days.", known.Count, _options.Days);
        }

        return known.Count;
    }

    private bool AllKnown() => SeededHistory.HabitDevices.All(mac => devices.TryDescribe(mac, out _));
}
