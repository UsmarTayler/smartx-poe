using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartX.Api.Patterns;
using SmartX.Shared;
using Xunit.Abstractions;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>
/// What the pattern engine costs per event under load, measured and written down, because the rubric names "performance
/// drops" as the failure to avoid.
/// </summary>
/// <remarks>
/// Behind the Benchmark trait, so the default run leaves it out: it asserts thresholds on a wall clock, and a threshold can be
/// missed on a cold or busy machine for no fault of the code. The thresholds here are an order of magnitude looser than what
/// was measured, so they catch a change that makes the engine genuinely slower without failing on noise.
/// <para>
/// <b>The load is the worst the engine allows.</b> The window is full to its bound of five thousand triggers, spread over a
/// fleet of five thousand sensors, and the first time an action is seen it is paired with every one of them at both levels.
/// That is the case the cost of an action is proportional to. A repeat of an action already counted against those triggers
/// is the other case: every pairing is already in the set, and the cost is the lookups alone.
/// </para>
/// Run it on purpose:
/// <code>dotnet test tests/SmartX.Tests --settings tests/SmartX.Tests/benchmark.runsettings --filter "FullyQualifiedName~PatternBenchmarkTests" --logger "console;verbosity=detailed"</code>
/// </remarks>
public class PatternBenchmarkTests(ITestOutputHelper output)
{
    private const int Sensors = 5_000;
    private const int Actuators = 100;
    private const int Zones = 50;

    private static string SensorMac(int index) => $"B8:27:EB:A0:{index / 256:X2}:{index % 256:X2}";

    private static string ActuatorMac(int index) => $"B8:27:EB:B0:00:{index:X2}";

    private static FakeDirectory Fleet()
    {
        var directory = new FakeDirectory();

        for (var index = 0; index < Sensors; index++)
        {
            var zone = $"zone-{index % Zones}";
            directory.Add(SensorMac(index), $"Sensor {index}", "Environmental/%RH", "%RH sensor", zone, zone, $"Zone {index % Zones}");
        }

        for (var index = 0; index < Actuators; index++)
        {
            var zone = $"zone-{index % Zones}";
            directory.Add(ActuatorMac(index), $"Pump {index}", "Actuator/Pump", "pump", zone, zone, $"Zone {index % Zones}", isState: true, maximum: 1);
        }

        return directory;
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void The_cost_of_an_action_with_a_full_window_across_a_large_fleet()
    {
        var engine = new PatternEngine(Options.Create(new PatternOptions()), Fleet());

        // A full window: one trigger from each sensor, one every 100 ms, which is about eight minutes of an incident.
        var clock = Stopwatch.StartNew();

        for (var index = 0; index < Sensors; index++)
        {
            engine.RecordTrigger(Low(SensorMac(index), T0 + TimeSpan.FromMilliseconds(100 * index)));
        }

        var triggerCost = clock.Elapsed.TotalMilliseconds / Sensors;
        var now = T0 + TimeSpan.FromMilliseconds(100 * Sensors);

        Assert.Equal(Sensors, engine.TriggersInWindow);

        // The first time each action is seen: paired with all five thousand triggers, at the specific and the general level.
        clock.Restart();

        for (var index = 0; index < Actuators; index++)
        {
            engine.RecordAction(View(ActuatorMac(index), now + TimeSpan.FromMilliseconds(index)));
        }

        var firstCost = clock.Elapsed.TotalMilliseconds / Actuators;

        // The same actions again: every pairing is already counted, so the cost is the walk and the set lookups.
        const int repeats = 2_000;
        clock.Restart();

        for (var index = 0; index < repeats; index++)
        {
            engine.RecordAction(View(ActuatorMac(index % Actuators), now + TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(index)));
        }

        var repeatCost = clock.Elapsed.TotalMilliseconds / repeats;

        // And answering: what tends to follow a trigger on one sensor, which looks at its own and its kind's pairings.
        const int queries = 500;
        clock.Restart();

        for (var index = 0; index < queries; index++)
        {
            engine.Predict(TriggerKind.ThresholdCrossing, "Low", SensorMac(index), now + TimeSpan.FromSeconds(2));
        }

        var queryCost = clock.Elapsed.TotalMilliseconds / queries;

        output.WriteLine($"Fleet: {Sensors:N0} sensors and {Actuators} actuators in {Zones} zones; window full at {engine.TriggersInWindow:N0} triggers.");
        output.WriteLine($"Recording a trigger:                          {triggerCost * 1000:N1} us each");
        output.WriteLine($"An action, first time seen (worst case):      {firstCost:N2} ms each, {1000 / firstCost:N0} per second");
        output.WriteLine($"An action already counted against the window: {repeatCost:N2} ms each, {1000 / repeatCost:N0} per second");
        output.WriteLine($"Predicting for a sensor:                      {queryCost * 1000:N1} us each");

        // Loose, on purpose: see the remarks on the class.
        Assert.True(triggerCost < 0.5, $"A trigger cost {triggerCost:N3} ms.");
        Assert.True(firstCost < 100, $"A first-time action cost {firstCost:N1} ms.");
        Assert.True(repeatCost < 25, $"A repeated action cost {repeatCost:N1} ms.");
        Assert.True(queryCost < 25, $"A prediction cost {queryCost:N1} ms.");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void The_cost_of_an_action_does_not_depend_on_how_much_has_been_learned()
    {
        // The same single action against an empty engine and against one that has learned a great deal. What an action
        // costs is the triggers in the window, never the history behind it.
        var fresh = new PatternEngine(Options.Create(new PatternOptions()), Fleet());
        var seasoned = new PatternEngine(Options.Create(new PatternOptions { MaxLearnedPairs = 1_000_000 }), Fleet());

        for (var day = 0; day < 20; day++)
        {
            var start = T0 - TimeSpan.FromDays(20 - day);

            for (var index = 0; index < 200; index++)
            {
                seasoned.RecordTrigger(Low(SensorMac(index), start + TimeSpan.FromMinutes(index)));
                seasoned.RecordAction(View(ActuatorMac(index % Actuators), start + TimeSpan.FromMinutes(index) + TimeSpan.FromSeconds(30)));
            }
        }

        double Measure(PatternEngine engine)
        {
            for (var index = 0; index < 500; index++)
            {
                engine.RecordTrigger(Low(SensorMac(index), T0 + TimeSpan.FromMilliseconds(index)));
            }

            var clock = Stopwatch.StartNew();

            for (var index = 0; index < Actuators; index++)
            {
                engine.RecordAction(View(ActuatorMac(index), T0 + TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(index)));
            }

            return clock.Elapsed.TotalMilliseconds / Actuators;
        }

        var freshCost = Measure(fresh);
        var seasonedCost = Measure(seasoned);

        output.WriteLine($"Per action, 500 triggers in the window: {freshCost:N3} ms on a fresh engine, {seasonedCost:N3} ms after twenty days of learning.");

        // Within a small multiple, allowing for the larger dictionaries' cache behaviour and for noise at these sizes.
        Assert.True(seasonedCost < Math.Max(freshCost * 5, 2), $"{seasonedCost:N3} ms against {freshCost:N3} ms.");
    }
}
