using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartX.Api.History;
using SmartX.Api.Patterns;
using SmartX.Api.Trust;
using SmartX.Shared;
using Xunit.Abstractions;
using static SmartX.Tests.ApiTestHost;
using static SmartX.Tests.PatternKit;

namespace SmartX.Tests;

/// <summary>
/// What the suggestion engine costs, measured and written down, because the rubric names "performance drops" as the failure to avoid.
/// </summary>
/// <remarks>
/// Behind the Benchmark trait, so the default run leaves it out: it asserts thresholds on a wall clock, and a threshold can be missed
/// on a cold or busy machine for no fault of the code. The thresholds are an order of magnitude looser than what was measured.
/// <code>dotnet test tests/SmartX.Tests --settings tests/SmartX.Tests/benchmark.runsettings --filter "FullyQualifiedName~SuggestionBenchmarkTests" --logger "console;verbosity=detailed"</code>
/// </remarks>
public class SuggestionBenchmarkTests(ITestOutputHelper output)
{
    private static string[] Sensors => [.. DemoDevices.All.Where(device => device.Registration.Category != SensorCategory.Actuator).Select(device => device.MacAddress)];

    // ---- recomputing suggestions ------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Benchmark")]
    public void The_cost_of_recomputing_suggestions_with_the_full_fleet_and_the_hydroponics_bay()
    {
        var directory = SeededHistoryTests.FleetDirectory();
        var clock = new FakeTimeProvider(T0);
        var engine = new PatternEngine(Options.Create(new PatternOptions()), directory);
        var service = new SuggestionService(
            engine, directory, Options.Create(new SuggestionOptions()), Options.Create(new PatternOptions()), clock, NullLogger<SuggestionService>.Instance);

        engine.Replay(SeededHistory.Build(T0, new SeededHistoryOptions()));

        var sensors = Sensors;
        var moistureB = DemoDevices.Hydroponics.MoistureSensorB;
        var changes = 0;
        service.Changed += () => changes++;

        // A trigger that has a pattern: the engine records it, tells the service, and the service ranks, explains and announces.
        const int withPattern = 2_000;
        var watch = Stopwatch.StartNew();

        for (var index = 0; index < withPattern; index++)
        {
            engine.RecordTrigger(new TriggerEvent(TriggerKind.ThresholdCrossing, moistureB, "Low", T0.AddMilliseconds(index)));
        }

        var patternCost = watch.Elapsed.TotalMilliseconds / withPattern;

        // A trigger that does not: recorded, remembered, and nothing to rank.
        const int withoutPattern = 5_000;
        watch.Restart();

        for (var index = 0; index < withoutPattern; index++)
        {
            engine.RecordTrigger(new TriggerEvent(TriggerKind.ThresholdCrossing, sensors[index % sensors.Length], "High", T0.AddMilliseconds(index)));
        }

        var plainCost = watch.Elapsed.TotalMilliseconds / withoutPattern;

        // The sweep, with the live triggers those left behind (bounded at 500).
        const int sweeps = 2_000;
        watch.Restart();

        for (var index = 0; index < sweeps; index++)
        {
            service.Sweep();
        }

        var sweepCost = watch.Elapsed.TotalMilliseconds / sweeps;

        output.WriteLine($"Fleet: {DemoDevices.All.Count} devices ({sensors.Length} sensors), seeded history of {SeededHistory.Build(T0, new SeededHistoryOptions()).Count:N0} events; {service.Current.Suggestions.Count} suggestions held.");
        output.WriteLine($"A trigger with a pattern, recorded and recomputed:        {patternCost * 1000:N1} us each");
        output.WriteLine($"A trigger with no pattern, recorded:                       {plainCost * 1000:N1} us each");
        output.WriteLine($"A sweep with {500} live triggers (the bound):               {sweepCost * 1000:N1} us each");
        output.WriteLine($"Announcements: {changes}");

        Assert.True(patternCost < 5, $"A trigger with a pattern cost {patternCost:N2} ms.");
        Assert.True(plainCost < 1, $"A trigger without one cost {plainCost:N2} ms.");
        Assert.True(sweepCost < 10, $"A sweep cost {sweepCost:N2} ms.");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void The_cost_of_an_action_and_of_a_dismissal_with_suggestions_live()
    {
        var directory = SeededHistoryTests.FleetDirectory();
        var clock = new FakeTimeProvider(T0);
        var engine = new PatternEngine(Options.Create(new PatternOptions()), directory);
        var service = new SuggestionService(
            engine, directory, Options.Create(new SuggestionOptions()), Options.Create(new PatternOptions()), clock, NullLogger<SuggestionService>.Instance);

        engine.Replay(SeededHistory.Build(T0, new SeededHistoryOptions()));
        engine.RecordTrigger(new TriggerEvent(TriggerKind.ThresholdCrossing, DemoDevices.Hydroponics.MoistureSensorB, "Low", T0));

        const int actions = 2_000;
        var watch = Stopwatch.StartNew();

        for (var index = 0; index < actions; index++)
        {
            engine.RecordAction(new OperatorAction(OperatorActionKind.Viewed, DemoDevices.Mac(0x01), string.Empty, T0.AddMilliseconds(index)));
        }

        var actionCost = watch.Elapsed.TotalMilliseconds / actions;

        watch.Restart();

        for (var index = 0; index < 500; index++)
        {
            var id = service.Current.Suggestions[0].Id;

            service.Dismiss(id);
            service.Restore(id);
        }

        var feedbackCost = watch.Elapsed.TotalMilliseconds / 500;

        output.WriteLine($"An operator action, recorded and the suggestions re-ranked: {actionCost * 1000:N1} us each");
        output.WriteLine($"A dismissal and its restoration:                            {feedbackCost * 1000:N1} us per pair");

        Assert.True(actionCost < 5, $"An action cost {actionCost:N2} ms.");
        Assert.True(feedbackCost < 10, $"A dismissal and restoration cost {feedbackCost:N2} ms.");
    }

    // ---- the forecast -----------------------------------------------------------------------------------------------

    private sealed class ForecastRig
    {
        public required InsightService Insights { get; init; }

        public required int Entries { get; init; }

        public static ForecastRig Create(int nodes, TimeSpan history)
        {
            var clock = new FakeTimeProvider(T0);
            var trust = new TrustEngine(Options.Create(new TrustOptions()), clock, NullLogger<TrustEngine>.Instance);
            var mesh = MeshTestHost.Create(clock);
            var investigations = new InvestigationService(
                trust, mesh.Alerts, mesh.History, new RecordingEngine(), Directory(), clock, Options.Create(new InvestigationOptions()));

            var entries = 0;

            for (var node = 0; node < nodes; node++)
            {
                var mac = $"B8:27:EB:30:{node / 256:X2}:{node % 256:X2}";

                // A node that has been doing well for a while, tracked by the trust engine and so listed in its snapshot.
                for (var index = 0; index < 6; index++)
                {
                    trust.Record(mac, TrustOutcome.Success, T0 - TimeSpan.FromSeconds(30 - index * 5));
                }

                // Its readings every four seconds and its trust every ten, for as long as history goes back.
                for (var at = T0 - history; at <= T0; at += TimeSpan.FromSeconds(4))
                {
                    mesh.History.Append(new HistoryEntry
                    {
                        Sequence = mesh.Sequence.Next(),
                        Timestamp = at,
                        MacAddress = mac,
                        Kind = HistoryKind.Reading,
                        Value = 20,
                        Unit = "°C",
                    });
                    entries++;
                }

                for (var at = T0 - history; at <= T0; at += TimeSpan.FromSeconds(10))
                {
                    mesh.History.Append(HistoryEntry.FromTrustSample(mesh.Sequence.Next(), at, mac, 0.9 - (T0 - at).TotalMinutes * 0.01));
                    entries++;
                }
            }

            var insights = new InsightService(
                trust,
                mesh.History,
                mesh.Alerts,
                investigations,
                Directory(),
                Options.Create(new ForecastOptions()),
                Options.Create(new TrustOptions()),
                clock,
                NullLogger<InsightService>.Instance);

            return new ForecastRig { Insights = insights, Entries = entries };
        }
    }

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData(55, 60)]
    [InlineData(55, 1440)]
    [InlineData(1_000, 10)]
    [InlineData(4_800, 10)]
    public void The_cost_of_a_forecast_pass_over_every_node(int nodes, int historyMinutes)
    {
        var rig = ForecastRig.Create(nodes, TimeSpan.FromMinutes(historyMinutes));

        // One pass to warm up, then the measurement.
        rig.Insights.Pass();

        const int passes = 10;
        var watch = Stopwatch.StartNew();

        for (var index = 0; index < passes; index++)
        {
            rig.Insights.Pass();
        }

        var pass = watch.Elapsed.TotalMilliseconds / passes;

        output.WriteLine(
            $"{nodes:N0} nodes, {historyMinutes} minutes of history each ({rig.Entries:N0} entries held): a forecast pass costs {pass:N2} ms, {pass * 1000 / nodes:N1} us a node. At one pass every 5 s that is {pass / 50:N2}% of a core.");

        // The store walks to the start of a node's range, so this grows with how much each node holds, which is what the figures
        // above show. The bound below is for the demo fleet at a day's history.
        if (nodes <= 55)
        {
            Assert.True(pass < 250, $"A pass over {nodes} nodes cost {pass:N1} ms.");
        }
    }

