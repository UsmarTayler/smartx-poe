using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.Ingestion;
using SmartX.Api.Realtime;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;
using static SmartX.Tests.RealtimeHost;

namespace SmartX.Tests;

/// <summary>
/// The hub and the broadcaster: who receives what, and how it is batched, coalesced or sent at once.
/// </summary>
public class RealtimeHubTests
{
    private const string NodeA = "AA:BB:CC:00:00:01";
    private const string NodeB = "AA:BB:CC:00:00:02";

    // ---- groups ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_client_receives_only_the_topics_it_joined()
    {
        await using var host = RealtimeHost.Create();
        var alertWatcher = await host.ConnectAsync(RealtimeTopics.Alerts);
        var trustWatcher = await host.ConnectAsync(RealtimeTopics.Trust);
        var bystander = await host.ConnectAsync();

        host.Alerts.Raise(new AlertKey(NodeA, AlertKind.Disconnected), "silent");
        host.Trust.Track(NodeA);
        host.Trust.Record(NodeA, TrustOutcome.Success);
        await host.Broadcaster.FlushAsync();

        await alertWatcher.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated);
        await trustWatcher.WaitForAsync<TrustUpdate>(RealtimeMethods.TrustUpdated);
        await TestClient.SettleAsync();

        // Each heard its own topic and nothing else. The server never sent the others to it, so nothing
        // had to be filtered on the client.
        Assert.Empty(alertWatcher.Messages<TrustUpdate>(RealtimeMethods.TrustUpdated));
        Assert.Empty(trustWatcher.Messages<AlertUpdate>(RealtimeMethods.AlertsUpdated));
        Assert.Equal(0, bystander.Count);
    }

    [Fact]
    public async Task A_client_that_leaves_a_topic_stops_receiving_it()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts);

        host.Alerts.Raise(new AlertKey(NodeA, AlertKind.Disconnected));
        await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated);

        await client.Connection.InvokeAsync(RealtimeMethods.Unsubscribe, RealtimeTopics.Alerts);
        host.Alerts.Raise(new AlertKey(NodeB, AlertKind.Disconnected));
        await TestClient.SettleAsync();

        Assert.Single(client.Messages<AlertUpdate>(RealtimeMethods.AlertsUpdated));
    }

    [Fact]
    public async Task A_device_topic_carries_only_that_devices_readings()
    {
        await using var host = RealtimeHost.Create();
        await host.RegisterSensorAsync(NodeA);
        await host.RegisterSensorAsync(NodeB);

        var watcherA = await host.ConnectAsync(RealtimeTopics.Device(NodeA));
        var watcherB = await host.ConnectAsync(RealtimeTopics.Device(NodeB));

        host.Publish(NodeA, 1);
        host.Publish(NodeA, 2);
        host.Publish(NodeB, 3);
        await host.Broadcaster.FlushAsync();

        var a = await watcherA.WaitForAsync<ReadingsBatch>(RealtimeMethods.ReadingsArrived);
        var b = await watcherB.WaitForAsync<ReadingsBatch>(RealtimeMethods.ReadingsArrived);
        await TestClient.SettleAsync();

        Assert.Equal([1d, 2d], Assert.Single(a).Readings.Select(reading => reading.Value));
        Assert.Equal([3d], Assert.Single(b).Readings.Select(reading => reading.Value));
        Assert.All(Assert.Single(a).Readings, reading => Assert.Equal(NodeA, reading.MacAddress));
    }

    [Fact]
    public async Task A_device_topic_may_be_written_in_any_accepted_mac_format()
    {
        await using var host = RealtimeHost.Create();
        await host.RegisterSensorAsync(NodeA);
        var client = await host.ConnectAsync("device:aa-bb-cc-00-00-01");

        host.Publish(NodeA, 5);
        await host.Broadcaster.FlushAsync();

        Assert.Single(await client.WaitForAsync<ReadingsBatch>(RealtimeMethods.ReadingsArrived));
    }

    [Fact]
    public async Task A_topic_that_does_not_exist_is_refused_with_a_reason()
    {
        // One hub and one connection for the four topics: a refused subscription is an error to the caller, not a dropped connection.
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync();

        foreach (var topic in new[] { "nonsense", "device:not-a-mac", "device:AA:BB:CC:DD:EE:99", "" })
        {
            var error = await Assert.ThrowsAsync<HubException>(() => client.Connection.InvokeAsync(RealtimeMethods.Subscribe, topic));

            Assert.False(string.IsNullOrWhiteSpace(error.Message), $"topic '{topic}': the refusal gave no reason.");
        }
    }

    [Fact]
    public async Task Joining_a_topic_twice_still_delivers_each_message_once()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts);
        await client.Connection.InvokeAsync(RealtimeMethods.Subscribe, RealtimeTopics.Alerts);

        host.Alerts.Raise(new AlertKey(NodeA, AlertKind.Fault));
        await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated);
        await TestClient.SettleAsync();

        Assert.Single(client.Messages<AlertUpdate>(RealtimeMethods.AlertsUpdated));
    }

    [Fact]
    public async Task A_client_that_disconnects_is_forgotten_by_the_subscription_counts()
    {
        await using var host = RealtimeHost.Create();
        var subscriptions = host.Services.GetRequiredService<RealtimeSubscriptions>();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts, RealtimeTopics.Trust);
        Assert.True(subscriptions.Has(RealtimeTopics.Alerts));

        await client.Connection.StopAsync();

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (subscriptions.Has(RealtimeTopics.Alerts) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.False(subscriptions.Has(RealtimeTopics.Alerts));
        Assert.False(subscriptions.Has(RealtimeTopics.Trust));
    }

    // ---- batching ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Many_readings_within_one_window_arrive_as_one_message()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Stream);

        for (var i = 0; i < 500; i++)
        {
            host.Publish(i % 2 == 0 ? NodeA : NodeB, i);
        }

        await host.Broadcaster.FlushAsync();

        var batch = Assert.Single(await client.WaitForAsync<ReadingsBatch>(RealtimeMethods.ReadingsArrived));
        await TestClient.SettleAsync();

        // Five hundred readings, one message, in the order they were processed.
        Assert.Equal(500, batch.Readings.Count);
        Assert.Equal(Enumerable.Range(0, 500).Select(i => (double)i), batch.Readings.Select(reading => reading.Value));
        Assert.Equal(0, batch.Dropped);
        Assert.Single(client.Messages<ReadingsBatch>(RealtimeMethods.ReadingsArrived));
    }

    [Fact]
    public async Task A_flush_with_nothing_new_sends_nothing()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Stream, RealtimeTopics.Trust, RealtimeTopics.Alerts);

        await host.Broadcaster.FlushAsync();
        await host.Broadcaster.FlushAsync();
        await TestClient.SettleAsync();

        Assert.Equal(0, client.Count);
    }

    [Fact]
    public async Task Readings_in_separate_windows_are_separate_messages_with_increasing_sequence_numbers()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Stream);

        host.Publish(NodeA, 1);
        await host.Broadcaster.FlushAsync();
        host.Publish(NodeA, 2);
        await host.Broadcaster.FlushAsync();
        host.Publish(NodeA, 3);
        await host.Broadcaster.FlushAsync();

        var batches = await client.WaitForAsync<ReadingsBatch>(RealtimeMethods.ReadingsArrived, 3);

        Assert.Equal([1L, 2L, 3L], batches.Select(batch => batch.Sequence));
    }

    [Fact]
    public async Task More_readings_than_the_buffer_holds_are_dropped_and_the_batch_says_how_many()
    {
        await using var host = RealtimeHost.Create(new() { ["Realtime:MaxBufferedReadings"] = "10" });
        var client = await host.ConnectAsync(RealtimeTopics.Stream);

        for (var i = 0; i < 100; i++)
        {
            host.Publish(NodeA, i);
        }

        await host.Broadcaster.FlushAsync();

        var batch = Assert.Single(await client.WaitForAsync<ReadingsBatch>(RealtimeMethods.ReadingsArrived));

        // Bounded memory, and honest about it.
        Assert.Equal(10, batch.Readings.Count);
        Assert.Equal(90, batch.Dropped);
    }

    [Fact]
    public async Task Readings_are_not_collected_when_nobody_is_listening()
    {
        await using var host = RealtimeHost.Create();

        for (var i = 0; i < 100; i++)
        {
            host.Publish(NodeA, i);
        }

        // Someone joins after the fact. Nothing was held for them: the buffer is for listeners, so an idle
        // gateway cannot accumulate readings nobody asked for.
        var late = await host.ConnectAsync(RealtimeTopics.Stream);
        await host.Broadcaster.FlushAsync();
        await TestClient.SettleAsync();

        Assert.Empty(late.Messages<ReadingsBatch>(RealtimeMethods.ReadingsArrived));
    }

    // ---- coalescing ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Many_band_changes_within_one_window_arrive_as_the_latest_state_once()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Trust);

        host.Trust.Track(NodeA);

        // Good, bad, good, bad: the node changes band over and over inside one window.
        for (var i = 0; i < 10; i++)
        {
            host.Trust.Record(NodeA, i % 2 == 0 ? TrustOutcome.Success : TrustOutcome.RangeViolation);
        }

        for (var i = 0; i < 10; i++)
        {
            host.Trust.Record(NodeA, TrustOutcome.RangeViolation);
        }

        await host.Broadcaster.FlushAsync();

        var update = Assert.Single(await client.WaitForAsync<TrustUpdate>(RealtimeMethods.TrustUpdated));
        await TestClient.SettleAsync();

        // One message, the node once, and it is where the node is now, not where it was along the way.
        var node = Assert.Single(update.Nodes);
        Assert.True(host.Trust.TryGet(NodeA, out var now));
        Assert.Equal(now.Band, node.Band);
        Assert.Equal(now.Score, node.Score, precision: 9);
        Assert.Single(client.Messages<TrustUpdate>(RealtimeMethods.TrustUpdated));
    }

    [Fact]
    public async Task Each_changed_node_appears_once_in_a_flush_however_often_it_changed()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Trust);

        foreach (var mac in new[] { NodeA, NodeB })
        {
            host.Trust.Track(mac);

            for (var i = 0; i < 5; i++)
            {
                host.Trust.Record(mac, TrustOutcome.Success);
            }
        }

        await host.Broadcaster.FlushAsync();

        var update = Assert.Single(await client.WaitForAsync<TrustUpdate>(RealtimeMethods.TrustUpdated));

        Assert.Equal([NodeA, NodeB], update.Nodes.Select(node => node.MacAddress).Order());
        Assert.NotNull(update.Summary);
    }

    [Fact]
    public async Task A_score_that_only_drifted_is_not_sent_again()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Trust);

        host.Trust.Track(NodeA);
        host.Trust.Record(NodeA, TrustOutcome.Success);
        await host.Broadcaster.FlushAsync();
        await client.WaitForAsync<TrustUpdate>(RealtimeMethods.TrustUpdated);

        // The heartbeat monitor re-evaluates every node every few seconds. Nothing about this node changed.
        host.Trust.Reevaluate();
        await host.Broadcaster.FlushAsync();
        await TestClient.SettleAsync();

        Assert.Single(client.Messages<TrustUpdate>(RealtimeMethods.TrustUpdated));
    }

    [Fact]
    public async Task A_newly_tracked_node_is_announced_before_it_has_a_score()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Trust);

        host.Trust.Track(NodeA);
        await host.Broadcaster.FlushAsync();

        var update = Assert.Single(await client.WaitForAsync<TrustUpdate>(RealtimeMethods.TrustUpdated));

        Assert.Equal(TrustBand.Unproven, Assert.Single(update.Nodes).Band);
    }

    [Fact]
    public async Task A_late_joiner_is_not_told_about_changes_that_happened_before_it_joined()
    {
        await using var host = RealtimeHost.Create();

        host.Trust.Track(NodeA);
        host.Trust.Record(NodeA, TrustOutcome.Success);

        var late = await host.ConnectAsync(RealtimeTopics.Trust);
        await host.Broadcaster.FlushAsync();
        await TestClient.SettleAsync();

        // It will get that state from the snapshot. Nobody was listening, so nothing was held.
        Assert.Empty(late.Messages<TrustUpdate>(RealtimeMethods.TrustUpdated));
    }

    // ---- immediate ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_critical_reading_is_announced_at_once_and_does_not_wait_for_a_flush()
    {
        await using var host = RealtimeHost.Create();
        var alerts = await host.ConnectAsync(RealtimeTopics.Alerts);
        var stream = await host.ConnectAsync(RealtimeTopics.Stream);

        // An ordinary reading and a critical one, back to back. The flush timer is an hour long and is never
        // called: whatever arrives, arrives without it.
        host.Publish(NodeA, 10);
        host.Publish(NodeB, 99, new CriticalAlert(1, "power spike"));

        var priority = Assert.Single(await alerts.WaitForAsync<PriorityAlert>(RealtimeMethods.PriorityAlerted));
        await TestClient.SettleAsync();

        Assert.Equal(NodeB, priority.MacAddress);
        Assert.Equal("power spike", priority.Reason);
        Assert.Equal(99, priority.Value);

        // The ordinary reading is still waiting for its batch, which is exactly the difference.
        Assert.Empty(stream.Messages<ReadingsBatch>(RealtimeMethods.ReadingsArrived));
    }

    [Fact]
    public async Task A_critical_reading_reaches_the_client_ahead_of_readings_still_waiting_in_a_batch()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Stream, RealtimeTopics.Alerts);

        for (var i = 0; i < 200; i++)
        {
            host.Publish(NodeA, i);
        }

        host.Publish(NodeB, 99, new CriticalAlert(1, "power spike"));

        // Arrived before any flush: the batch has not been sent, the alert has.
        await client.WaitForAsync<PriorityAlert>(RealtimeMethods.PriorityAlerted);
        Assert.Empty(client.Messages<ReadingsBatch>(RealtimeMethods.ReadingsArrived));

        await host.Broadcaster.FlushAsync();
        var batch = Assert.Single(await client.WaitForAsync<ReadingsBatch>(RealtimeMethods.ReadingsArrived));

        // And the critical reading is in the batch as well, so the stream is complete.
        Assert.Equal(201, batch.Readings.Count);
    }

    [Fact]
    public async Task A_raised_alert_is_announced_at_once_with_its_state()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts);

        host.Alerts.Raise(new AlertKey(NodeA, AlertKind.OutOfRange), "42 is above 30");

        var update = Assert.Single(await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated));

        var alert = Assert.Single(update.Upserted);
        Assert.Equal(new AlertKey(NodeA, AlertKind.OutOfRange), alert.Key);
        Assert.Equal("42 is above 30", alert.Detail);
        Assert.Empty(update.Cleared);
        Assert.Equal(1, update.Stats.Active);
    }

    [Fact]
    public async Task A_cleared_alert_and_an_acknowledgement_are_announced_at_once()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts);
        var key = new AlertKey(NodeA, AlertKind.Disconnected);

        // One step at a time. A message carries the alert's state when it is sent and not when the event happened,
        // so three changes made faster than the sender can act on them are legitimately sent as what they add up
        // to. Waiting between them is how each is seen separately.
        host.Alerts.Raise(key);
        await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated, 1);

        host.Alerts.Acknowledge(key);
        await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated, 2);

        host.Alerts.Clear(key);
        var updates = await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated, 3);

        Assert.False(Assert.Single(updates[0].Upserted).Acknowledged);
        Assert.True(Assert.Single(updates[1].Upserted).Acknowledged);
        Assert.Equal(key, Assert.Single(updates[2].Cleared));
    }

    [Fact]
    public async Task An_alert_raised_and_cleared_in_quick_succession_always_ends_with_the_clear_on_the_wire()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts);
        var key = new AlertKey(NodeA, AlertKind.Disconnected);

        host.Alerts.Raise(key);
        host.Alerts.Clear(key);

        // Depending on how fast the sender is, the client sees the raise and then the clear, or only the clear:
        // a message carries the alert's state when it is sent, and by then the raise may be history. Either way
        // the last thing it is told is that the alert is gone, and it is never told the alert is active after
        // the clear. That is the property that matters, and it holds for both.
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (!client.Messages<AlertUpdate>(RealtimeMethods.AlertsUpdated).Any(update => update.Cleared.Contains(key)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await TestClient.SettleAsync();

        var updates = client.Messages<AlertUpdate>(RealtimeMethods.AlertsUpdated);

        Assert.Equal(key, Assert.Single(updates[^1].Cleared));
        Assert.Empty(updates[^1].Upserted);
    }

    [Fact]
    public async Task A_repeat_of_an_active_alert_is_coalesced_into_the_next_flush_as_its_latest_count()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts);
        var key = new AlertKey(NodeA, AlertKind.OutOfRange);

        host.Alerts.Raise(key);
        await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated);

        for (var i = 0; i < 50; i++)
        {
            host.Alerts.Raise(key);
        }

        await TestClient.SettleAsync();

        // Fifty repeats, no messages yet: a counter ticking is not news.
        Assert.Single(client.Messages<AlertUpdate>(RealtimeMethods.AlertsUpdated));

        await host.Broadcaster.FlushAsync();

        var updates = await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated, 2);

        // Then one, carrying the count as it is now.
        Assert.Equal(50, Assert.Single(updates[1].Upserted).SuppressedCount);
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public async Task A_command_state_change_is_announced_at_once()
    {
        await using var host = RealtimeHost.Create(new() { ["Zones:Hierarchy"] = "Demo" });
        var client = await host.ConnectAsync(RealtimeTopics.Commands);

        var valve = new SensorRegistration(
            "B8:27:EB:10:00:19", new DeploymentLocation("Freezer Valve", "sub-zone-f", "GW-A5"), SensorCategory.Actuator,
            "state", new ValueRange(0, 1), TelemetryPayloadKind.State, null, ActuatorKind.Valve);

        var issue = host.Commands.Issue(valve, CommandAction.ValveClose, null);
        host.Commands.Collect(valve.MacAddress);

        var updates = await client.WaitForAsync<CommandUpdate>(RealtimeMethods.CommandChanged, 2);

        Assert.Equal([CommandEvent.Issued, CommandEvent.Delivered], updates.Select(update => update.Event));
        Assert.Equal(issue.Command!.Id, updates[0].Command.Id);
        Assert.Equal(CommandState.Delivered, updates[1].Command.State);
    }

    [Fact]
    public async Task Pipeline_statistics_are_pushed_to_those_who_asked()
    {
        await using var host = RealtimeHost.Create();
        var watcher = await host.ConnectAsync(RealtimeTopics.Pipeline);
        var other = await host.ConnectAsync(RealtimeTopics.Alerts);

        await host.Broadcaster.SendPipelineAsync();

        var update = Assert.Single(await watcher.WaitForAsync<PipelineUpdate>(RealtimeMethods.PipelineUpdated));
        await TestClient.SettleAsync();

        Assert.Equal(2, update.Stats.Lanes.Count);
        Assert.Equal(0, other.Count);
    }

    [Fact]
    public async Task Nothing_is_sent_for_a_topic_with_no_listeners()
    {
        await using var host = RealtimeHost.Create();

        await host.Broadcaster.SendPipelineAsync();

        // No listener, so no sequence number was spent.
        Assert.Equal(0, host.Broadcaster.SequenceOf(RealtimeTopics.Pipeline));
    }

    // ---- sequence numbers -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Sequence_numbers_increase_by_one_per_message_on_each_topic_independently()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts, RealtimeTopics.Commands);

        host.Alerts.Raise(new AlertKey(NodeA, AlertKind.Fault));
        host.Alerts.Raise(new AlertKey(NodeB, AlertKind.Fault));
        host.Publish(NodeA, 1, new CriticalAlert(1, "spike"));

        var alertUpdates = await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated, 2);
        var priority = await client.WaitForAsync<PriorityAlert>(RealtimeMethods.PriorityAlerted);

        // Alert updates and critical readings share the alerts topic, so they share one numbering: 1, 2, 3.
        var all = alertUpdates.Select(update => update.Sequence).Concat(priority.Select(alert => alert.Sequence)).Order().ToList();

        Assert.Equal([1L, 2L, 3L], all);
        Assert.Equal(3, host.Broadcaster.SequenceOf(RealtimeTopics.Alerts));
        Assert.Equal(0, host.Broadcaster.SequenceOf(RealtimeTopics.Commands));
    }

    [Fact]
    public async Task Alert_messages_arrive_in_the_order_they_were_sent_even_when_raised_from_many_threads()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                host.Alerts.Raise(new AlertKey($"AA:BB:CC:00:{thread:X2}:{i:X2}", AlertKind.Disconnected));
            }
        })));

        var updates = await client.WaitForAsync<AlertUpdate>(RealtimeMethods.AlertsUpdated, 200);
        var sequences = updates.Select(update => update.Sequence).ToList();

        // One loop sends, so what arrives is in numbered order whichever thread raised the event.
        Assert.Equal(sequences.Order(), sequences);
        Assert.Equal(200, sequences.Distinct().Count());
    }

    // ---- snapshots -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_trust_snapshot_carries_the_sequence_it_is_as_of_and_every_node()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Trust);
        host.Trust.Track(NodeA);
        host.Trust.Track(NodeB);
        host.Trust.Record(NodeA, TrustOutcome.Success);
        await host.Broadcaster.FlushAsync();
        var pushed = Assert.Single(await client.WaitForAsync<TrustUpdate>(RealtimeMethods.TrustUpdated));

        var snapshot = (await host.Http.GetFromJsonAsync<TrustSnapshot>("/api/realtime/trust"))!;

        // The snapshot is as of the last message sent, and holds every node, not only the ones that changed.
        Assert.Equal(pushed.Sequence, snapshot.Sequence);
        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Equal(2, snapshot.Summary.NodeCount);
    }

    [Fact]
    public async Task The_alert_snapshot_carries_active_alerts_and_the_recent_critical_readings()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync(RealtimeTopics.Alerts);
        host.Alerts.Raise(new AlertKey(NodeA, AlertKind.Fault), "bad");
        host.Publish(NodeB, 5, new CriticalAlert(2, "spike"));
        await client.WaitForAsync<PriorityAlert>(RealtimeMethods.PriorityAlerted);

        var snapshot = (await host.Http.GetFromJsonAsync<AlertSnapshot>("/api/realtime/alerts"))!;

        Assert.Equal(2, snapshot.Sequence);
        Assert.Equal(new AlertKey(NodeA, AlertKind.Fault), Assert.Single(snapshot.Alerts).Key);
        Assert.Equal("spike", Assert.Single(snapshot.RecentPriority).Reason);
    }

    [Fact]
    public async Task The_stream_snapshot_holds_the_latest_reading_from_each_node()
    {
        await using var host = RealtimeHost.Create();

        host.Publish(NodeA, 1);
        host.Publish(NodeA, 2);
        host.Publish(NodeB, 7);

        var snapshot = (await host.Http.GetFromJsonAsync<StreamSnapshot>("/api/realtime/stream"))!;

        Assert.Equal([2d, 7d], snapshot.Latest.Select(reading => reading.Value));
    }

    [Fact]
    public async Task The_command_and_pipeline_snapshots_answer()
    {
        await using var host = RealtimeHost.Create();

        var commands = await host.Http.GetFromJsonAsync<CommandSnapshot>("/api/realtime/commands");
        var pipeline = await host.Http.GetFromJsonAsync<PipelineUpdate>("/api/realtime/pipeline");

        Assert.NotNull(commands);
        Assert.Equal(2, pipeline!.Stats.Lanes.Count);
    }

    // ---- the connection ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_client_can_ask_which_transport_it_negotiated()
    {
        await using var host = RealtimeHost.Create();
        var client = await host.ConnectAsync();

        var transport = await client.Connection.InvokeAsync<string>(RealtimeMethods.GetTransport);

        // Long polling here, because the in-memory server is driven that way. The point is that it is readable.
        Assert.Equal("LongPolling", transport);
    }

    [Fact]
    public void The_typed_client_interface_uses_exactly_the_method_names_the_clients_listen_for()
    {
        var declared = typeof(ITelemetryClient).GetMethods().Select(method => method.Name).Order().ToList();

        var constants = typeof(RealtimeMethods)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(name => declared.Contains(name))
            .Order()
            .ToList();

        // Every method the server can call has a matching constant for the client to listen on, and the
        // reverse. A mismatch is not an error at runtime: SignalR just finds no handler and drops the message.
        Assert.Equal(declared, constants);
    }

    [Fact]
    public async Task The_negotiate_request_from_an_allowed_origin_is_answered_for_credentialed_cross_origin_use()
    {
        await using var host = RealtimeHost.Create();

        // What a browser sends first: a preflight, and then the negotiate POST carrying credentials.
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/hubs/telemetry/negotiate?negotiateVersion=1");
        preflight.Headers.Add("Origin", "http://localhost:5131");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-signalr-user-agent");

        var preflightResponse = await host.Http.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal("http://localhost:5131", preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", preflightResponse.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        var negotiate = new HttpRequestMessage(HttpMethod.Post, "/hubs/telemetry/negotiate?negotiateVersion=1");
        negotiate.Headers.Add("Origin", "http://localhost:5131");

        var negotiateResponse = await host.Http.SendAsync(negotiate);

        Assert.Equal(HttpStatusCode.OK, negotiateResponse.StatusCode);
        Assert.Equal("http://localhost:5131", negotiateResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", negotiateResponse.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task An_origin_that_is_not_allowed_gets_no_cors_headers()
    {
        await using var host = RealtimeHost.Create();

        var negotiate = new HttpRequestMessage(HttpMethod.Post, "/hubs/telemetry/negotiate?negotiateVersion=1");
        negotiate.Headers.Add("Origin", "http://evil.example");

        var response = await host.Http.SendAsync(negotiate);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
