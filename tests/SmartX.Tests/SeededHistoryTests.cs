using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Patterns;
using SmartX.Api.Search;
using SmartX.Api.Storage;
using SmartX.Shared;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>The synthetic operator history replayed at start-up: what is in it, that it is labelled, and what it makes the engine say.</summary>
public class SeededHistoryTests
{
    private static readonly string MoistureB = DemoDevices.Hydroponics.MoistureSensorB;
    private static readonly string PumpA = DemoDevices.Hydroponics.PumpA;
    private static readonly string PumpB = DemoDevices.Hydroponics.PumpB;

    /// <summary>The whole demo fleet registered in a real device index, with the demo zone tree, as the API has it once the simulator has run.</summary>
    internal static DeviceDirectory FleetDirectory(IOptions<PatternOptions>? options = null)
    {
        var index = new DeviceIndex();

        foreach (var device in DemoDevices.All)
        {
            Assert.True(device.Registration.TryCreateRegistration(out var registration, out _));
            index.Register(registration);
        }

        return new DeviceDirectory(index, options ?? Options.Create(new PatternOptions()))
        {
            Catalogue = new ZoneCatalogue(DemoZoneHierarchy.Zones),
        };
    }

    private static IReadOnlyList<HistoricEvent> History(Action<SeededHistoryOptions>? configure = null)
    {
        var options = new SeededHistoryOptions();
        configure?.Invoke(options);

        return SeededHistory.Build(T0, options);
    }

    private static IEnumerable<TriggerEvent> TriggersOf(IReadOnlyList<HistoricEvent> history) =>
        history.Where(item => item.Trigger is not null).Select(item => item.Trigger!.Value);

    private static IEnumerable<OperatorAction> ActionsOf(IReadOnlyList<HistoricEvent> history) =>
        history.Where(item => item.Action is not null).Select(item => item.Action!.Value);

    // ---- what is in it ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_configuration_always_builds_the_same_history()
    {
        Assert.Equal(History(), History());
    }

    [Fact]
    public void A_different_random_seed_builds_a_different_history()
    {
        Assert.NotEqual(History(), History(options => options.RandomSeed = 99));
    }

    [Fact]
    public void Every_event_is_labelled_as_seeded()
    {
        var history = History();

        Assert.NotEmpty(history);
        Assert.All(TriggersOf(history), trigger => Assert.Equal(EventSource.Seeded, trigger.Source));
        Assert.All(ActionsOf(history), action => Assert.Equal(EventSource.Seeded, action.Source));
        Assert.All(ActionsOf(history), action => Assert.False(action.FromSuggestion));
    }

    [Fact]
    public void Everything_is_in_the_past_and_within_the_days_asked_for()
    {
        var history = History(options => options.Days = 3);

        Assert.All(history, item =>
        {
            Assert.True(item.At < T0, $"{item.At} is not before {T0}.");
            Assert.True(item.At >= T0 - TimeSpan.FromDays(3.1), $"{item.At} is too long ago.");
        });
    }