    // ---- from the trigger to the client ------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task The_latency_from_a_reading_crossing_20_percent_to_the_suggestion_arriving_in_a_client()
    {
        await using var host = RealtimeHost.Create(new Dictionary<string, string?>
        {
            ["Zones:Hierarchy"] = "Demo",
            ["SeededHistory:Enabled"] = "true",
            ["SeededHistory:PollInterval"] = "00:00:00.020",
        });

        foreach (var device in DemoDevices.All)
        {
            using var response = await host.Http.PostAsJsonAsync("api/sensors", device.Registration);
            Assert.True(response.IsSuccessStatusCode);
        }

        var seeder = host.Services.GetRequiredService<PatternSeeder>();

        for (var waited = 0; seeder.Replayed == 0 && waited < 500; waited++)
        {
            await Task.Delay(20);
        }

        var client = await host.ConnectAsync(RealtimeTopics.Suggestions);
        var moisture = DemoDevices.Hydroponics.MoistureSensorB;

        List<double> postedToReceived = [];
        List<double> triggerToReceived = [];

        for (var round = 0; round < 40; round++)
        {
            var before = client.Messages<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated).Count;
            var watch = Stopwatch.StartNew();

            await host.Http.PostAsync("api/telemetry", Reading(moisture, "15"));

            var update = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated, before + 1))[before];
            var arrived = DateTimeOffset.UtcNow;

            postedToReceived.Add(watch.Elapsed.TotalMilliseconds);
            triggerToReceived.Add((arrived - update.Suggestions.Max(found => found.TriggeredAt)).TotalMilliseconds);

            // Back above the re-arm level, so the next dip is a new trigger.
            await host.Http.PostAsync("api/telemetry", Reading(moisture, "60"));
            await Task.Delay(30);
        }

        postedToReceived.Sort();
        triggerToReceived.Sort();

        double Percentile(List<double> sorted, double p) => sorted[(int)Math.Min(sorted.Count - 1, p * sorted.Count)];

        output.WriteLine($"40 dips below 20 %, a client subscribed to the suggestions topic, long polling over the in-memory test server:");
        output.WriteLine($"Reading posted to suggestion received:  median {Percentile(postedToReceived, 0.5):N1} ms, p95 {Percentile(postedToReceived, 0.95):N1} ms, worst {postedToReceived[^1]:N1} ms");
        output.WriteLine($"Trigger fired to suggestion received:   median {Percentile(triggerToReceived, 0.5):N1} ms, p95 {Percentile(triggerToReceived, 0.95):N1} ms, worst {triggerToReceived[^1]:N1} ms");

        Assert.True(Percentile(postedToReceived, 0.95) < 1_000, $"p95 was {Percentile(postedToReceived, 0.95):N0} ms.");
    }
}
