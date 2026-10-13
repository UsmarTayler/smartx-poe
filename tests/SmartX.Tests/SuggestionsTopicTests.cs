using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Api.Patterns;
using SmartX.Shared;
using Xunit.Abstractions;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// The suggestions topic end to end: the demo fleet registered over HTTP, a seeded history, a reading posted over HTTP,
/// and a hub client that receives what the operator would see.
/// </summary>
public class SuggestionsTopicTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string MoistureB = DemoDevices.Hydroponics.MoistureSensorB;
    private static readonly string PumpA = DemoDevices.Hydroponics.PumpA;

    private RealtimeHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = RealtimeHost.Create(new Dictionary<string, string?>
        {
            ["Zones:Hierarchy"] = "Demo",
            ["SeededHistory:Enabled"] = "true",
            ["SeededHistory:PollInterval"] = "00:00:00.020",
        });

        // The fleet registers over HTTP, as the simulator does. The seeder is waiting for exactly these devices.
        foreach (var device in DemoDevices.All)
        {
            using var response = await _host.Http.PostAsJsonAsync("api/sensors", device.Registration);

            Assert.True(response.IsSuccessStatusCode, $"{device.Registration.Room}: {(int)response.StatusCode}");
        }

        var seeder = _host.Services.GetRequiredService<PatternSeeder>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (seeder.Replayed == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(seeder.Replayed > 0, "The history was never seeded.");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<HttpResponseMessage> Drop(string mac, double value) =>
        _host.Http.PostAsync("api/telemetry", Reading(mac, value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private Task<SuggestionsUpdate> SnapshotAsync() => _host.Http.GetFromJsonAsync<SuggestionsUpdate>("api/realtime/suggestions")!;

    private static Suggestion Find(SuggestionsUpdate update, string what) => Assert.Single(update.Suggestions, found => found.What == what);

    [Fact]
    public async Task A_fresh_start_has_nothing_to_suggest_until_something_happens()
    {
        var snapshot = await SnapshotAsync();

        Assert.Empty(snapshot.Suggestions);
        Assert.Equal(0, snapshot.Sequence);
    }

    [Fact]
    public async Task The_brief_example_produces_a_suggestion_as_soon_as_moisture_sensor_b_drops_below_20_percent()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);

        Assert.True((await Drop(MoistureB, 15)).IsSuccessStatusCode);

        var update = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated))[0];
        var turnOn = Find(update, "Turn on Pump A");

        Assert.Equal("Moisture Sensor B dropped below 20 %", turnOn.Why);
        Assert.Equal(PumpA, turnOn.Action.MacAddress);
        Assert.Equal(CommandAction.PumpOn, turnOn.Action.Command);
        Assert.True(turnOn.Evidence.SeededTimes > 0);
        Assert.Contains("from seeded history", turnOn.Evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("more likely than usual", turnOn.Evidence.Summary, StringComparison.Ordinal);
        Assert.Contains(update.Suggestions, found => found.What == "Open Pump A");
    }

    [Fact]
    public async Task A_suggestion_reaches_the_client_within_a_moment_of_the_trigger()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);
        var clock = Stopwatch.StartNew();

        await Drop(MoistureB, 15);

        var update = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated))[0];
        var arrived = clock.Elapsed;
        var sinceTrigger = DateTimeOffset.UtcNow - Find(update, "Turn on Pump A").TriggeredAt;

        output.WriteLine($"Posted to received: {arrived.TotalMilliseconds:N0} ms. Trigger to received: {sinceTrigger.TotalMilliseconds:N0} ms.");

        // Generous: the bound that matters is measured in the benchmark. This one only catches a message that waited for a timer.
        Assert.True(arrived < TimeSpan.FromSeconds(3), $"It took {arrived.TotalMilliseconds:N0} ms.");
    }

    [Fact]
    public async Task A_second_dip_in_the_same_episode_is_not_a_new_trigger()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);

        await Drop(MoistureB, 15);
        await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated);

        // Still below the level, and below the re-arm level: the hysteresis holds it as one dip.
        await Drop(MoistureB, 12);
        await Drop(MoistureB, 18);
        await RealtimeHost.TestClient.SettleAsync();

        Assert.Single(client.Messages<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated));
    }

    // ---- snapshot and stream ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_snapshot_is_as_of_the_sequence_of_the_last_message_pushed()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);

        await Drop(MoistureB, 15);
        var pushed = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated))[^1];

        var snapshot = await SnapshotAsync();

        Assert.Equal(pushed.Sequence, snapshot.Sequence);
        Assert.Equal(pushed.Suggestions.Select(found => found.Id), snapshot.Suggestions.Select(found => found.Id));
    }

    [Fact]
    public async Task Pushed_sequences_strictly_increase_and_each_message_is_the_whole_picture()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);

        await Drop(MoistureB, 15);
        var first = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated))[0];

        using var response = await _host.Http.PostAsync($"api/suggestions/{Find(first, "Open Pump A").Id}/dismiss", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var messages = await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated, count: 2);

        Assert.True(messages[1].Sequence > messages[0].Sequence);

        // Whole, not a difference: the second still has everything that was not dismissed.
        Assert.Contains(messages[1].Suggestions, found => found.What == "Turn on Pump A");
        Assert.DoesNotContain(messages[1].Suggestions, found => found.What == "Open Pump A");
    }

    [Fact]
    public async Task A_client_that_did_not_join_receives_nothing()
    {
        var listening = await _host.ConnectAsync(RealtimeTopics.Suggestions);
        var elsewhere = await _host.ConnectAsync(RealtimeTopics.Alerts);

        await Drop(MoistureB, 15);
        await listening.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated);
        await RealtimeHost.TestClient.SettleAsync();

        Assert.Empty(elsewhere.Messages<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated));
    }

    [Fact]
    public async Task Nothing_is_sent_for_ordinary_readings()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);

        for (var index = 0; index < 20; index++)
        {
            await Drop(MoistureB, 40 + index);
        }

        await RealtimeHost.TestClient.SettleAsync();

        Assert.Empty(client.Messages<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated));
    }

    // ---- doing what is suggested ------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_the_device_a_suggestion_pointed_at_removes_it_without_a_reload_and_teaches_the_engine_nothing()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);
        await Drop(MoistureB, 15);
        var first = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated))[0];
        var open = Find(first, "Open Pump A");
        var timesBefore = await TimesAsync();

        using var response = await _host.Http.PostAsJsonAsync("api/activity/selection", new SelectionRequest(PumpA, open.Id));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated, count: 2))[^1];

        Assert.DoesNotContain(after.Suggestions, found => found.What == "Open Pump A");
        Assert.Contains(after.Suggestions, found => found.What == "Turn on Pump A");

        // Accepting the advice did not make the pattern more confident.
        Assert.Equal(timesBefore, await TimesAsync());
    }

    [Fact]
    public async Task Opening_the_same_device_by_hand_removes_the_suggestion_and_does_count_as_a_habit()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);
        await Drop(MoistureB, 15);
        await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated);
        var timesBefore = await TimesAsync();

        using var response = await _host.Http.PostAsJsonAsync("api/activity/selection", new SelectionRequest(PumpA));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated, count: 2))[^1];

        Assert.DoesNotContain(after.Suggestions, found => found.What == "Open Pump A");
        Assert.Equal(timesBefore + 1, await TimesAsync());
    }

    [Fact]
    public async Task Accepting_a_command_suggestion_goes_down_the_normal_command_path_and_removes_the_suggestion()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);
        await Drop(MoistureB, 15);
        var first = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated))[0];
        var turnOn = Find(first, "Turn on Pump A");

        using var response = await _host.Http.PostAsJsonAsync(
            "api/commands", new IssueCommandRequest(turnOn.Action.MacAddress, turnOn.Action.Command!.Value, null, turnOn.Id));

        // Accepted, like any command: the device has not received it yet.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var command = (await response.Content.ReadFromJsonAsync<Command>())!;

        Assert.Equal(CommandState.Pending, command.State);
        Assert.Equal(PumpA, command.MacAddress);

        var after = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated, count: 2))[^1];

        Assert.DoesNotContain(after.Suggestions, found => found.What == "Turn on Pump A");
        Assert.Contains(after.Suggestions, found => found.What == "Open Pump A");
    }

    [Fact]
    public async Task A_refused_command_is_refused_whether_or_not_it_came_from_a_suggestion()
    {
        using var response = await _host.Http.PostAsJsonAsync(
            "api/commands", new IssueCommandRequest(PumpA, CommandAction.ValveOpen, null, "anything"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- "Not useful" -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Marking_not_useful_removes_the_suggestion_and_taking_it_back_returns_it()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);
        await Drop(MoistureB, 15);
        var first = (await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated))[0];
        var open = Find(first, "Open Pump A");

        using var dismissed = await _host.Http.PostAsync($"api/suggestions/{open.Id}/dismiss", null);
        var afterDismiss = (await dismissed.Content.ReadFromJsonAsync<SuggestionsUpdate>())!;

        Assert.DoesNotContain(afterDismiss.Suggestions, found => found.Id == open.Id);

        var record = Assert.Single(afterDismiss.Dismissed);

        Assert.Equal(open.Id, record.Suggestion.Id);
        Assert.Equal(0.85, record.WeightAfter, precision: 6);

        using var restored = await _host.Http.DeleteAsync($"api/suggestions/{open.Id}/dismiss");
        var afterRestore = (await restored.Content.ReadFromJsonAsync<SuggestionsUpdate>())!;

        Assert.Contains(afterRestore.Suggestions, found => found.Id == open.Id);
        Assert.Empty(afterRestore.Dismissed);
    }

    [Fact]
    public async Task Dismissing_or_restoring_what_is_not_held_is_a_404()
    {
        using var dismissed = await _host.Http.PostAsync("api/suggestions/nothing/dismiss", null);
        using var restored = await _host.Http.DeleteAsync("api/suggestions/nothing/dismiss");

        Assert.Equal(HttpStatusCode.NotFound, dismissed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, restored.StatusCode);
    }

    [Fact]
    public async Task The_http_endpoint_serves_the_same_state_as_the_snapshot()
    {
        var client = await _host.ConnectAsync(RealtimeTopics.Suggestions);
        await Drop(MoistureB, 15);
        await client.WaitForAsync<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated);

        var viaSuggestions = (await _host.Http.GetFromJsonAsync<SuggestionsUpdate>("api/suggestions"))!;
        var viaRealtime = await SnapshotAsync();

        Assert.Equal(viaRealtime.Suggestions.Select(found => found.Id), viaSuggestions.Suggestions.Select(found => found.Id));
        Assert.Equal(viaRealtime.Sequence, viaSuggestions.Sequence);
    }

    [Fact]
    public async Task The_insights_endpoint_answers()
    {
        using var response = await _host.Http.GetAsync("api/insights");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<List<Insight>>());
    }

    /// <summary>How many times Pump A was opened after Moisture Sensor B dropped, as the engine counts it.</summary>
    private async Task<int> TimesAsync()
    {
        var response = (await _host.Http.GetFromJsonAsync<PatternsResponse>($"api/patterns?mac={MoistureB}&trigger=ThresholdCrossing&level=Specific&includeWeak=true"))!;

        return response.Associations.Single(found => found.Trigger.Detail == "Low" && found.Action.Target == PumpA && found.Action.Kind == OperatorActionKind.Viewed).Times;
    }
}