    [Fact]
    public void The_briefs_example_is_in_it_several_times()
    {
        var history = History();

        var drops = TriggersOf(history).Where(trigger => trigger.MacAddress == MoistureB && trigger.Detail == "Low").ToList();
        var opened = ActionsOf(history).Where(action => action.MacAddress == PumpA && action.Kind == OperatorActionKind.Viewed).ToList();
        var turnedOn = ActionsOf(history).Where(action => action.MacAddress == PumpA && action.Command == nameof(CommandAction.PumpOn)).ToList();

        Assert.Equal(8, drops.Count);

        // Opened and turned on after seven of the eight, and not after the other.
        Assert.Equal(7, drops.Count(drop => opened.Any(open => open.At > drop.At && open.At < drop.At + TimeSpan.FromMinutes(5))));
        Assert.Equal(7, drops.Count(drop => turnedOn.Any(on => on.At > drop.At && on.At < drop.At + TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void A_response_comes_after_its_trigger_and_inside_the_window()
    {
        var history = History();
        var window = new PatternOptions().Window;

        foreach (var drop in TriggersOf(history).Where(trigger => trigger.MacAddress == MoistureB))
        {
            var responses = ActionsOf(history).Where(action => action.MacAddress == PumpA && action.At > drop.At && action.At < drop.At + window);

            Assert.All(responses, action => Assert.InRange(action.At - drop.At, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(4)));
        }
    }

    [Fact]
    public void The_other_habits_are_in_it()
    {
        var history = History();

        Assert.Equal(6, TriggersOf(history).Count(trigger => trigger.MacAddress == DemoDevices.Mac(0x1A) && trigger.Detail == "High"));
        Assert.True(ActionsOf(history).Count(action => action.MacAddress == DemoDevices.Mac(0x2B)) >= 6);
        Assert.Equal(6, TriggersOf(history).Count(trigger => trigger.MacAddress == DemoDevices.Mac(0x0C) && trigger.Detail == "High"));
        Assert.True(ActionsOf(history).Count(action => action.MacAddress == DemoDevices.Mac(0x0A)) >= 6);
    }

    [Fact]
    public void One_device_is_opened_all_the_time_and_others_at_random()
    {
        var history = History();
        var constant = ActionsOf(history).Where(action => action.MacAddress == PumpB).OrderBy(action => action.At).ToList();

        // Every four minutes for a day and a half.
        Assert.InRange(constant.Count, 500, 560);
        Assert.All(constant.Zip(constant.Skip(1)), pair => Assert.Equal(TimeSpan.FromMinutes(4), pair.Second.At - pair.First.At));

        // And a spread of other devices, opened at no particular time.
        Assert.True(ActionsOf(history).Select(action => action.MacAddress).Distinct().Count() > 15);
    }

    [Fact]
    public void Trigger_and_action_counts_follow_the_options()
    {
        var history = History(options =>
        {
            options.BriefTriggers = 4;
            options.BriefResponses = 4;
            options.OtherHabitTriggers = 0;
            options.UnansweredTriggers = 0;
            options.RandomOpens = 0;
            options.PumpARounds = 0;
            options.ConstantOpenEvery = TimeSpan.FromDays(10);
        });

        Assert.Equal(4, TriggersOf(history).Count());
        Assert.Equal(4, ActionsOf(history).Count(action => action.MacAddress == PumpA && action.Kind == OperatorActionKind.Viewed));
    }

    // ---- what it makes the engine say -------------------------------------------------------------------------------

    private static (PatternEngine Engine, DeviceDirectory Directory) SeededEngine()
    {
        var directory = FleetDirectory();
        var engine = new PatternEngine(Options.Create(new PatternOptions()), directory);

        engine.Replay(History());

        return (engine, directory);
    }

    [Fact]
    public void A_freshly_seeded_engine_knows_what_follows_moisture_sensor_b_dropping_below_20_percent()
    {
        var (engine, _) = SeededEngine();

        var answer = engine.Predict(TriggerKind.ThresholdCrossing, "Low", MoistureB, T0);

        Assert.Equal(PatternLevel.Specific, answer.AnsweredBy);

        var open = Assert.Single(answer.Associations, found => found.Action.Target == PumpA && found.Action.Kind == OperatorActionKind.Viewed);
        var on = Assert.Single(answer.Associations, found => found.Action.Target == PumpA && found.Action.Command == nameof(CommandAction.PumpOn));

        Assert.True(open.Qualified);
        Assert.True(on.Qualified);
        Assert.Equal(7, open.Times);
        Assert.Equal(8, open.TriggerTimes);
    }

    [Fact]
    public void The_evidence_says_how_much_of_it_is_seeded()
    {
        var (engine, _) = SeededEngine();

        var open = Assert.Single(
            engine.Predict(TriggerKind.ThresholdCrossing, "Low", MoistureB, T0).Associations,
            found => found.Action.Target == PumpA && found.Action.Kind == OperatorActionKind.Viewed);

        Assert.Equal(7, open.SeededTimes);
        Assert.Equal(8, open.SeededTriggerTimes);
    }

    [Fact]
    public void The_device_opened_all_the_time_is_not_offered_however_often_it_follows()
    {
        var (engine, _) = SeededEngine();

        var everything = All(engine, T0, new PatternQuery(MacAddress: MoistureB, Level: PatternLevel.Specific));
        var constant = everything.SingleOrDefault(found => found.Action.Target == PumpB);

        // It may follow the trigger, and when it does it follows with a confidence of nearly one. It is not a pattern.
        Assert.True(constant is null || !constant.Qualified, $"Pump B qualified with lift {constant?.Lift}.");
    }

    [Fact]
    public void The_other_habits_qualify_too()
    {
        var (engine, _) = SeededEngine();

        var cold = engine.Predict(TriggerKind.ThresholdCrossing, "High", DemoDevices.Mac(0x1A), T0);
        var power = engine.Predict(TriggerKind.ThresholdCrossing, "High", DemoDevices.Mac(0x0C), T0);

        Assert.Contains(cold.Associations, found => found.Action.Target == DemoDevices.Mac(0x2B) && found.Qualified);
        Assert.Contains(power.Associations, found => found.Action.Target == DemoDevices.Mac(0x0A) && found.Qualified);
    }

    // ---- the seeder -------------------------------------------------------------------------------------------------

    private static PatternSeeder Seeder(IPatternEngine engine, IDeviceDirectory directory, bool enabled = true) =>
        new(
            engine,
            directory,
            Options.Create(new SeededHistoryOptions { Enabled = enabled }),
            new ManualTimeProvider(T0),
            NullLogger<PatternSeeder>.Instance);

    [Fact]
    public void The_seeder_replays_the_history_into_the_engine()
    {
        var engine = new RecordingEngine();
        var seeder = Seeder(engine, FleetDirectory());

        var replayed = seeder.Seed();

        Assert.True(replayed > 600);
        Assert.Equal(replayed, engine.Triggers.Count + engine.Actions.Count);
        Assert.All(engine.Triggers, trigger => Assert.Equal(EventSource.Seeded, trigger.Source));
    }

    [Fact]
    public void An_event_about_a_device_that_is_not_registered_is_skipped()
    {
        var engine = new RecordingEngine();
        var seeder = Seeder(engine, new FakeDirectory());

        Assert.Equal(0, seeder.Seed());
        Assert.Empty(engine.Triggers);
        Assert.Empty(engine.Actions);
    }

    [Fact]
    public async Task A_disabled_seeder_replays_nothing()
    {
        var engine = new RecordingEngine();
        var seeder = Seeder(engine, FleetDirectory(), enabled: false);

        await seeder.StartAsync(CancellationToken.None);
        await seeder.ExecuteTask!;
        await seeder.StopAsync(CancellationToken.None);

        Assert.Empty(engine.Triggers);
        Assert.Equal(0, seeder.Replayed);
    }

    [Fact]
    public async Task An_enabled_seeder_replays_once_the_devices_the_habits_are_about_are_registered()
    {
        var engine = new RecordingEngine();
        var seeder = Seeder(engine, FleetDirectory());

        await seeder.StartAsync(CancellationToken.None);
        await seeder.ExecuteTask!;
        await seeder.StopAsync(CancellationToken.None);

        Assert.True(seeder.Replayed > 600);
    }

    [Fact]
    public void The_options_are_validated()
    {
        Assert.Empty(SeededHistoryValidation.Problems(new SeededHistoryOptions()));
        Assert.NotEmpty(SeededHistoryValidation.Problems(new SeededHistoryOptions { Days = 0 }));
        Assert.NotEmpty(SeededHistoryValidation.Problems(new SeededHistoryOptions { BriefTriggers = 3, BriefResponses = 4 }));
        Assert.NotEmpty(SeededHistoryValidation.Problems(new SeededHistoryOptions { ConstantOpenEvery = TimeSpan.Zero }));
    }

    [Fact]
    public void Seeding_is_off_by_default()
    {
        Assert.False(new SeededHistoryOptions().Enabled);
    }
}
