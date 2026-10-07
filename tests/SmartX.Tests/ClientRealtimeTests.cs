using SmartX.Client.Realtime;
using SmartX.Client.Services;
using SmartX.Shared;
using static SmartX.Tests.ClientRealtimeKit;

namespace SmartX.Tests;

/// <summary>
/// The client's real-time layer, against a hub and a snapshot source that a test controls to the instant.
/// </summary>
public class ClientRealtimeTests
{
    private const string A = "AA:BB:CC:00:00:01";
    private const string B = "AA:BB:CC:00:00:02";
    private const string C = "AA:BB:CC:00:00:03";

    private static async Task<ClientRig> LiveRigAsync(Action<ClientRig>? configure = null)
    {
        var rig = ClientRig.Create(configure);
        rig.Connection.EnsureStarted();
        await rig.WaitForStateAsync(RealtimeState.Live);

        return rig;
    }

    private static NodeTrustSnapshot Held(ClientRig rig, string mac) => rig.Trust.Current.Nodes.Single(node => node.MacAddress == mac);

    // ---- snapshot, then stream: the ordering rule -------------------------------------------------------------------

    [Fact]
    public async Task A_push_older_than_the_snapshot_that_arrives_while_it_is_in_flight_is_discarded()
    {
        await using var rig = await LiveRigAsync();

        // The server's state when the snapshot is taken: sequence 9, node A faulted.
        rig.Snapshots.Trust = () => TrustSnapshot(9, Node(A, TrustBand.Fault, 0.2));
        var release = rig.Snapshots.HoldNextTrust();

        var starting = rig.Trust.StartAsync();
        await ClientRig.UntilAsync(() => rig.Snapshots.TrustCalls == 1, "the snapshot request to start");

        // The snapshot has been taken but its response has not reached the client. Meanwhile two pushes arrive:
        // one sent just before the snapshot was taken (5, saying A is nominal) and one after it (11, node B).
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(5, Node(A, TrustBand.Nominal, 0.9)));
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(11, Node(B, TrustBand.Degrading, 0.6)));

        release();
        await starting;

        // The older push did not overwrite the newer snapshot, and the newer push was applied on top of it.
        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);
        Assert.Equal(TrustBand.Degrading, Held(rig, B).Band);
    }

    [Fact]
    public async Task A_push_older_than_the_snapshot_that_arrives_after_it_was_applied_is_discarded()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(9, Node(A, TrustBand.Fault, 0.2));
        await rig.Trust.StartAsync();

        var updates = 0;
        rig.Trust.Updated += () => updates++;

        // A message from before the snapshot, delivered late: already included in the snapshot, and older.
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(8, Node(A, TrustBand.Nominal, 0.9)));

        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);
        Assert.Equal(0, updates);

        // The snapshot's own sequence is not newer than itself.
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(9, Node(A, TrustBand.Nominal, 0.9)));
        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);

        // The next one is.
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(10, Node(A, TrustBand.Degrading, 0.6)));
        Assert.Equal(TrustBand.Degrading, Held(rig, A).Band);
        Assert.Equal(1, updates);
    }

    [Fact]
    public async Task Pushes_held_during_the_snapshot_are_applied_in_sequence_order_not_arrival_order()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(9, Node(A, TrustBand.Nominal));
        var release = rig.Snapshots.HoldNextTrust();

        var starting = rig.Trust.StartAsync();
        await ClientRig.UntilAsync(() => rig.Snapshots.TrustCalls == 1, "the snapshot request to start");

        // Delivered the wrong way round. The server meant: first Degrading, then Fault.
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(12, Node(A, TrustBand.Fault, 0.2)));
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(11, Node(A, TrustBand.Degrading, 0.6)));

        release();
        await starting;

        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);
    }

    [Fact]
    public async Task A_push_after_the_snapshot_is_applied_the_moment_it_arrives_with_no_resubscribe()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(1, Node(A));
        await rig.Trust.StartAsync();

        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(2, Node(A, TrustBand.Fault, 0.2)));

        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);
        Assert.Equal(1, rig.Snapshots.TrustCalls);
    }

    [Fact]
    public async Task A_pushed_node_is_merged_into_the_list_in_worst_first_order_like_the_api_sorts_it()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(
            1, Node(A, TrustBand.Fault, 0.2), Node(B, TrustBand.Degrading, 0.6), Node(C, TrustBand.Nominal, 0.95));
        await rig.Trust.StartAsync();

        // C falls into fault with a worse score than A: it must now be the top row, as the API would have it.
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(2, Node(C, TrustBand.Fault, 0.1)));

        Assert.Equal([C, A, B], rig.Trust.Current.Nodes.Select(node => node.MacAddress));
        Assert.Equal(3, rig.Trust.Current.Nodes.Count);
    }

    [Fact]
    public async Task A_snapshot_replaces_what_the_feed_held_including_nodes_that_no_longer_exist()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(1, Node(A), Node(B));
        await rig.Trust.StartAsync();
        Assert.Equal(2, rig.Trust.Current.Nodes.Count);

        // The API restarted and forgot B. The next snapshot is the truth.
        rig.Snapshots.Trust = () => TrustSnapshot(1, Node(A));
        await rig.Trust.RefreshNowAsync();

        Assert.Equal([A], rig.Trust.Current.Nodes.Select(node => node.MacAddress));
    }

    [Fact]
    public async Task A_failed_snapshot_keeps_the_last_state_flags_the_problem_and_tries_again()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(1, Node(A));
        await rig.Trust.StartAsync();

        rig.Snapshots.FailTrust = true;
        await rig.Trust.RefreshNowAsync();

        // Stale and said so, not blanked.
        Assert.True(rig.Trust.Current.IsLoaded);
        Assert.NotNull(rig.Trust.Current.Problem);
        Assert.Single(rig.Trust.Current.Nodes);

        // The API is back; the retry finds it without anyone asking.
        rig.Snapshots.FailTrust = false;
        rig.Snapshots.Trust = () => TrustSnapshot(4, Node(A), Node(B));

        await ClientRig.UntilAsync(() => rig.Trust.Current.Problem is null && rig.Trust.Current.Nodes.Count == 2, "the retry to succeed");
    }

    [Fact]
    public async Task A_snapshot_that_returns_after_the_page_left_is_ignored()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(9, Node(A));
        var release = rig.Snapshots.HoldNextTrust();

        var starting = rig.Trust.StartAsync();
        await ClientRig.UntilAsync(() => rig.Snapshots.TrustCalls == 1, "the snapshot request to start");

        // The user navigates away before the response arrives.
        await rig.Trust.StopAsync();
        release();
        await starting;

        Assert.False(rig.Trust.Current.IsLoaded);
        Assert.Equal(0, rig.Trust.HubHandlerCount);
    }

    // ---- reconnecting ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconnecting_rejoins_the_topic_and_takes_a_fresh_snapshot()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(1, Node(A));
        await rig.Trust.StartAsync();
        Assert.Equal(1, rig.Hub.CallsTo(RealtimeMethods.Subscribe, RealtimeTopics.Trust));
        Assert.Equal(1, rig.Snapshots.TrustCalls);

        // While the connection is down the server's state moves on, and the pushes about it are lost.
        rig.Snapshots.Trust = () => TrustSnapshot(40, Node(A, TrustBand.Fault, 0.2), Node(B));

        rig.Hub.Drop();
        await rig.WaitForStateAsync(RealtimeState.Reconnecting);
        Assert.Empty(rig.Hub.Subscribed);

        rig.Hub.Restore();
        await rig.WaitForStateAsync(RealtimeState.Live);

        // Joined again, snapshot again, and the missed changes are there without a single push having arrived.
        await ClientRig.UntilAsync(() => rig.Trust.Current.Nodes.Count == 2, "the snapshot after reconnecting");
        Assert.Equal(2, rig.Hub.CallsTo(RealtimeMethods.Subscribe, RealtimeTopics.Trust));
        Assert.Equal(2, rig.Snapshots.TrustCalls);
        Assert.Contains(RealtimeTopics.Trust, rig.Hub.Subscribed);
        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);
    }

    [Fact]
    public async Task After_reconnecting_a_push_older_than_the_new_snapshot_is_discarded()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(1, Node(A));
        await rig.Trust.StartAsync();

        rig.Snapshots.Trust = () => TrustSnapshot(40, Node(A, TrustBand.Fault, 0.2));
        rig.Hub.Drop();
        rig.Hub.Restore();
        await ClientRig.UntilAsync(() => rig.Snapshots.TrustCalls == 2 && Held(rig, A).Band == TrustBand.Fault, "the second snapshot");

        // A message from before the drop, still in a buffer somewhere, arrives now.
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(30, Node(A, TrustBand.Nominal, 0.9)));

        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);
    }

    [Fact]
    public async Task Every_feed_rejoins_and_resnapshots_after_a_reconnect()
    {
        await using var rig = await LiveRigAsync();
        await rig.Trust.StartAsync();
        await rig.Alerts.StartAsync();
        await rig.Commands.StartAsync();
        await rig.Stream.StartAsync();
        await rig.Pipeline.StartAsync();

        rig.Hub.Drop();
        rig.Hub.Restore();

        await ClientRig.UntilAsync(
            () => rig.Snapshots.TrustCalls == 2 && rig.Snapshots.AlertCalls == 2 && rig.Snapshots.CommandCalls == 2
                  && rig.Snapshots.StreamCalls == 2 && rig.Snapshots.PipelineCalls == 2,
            "every feed to take a second snapshot");

        Assert.Equal(
            new[] { RealtimeTopics.Trust, RealtimeTopics.Alerts, RealtimeTopics.Commands, RealtimeTopics.Stream, RealtimeTopics.Pipeline }.Order(StringComparer.Ordinal),
            rig.Hub.Subscribed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_page_that_opens_while_the_connection_is_down_syncs_when_it_comes_back()
    {
        await using var rig = await LiveRigAsync();
        rig.Hub.Drop();
        await rig.WaitForStateAsync(RealtimeState.Reconnecting);

        rig.Snapshots.Trust = () => TrustSnapshot(3, Node(A));
        await rig.Trust.StartAsync();

        // Nothing to fetch from yet, and nothing blocks: the page is open and showing what it has.
        Assert.Equal(0, rig.Snapshots.TrustCalls);

        rig.Hub.Restore();

        await ClientRig.UntilAsync(() => rig.Trust.Current.IsLoaded, "the snapshot once connected");
        Assert.Single(rig.Trust.Current.Nodes);
    }

    // ---- the connection's state machine ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_connection_goes_live_once_started_and_records_the_transport()
    {
        await using var rig = ClientRig.Create(r => r.Hub.Transport = "WebSockets");
        var connected = 0;
        rig.Connection.Connected += () => connected++;

        Assert.Equal(RealtimeState.Connecting, rig.Connection.State);

        rig.Connection.EnsureStarted();
        await rig.WaitForStateAsync(RealtimeState.Live);

        Assert.Equal("WebSockets", rig.Connection.Transport);
        Assert.Equal(1, connected);
    }

    [Fact]
    public async Task Starting_the_connection_twice_connects_once()
    {
        await using var rig = ClientRig.Create();

        rig.Connection.EnsureStarted();
        rig.Connection.EnsureStarted();
        rig.Connection.EnsureStarted();
        await rig.WaitForStateAsync(RealtimeState.Live);

        Assert.Equal(1, rig.Hub.StartCalls);
    }

    [Fact]
    public async Task A_hub_that_cannot_be_reached_puts_the_connection_in_polling_and_it_keeps_trying()
    {
        await using var rig = ClientRig.Create(r => r.Hub.FailStart = true);

        rig.Connection.EnsureStarted();
        await rig.WaitForStateAsync(RealtimeState.Polling);

        await ClientRig.UntilAsync(() => rig.Hub.StartCalls >= 3, "repeated connection attempts");

        // The proxy is fixed. The next attempt succeeds and the connection upgrades without a reload.
        rig.Hub.FailStart = false;
        await rig.WaitForStateAsync(RealtimeState.Live);
    }

    [Fact]
    public async Task A_drop_that_lasts_longer_than_the_threshold_becomes_polling_and_a_restore_goes_live_again()
    {
        await using var rig = await LiveRigAsync();
        var seen = new List<RealtimeState>();
        rig.Connection.StateChanged += () => seen.Add(rig.Connection.State);

        rig.Hub.Drop();
        await rig.WaitForStateAsync(RealtimeState.Polling);

        rig.Hub.Restore();
        await rig.WaitForStateAsync(RealtimeState.Live);

        Assert.Equal([RealtimeState.Reconnecting, RealtimeState.Polling, RealtimeState.Live], seen);
    }

    [Fact]
    public async Task A_short_drop_never_shows_polling()
    {
        await using var rig = await LiveRigAsync(r => r.Options.ReconnectingFallbackAfter = TimeSpan.FromSeconds(30));
        var seen = new List<RealtimeState>();
        rig.Connection.StateChanged += () => seen.Add(rig.Connection.State);

        rig.Hub.Drop();
        rig.Hub.Restore();
        await rig.WaitForStateAsync(RealtimeState.Live);
        await Task.Delay(100);

        Assert.Equal([RealtimeState.Reconnecting, RealtimeState.Live], seen);
    }

    [Fact]
    public async Task A_connection_closed_for_good_falls_back_to_polling_and_starts_over()
    {
        await using var rig = await LiveRigAsync();
        var seen = new List<RealtimeState>();
        rig.Connection.StateChanged += () => seen.Add(rig.Connection.State);

        rig.Hub.Close();

        // The hub is reachable, so starting over succeeds at once: Polling is a moment, and is recorded and not
        // waited for.
        await rig.WaitForStateAsync(RealtimeState.Live);

        Assert.Equal([RealtimeState.Polling, RealtimeState.Live], seen);
        Assert.Equal(2, rig.Hub.StartCalls);
    }

    [Fact]
    public void The_reconnect_schedule_never_ends_and_repeats_its_last_delay()
    {
        var policy = new RealtimeRetryPolicy([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)]);

        Assert.Equal(TimeSpan.Zero, policy.DelayFor(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(2));

        // SignalR's own default would have given up by now.
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(3));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(10_000));
        Assert.NotNull(policy.NextRetryDelay(new Microsoft.AspNetCore.SignalR.Client.RetryContext { PreviousRetryCount = 50_000 }));
    }

    [Fact]
    public void An_empty_schedule_still_retries()
    {
        Assert.True(new RealtimeRetryPolicy([]).DelayFor(0) > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(ConnectionStatus.Connected, RealtimeState.Live, ConnectionDisplay.Live)]
    [InlineData(ConnectionStatus.Connected, RealtimeState.Reconnecting, ConnectionDisplay.Reconnecting)]
    [InlineData(ConnectionStatus.Connected, RealtimeState.Polling, ConnectionDisplay.Polling)]
    [InlineData(ConnectionStatus.Unreachable, RealtimeState.Polling, ConnectionDisplay.Unreachable)]
    [InlineData(ConnectionStatus.Checking, RealtimeState.Connecting, ConnectionDisplay.Checking)]
    [InlineData(ConnectionStatus.Unreachable, RealtimeState.Connecting, ConnectionDisplay.Unreachable)]
    [InlineData(ConnectionStatus.Unreachable, RealtimeState.Live, ConnectionDisplay.Live)]
    public void The_indicator_reads_both_the_push_connection_and_the_apis_reachability(
        ConnectionStatus api, RealtimeState realtime, ConnectionDisplay expected)
    {
        Assert.Equal(expected, ConnectionDisplays.From(api, realtime));
    }

    // ---- fallback to polling -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_hub_is_unavailable_the_trust_feed_polls_and_when_it_returns_the_feed_upgrades_and_stops_polling()
    {
        await using var rig = ClientRig.Create(r => r.Hub.FailStart = true);
        rig.Http.Nodes = () => [Node(A, TrustBand.Degrading, 0.6)];
        rig.Snapshots.Trust = () => TrustSnapshot(3, Node(A, TrustBand.Nominal), Node(B));

        await rig.Trust.StartAsync();

        // No hub, so the page still gets its data: from the polling feed that existed before push.
        await ClientRig.UntilAsync(() => rig.Trust.IsPolling && rig.Trust.Current.IsLoaded, "polling to deliver data");
        Assert.Equal(TrustBand.Degrading, Held(rig, A).Band);
        Assert.NotNull(rig.Trust.RefreshInterval);
        Assert.True(rig.Http.Requests > 0);
        Assert.Equal(0, rig.Snapshots.TrustCalls);

        // The hub becomes reachable. The feed goes live, takes a real-time snapshot and stops polling.
        rig.Hub.FailStart = false;
        await rig.WaitForStateAsync(RealtimeState.Live);
        await ClientRig.UntilAsync(() => !rig.Trust.IsPolling && rig.Trust.Current.Nodes.Count == 2, "the upgrade to push");

        Assert.Null(rig.Trust.RefreshInterval);
        Assert.Equal(TrustBand.Nominal, Held(rig, A).Band);

        // Settle any request that was already in flight, then check nothing more is being asked for.
        await Task.Delay(300);
        var requests = rig.Http.Requests;
        await Task.Delay(1500);

        Assert.Equal(requests, rig.Http.Requests);
    }

    [Fact]
    public async Task While_polling_the_feeds_other_than_trust_poll_their_own_snapshots_and_upgrade_too()
    {
        await using var rig = ClientRig.Create(r => r.Hub.FailStart = true);
        var alerts = 0;
        rig.Snapshots.Alerts = () => new AlertSnapshot(++alerts, [Alert(A, AlertKind.Fault)], new AlertStats(1, 0, 0, 1, 0), []);

        await rig.Alerts.StartAsync();
        await rig.WaitForStateAsync(RealtimeState.Polling);

        // Several polls: it keeps asking.
        await ClientRig.UntilAsync(() => rig.Snapshots.AlertCalls >= 3, "the alert feed to poll repeatedly");
        Assert.Single(rig.Alerts.Current.Alerts);
        Assert.True(rig.Alerts.IsPolling);

        rig.Hub.FailStart = false;
        await rig.WaitForStateAsync(RealtimeState.Live);
        await ClientRig.UntilAsync(() => !rig.Alerts.IsPolling && rig.Hub.Subscribed.Contains(RealtimeTopics.Alerts), "the alert feed to go live");

        var calls = rig.Snapshots.AlertCalls;
        await Task.Delay(300);

        // No longer polling: the count has stopped rising.
        Assert.Equal(calls, rig.Snapshots.AlertCalls);
    }

    [Fact]
    public async Task The_manual_refresh_button_polls_once_when_polling_and_resyncs_when_live()
    {
        await using var rig = ClientRig.Create(r => r.Hub.FailStart = true);
        rig.Http.Nodes = () => [Node(A)];

        await rig.Trust.StartAsync();
        await ClientRig.UntilAsync(() => rig.Trust.IsPolling && rig.Trust.Current.IsLoaded, "polling");

        var before = rig.Http.Requests;
        await rig.Trust.RefreshNowAsync();

        Assert.True(rig.Http.Requests >= before + 2);
    }

    [Fact]
    public async Task While_polling_an_unreachable_api_leaves_the_last_data_on_screen_and_flags_it()
    {
        await using var rig = ClientRig.Create(r => r.Hub.FailStart = true);
        rig.Http.Nodes = () => [Node(A)];
        await rig.Trust.StartAsync();
        await ClientRig.UntilAsync(() => rig.Trust.Current.IsLoaded, "the first poll");

        rig.Http.Fail = true;
        await ClientRig.UntilAsync(() => rig.Trust.Current.Problem is not null, "a failed poll");

        Assert.Single(rig.Trust.Current.Nodes);
        Assert.Equal(ConnectionStatus.Unreachable, rig.Api.Status);
    }

    // ---- navigation: subscriptions and state ---------------------------------------------------------------------------------

    /// <summary>What a page does with a feed: subscribe when it opens, unsubscribe when it is disposed.</summary>
    private sealed class Page(IFeed feed)
    {
        private readonly Action _onChanged = () => { };

        public async Task OpenAsync()
        {
            feed.Updated += _onChanged;
            await feed.StartAsync();
        }

        public async Task CloseAsync()
        {
            feed.Updated -= _onChanged;
            await feed.StopAsync();
        }
    }

    public static TheoryData<string> Feeds => ["trust", "alerts", "commands", "stream", "pipeline"];

    private static (IFeed Feed, IFeedDiagnostics Diagnostics, string Topic, int Handlers) Pick(ClientRig rig, string name) => name switch
    {
        "trust" => (rig.Trust, rig.Trust, RealtimeTopics.Trust, 1),
        "alerts" => (rig.Alerts, rig.Alerts, RealtimeTopics.Alerts, 2),
        "commands" => (rig.Commands, rig.Commands, RealtimeTopics.Commands, 1),
        "stream" => (rig.Stream, rig.Stream, RealtimeTopics.Stream, 1),
        _ => (rig.Pipeline, rig.Pipeline, RealtimeTopics.Pipeline, 1),
    };

    [Theory]
    [MemberData(nameof(Feeds))]
    public async Task Switching_pages_back_and_forth_many_times_leaves_no_subscription_behind(string name)
    {
        await using var rig = await LiveRigAsync();
        var (feed, diagnostics, topic, _) = Pick(rig, name);

        for (var visit = 0; visit < 300; visit++)
        {
            var page = new Page(feed);
            await page.OpenAsync();
            await page.CloseAsync();
        }

        // Three hundred visits and nothing is left over.
        Assert.Equal(0, diagnostics.ListenerCount);
        Assert.Equal(0, diagnostics.HubHandlerCount);
        Assert.Equal(0, diagnostics.ActivePages);
        Assert.Equal(0, rig.Hub.TotalHandlers);
        Assert.Empty(rig.Hub.Subscribed);
        Assert.Equal(rig.Hub.CallsTo(RealtimeMethods.Subscribe, topic), rig.Hub.CallsTo(RealtimeMethods.Unsubscribe, topic));
    }

    [Theory]
    [MemberData(nameof(Feeds))]
    public async Task After_the_churn_each_active_page_has_exactly_one_handler_and_the_connection_has_one_per_message_type(string name)
    {
        await using var rig = await LiveRigAsync();
        var (feed, diagnostics, topic, handlers) = Pick(rig, name);

        for (var visit = 0; visit < 200; visit++)
        {
            var page = new Page(feed);
            await page.OpenAsync();
            await page.CloseAsync();
        }

        // Now two pages are open at once.
        var first = new Page(feed);
        var second = new Page(feed);
        await first.OpenAsync();
        await second.OpenAsync();

        Assert.Equal(2, diagnostics.ListenerCount);
        Assert.Equal(2, diagnostics.ActivePages);
        Assert.Equal(handlers, diagnostics.HubHandlerCount);
        Assert.Equal(handlers, rig.Hub.TotalHandlers);
        Assert.Equal([topic], rig.Hub.Subscribed);

        // One closes. The other still has its subscription and its handler, and the topic is still joined.
        await first.CloseAsync();

        Assert.Equal(1, diagnostics.ListenerCount);
        Assert.Equal(handlers, diagnostics.HubHandlerCount);
        Assert.Equal([topic], rig.Hub.Subscribed);

        await second.CloseAsync();

        Assert.Equal(0, diagnostics.HubHandlerCount);
        Assert.Empty(rig.Hub.Subscribed);
    }

    [Fact]
    public async Task A_page_disposed_after_the_next_page_opened_does_not_take_the_feed_away_from_it()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(1, Node(A));

        // Rapid navigation: the new page's OnInitialized runs before the old page's disposal has finished.
        var old = new Page(rig.Trust);
        await old.OpenAsync();

        var next = new Page(rig.Trust);
        await next.OpenAsync();
        await old.CloseAsync();

        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(2, Node(A, TrustBand.Fault, 0.2)));

        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);
        Assert.Equal(1, rig.Trust.ListenerCount);
        Assert.Equal([RealtimeTopics.Trust], rig.Hub.Subscribed);
    }

    [Fact]
    public async Task Pages_opening_and_closing_at_the_same_time_leave_nothing_behind()
    {
        await using var rig = await LiveRigAsync();

        // Forty tabs' worth of clicking at once, on every feed.
        var clickers = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            IFeed[] feeds = [rig.Trust, rig.Alerts, rig.Commands, rig.Stream, rig.Pipeline];

            for (var visit = 0; visit < 20; visit++)
            {
                var pages = feeds.Select(feed => new Page(feed)).ToList();

                foreach (var page in pages)
                {
                    await page.OpenAsync();
                }

                foreach (var page in pages)
                {
                    await page.CloseAsync();
                }
            }
        }));

        await Task.WhenAll(clickers);

        Assert.Equal(0, rig.Hub.TotalHandlers);
        Assert.Empty(rig.Hub.Subscribed);
        Assert.Equal(0, rig.Trust.ListenerCount + rig.Alerts.ListenerCount + rig.Commands.ListenerCount + rig.Stream.ListenerCount + rig.Pipeline.ListenerCount);

        // And a feed still works afterwards, with exactly one handler.
        var page = new Page(rig.Trust);
        await page.OpenAsync();

        Assert.Equal(1, rig.Hub.HandlerCount(RealtimeMethods.TrustUpdated));
    }

    [Fact]
    public async Task Returning_to_a_page_shows_the_last_known_state_at_once_and_then_continues_live()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(5, Node(A, TrustBand.Fault, 0.2), Node(B));

        var first = new Page(rig.Trust);
        await first.OpenAsync();
        await first.CloseAsync();

        // The user comes back. The next snapshot is slow, and the page must not wait for it.
        rig.Snapshots.Trust = () => TrustSnapshot(9, Node(A), Node(B), Node(C));
        var release = rig.Snapshots.HoldNextTrust();

        var again = new Page(rig.Trust);
        var opening = again.OpenAsync();
        await ClientRig.UntilAsync(() => rig.Snapshots.TrustCalls == 2, "the second snapshot request to start");

        // What the page reads on its first render, before anything has come back from the network: the last state,
        // loaded, with both nodes. No skeleton, no blank frame.
        Assert.True(rig.Trust.Current.IsLoaded);
        Assert.Equal(2, rig.Trust.Current.Nodes.Count);
        Assert.Equal(TrustBand.Fault, Held(rig, A).Band);

        release();
        await opening;

        // Then it carries on from the fresh snapshot, and continues live.
        Assert.Equal(3, rig.Trust.Current.Nodes.Count);
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(10, Node(C, TrustBand.Fault, 0.1)));
        Assert.Equal(TrustBand.Fault, Held(rig, C).Band);
    }

    [Fact]
    public async Task Every_feed_keeps_its_last_state_after_its_last_page_leaves()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Alerts = () => new AlertSnapshot(1, [Alert(A, AlertKind.Fault)], new AlertStats(1, 0, 0, 1, 0), []);
        rig.Snapshots.Commands = () => new CommandSnapshot(1, []);
        rig.Snapshots.Stream = () => new StreamSnapshot(1, [Reading(A, 5)]);
        rig.Snapshots.Pipeline = () => new PipelineUpdate(1, new PipelineStats([], 10_000, 0, Start));

        await rig.Alerts.StartAsync();
        await rig.Stream.StartAsync();
        await rig.Pipeline.StartAsync();
        await rig.Commands.StartAsync();
        await rig.Alerts.StopAsync();
        await rig.Stream.StopAsync();
        await rig.Pipeline.StopAsync();
        await rig.Commands.StopAsync();

        Assert.Single(rig.Alerts.Current.Alerts);
        Assert.Single(rig.Stream.Current.Latest);
        Assert.NotNull(rig.Pipeline.Current.Stats);
        Assert.True(rig.Commands.Current.IsLoaded);
    }

    [Fact]
    public async Task A_message_that_arrives_when_no_page_is_listening_is_ignored()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Trust = () => TrustSnapshot(1, Node(A));

        await rig.Trust.StartAsync();
        await rig.Trust.StopAsync();

        // No handler is registered, so a late delivery finds nothing. If one slipped through it would apply
        // to a feed nobody is watching and then be treated as newer than the next snapshot's baseline.
        rig.Hub.Deliver(RealtimeMethods.TrustUpdated, TrustUpdate(99, Node(A, TrustBand.Fault, 0.1)));

        Assert.Equal(TrustBand.Nominal, Held(rig, A).Band);
    }

    // ---- the other feeds -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_alert_feed_merges_raised_and_cleared_alerts_and_announces_critical_readings_once()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Alerts = () => new AlertSnapshot(
            10, [Alert(A, AlertKind.Disconnected, 3)], new AlertStats(1, 3, 0, 1, 0), []);
        await rig.Alerts.StartAsync();

        var critical = new List<PriorityAlert>();
        rig.Alerts.PriorityAlertReceived += critical.Add;

        rig.Hub.Deliver(RealtimeMethods.AlertsUpdated, new AlertUpdate(
            11, [Alert(B, AlertKind.Fault)], [], new AlertStats(2, 3, 0, 2, 0)));
        Assert.Equal(2, rig.Alerts.Current.Alerts.Count);

        rig.Hub.Deliver(RealtimeMethods.AlertsUpdated, new AlertUpdate(
            12, [], [new AlertKey(A, AlertKind.Disconnected)], new AlertStats(2, 3, 1, 1, 0)));
        Assert.Equal(B, Assert.Single(rig.Alerts.Current.Alerts).Key.Mac);
        Assert.Equal(1, rig.Alerts.Current.Stats.Cleared);

        var spike = new PriorityAlert(13, C, "power spike", 1, 9000, "W", Start, Start);
        rig.Hub.Deliver(RealtimeMethods.PriorityAlerted, spike);

        Assert.Equal(spike, Assert.Single(critical));
        Assert.Equal(spike, Assert.Single(rig.Alerts.Current.RecentPriority));

        // Critical readings share the alerts topic's sequence, so a repeat of the same one is discarded.
        rig.Hub.Deliver(RealtimeMethods.PriorityAlerted, spike);
        Assert.Single(critical);
    }

    [Fact]
    public async Task The_alert_feed_applies_an_alert_update_by_the_same_sequence_rule_as_trust()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Alerts = () => new AlertSnapshot(20, [Alert(A, AlertKind.Fault)], new AlertStats(1, 0, 0, 1, 0), []);
        var release = rig.Snapshots.HoldNextAlerts();

        var starting = rig.Alerts.StartAsync();
        await ClientRig.UntilAsync(() => rig.Snapshots.AlertCalls == 1, "the alert snapshot request to start");

        // An older message saying the alert was cleared, arriving after a snapshot in which it is active.
        rig.Hub.Deliver(RealtimeMethods.AlertsUpdated, new AlertUpdate(18, [], [new AlertKey(A, AlertKind.Fault)], new AlertStats(0, 0, 1, 0, 0)));

        release();
        await starting;

        Assert.Single(rig.Alerts.Current.Alerts);
    }

    [Fact]
    public async Task The_command_feed_merges_state_changes_by_id_newest_first()
    {
        await using var rig = await LiveRigAsync();
        await rig.Commands.StartAsync();

        var first = Cmd(Start, CommandState.Pending);
        var second = Cmd(Start.AddSeconds(5), CommandState.Pending);

        rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(1, first, CommandEvent.Issued));
        rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(2, second, CommandEvent.Issued));
        rig.Hub.Deliver(RealtimeMethods.CommandChanged, new CommandUpdate(3, first with { State = CommandState.Acknowledged }, CommandEvent.Acknowledged));

        Assert.Equal([second.Id, first.Id], rig.Commands.Current.Commands.Select(command => command.Id));
        Assert.Equal(CommandState.Acknowledged, rig.Commands.Current.Commands[1].State);
    }

    private static Command Cmd(DateTimeOffset issuedAt, CommandState state) => new(
        Guid.NewGuid(), A, ActuatorKind.Valve, CommandAction.ValveClose, null, 0, issuedAt, state, null, false, null, null, null, null, null, null, null);

    [Fact]
    public async Task The_stream_feed_keeps_the_latest_reading_per_node_and_announces_each_batch()
    {
        await using var rig = await LiveRigAsync();
        rig.Snapshots.Stream = () => new StreamSnapshot(1, [Reading(A, 1)]);
        await rig.Stream.StartAsync();

        var batches = new List<ReadingsBatch>();
        rig.Stream.BatchReceived += batches.Add;

        rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, new ReadingsBatch(2, [Reading(A, 2, 1), Reading(B, 7, 1), Reading(A, 3, 2)], 0));
        rig.Hub.Deliver(RealtimeMethods.ReadingsArrived, new ReadingsBatch(3, [Reading(B, 8, 3)], 4));

        // Every batch is announced, so a page that wants every reading gets them.
        Assert.Equal([2L, 3L], batches.Select(batch => batch.Sequence));

        var latest = rig.Stream.Current.Latest;
        Assert.Equal(3, latest[A].Value);
        Assert.Equal(8, latest[B].Value);
        Assert.Equal(4, rig.Stream.Current.ReadingsReceived);
        Assert.Equal(4, rig.Stream.Current.Dropped);
    }

    [Fact]
    public async Task The_pipeline_feed_shows_the_latest_statistics()
    {
        await using var rig = await LiveRigAsync();
        await rig.Pipeline.StartAsync();

        rig.Hub.Deliver(RealtimeMethods.PipelineUpdated, new PipelineUpdate(1, new PipelineStats([], 10_000, 7, Start)));

        Assert.Equal(7, rig.Pipeline.Current.Stats!.RejectedWhenFull);
    }

    // ---- the telemetry page's merge ----------------------------------------------------------------------------------------

    [Fact]
    public void Pushed_readings_become_the_packet_type_each_sensors_registration_calls_for()
    {
        var api = new SmartXApiClient(new HttpClient(new StubTrustHandler()) { BaseAddress = new Uri("http://stub/") });
        var service = new TelemetryService(api, new ApiConnection(api), TimeProvider.System);

        var meter = new SensorRegistration(A, new DeploymentLocation("Room", "zone-1", "N"), SensorCategory.PowerConsumption, "W", new ValueRange(0, 9000), TelemetryPayloadKind.Integer);
        var thermometer = new SensorRegistration(B, new DeploymentLocation("Room", "zone-1", "N"), SensorCategory.Environmental, "°C", new ValueRange(0, 50), TelemetryPayloadKind.Real);
        var valve = new SensorRegistration(C, new DeploymentLocation("Room", "zone-1", "N"), SensorCategory.Actuator, "state", new ValueRange(0, 1), TelemetryPayloadKind.State, null, ActuatorKind.Valve);
        var sensors = new[] { meter, thermometer, valve }.ToDictionary(sensor => sensor.MacAddress);

        service.ApplyPushed(
            [Reading(A, 120), Reading(B, 21.5, 1), new ReadingPush(C, Start.AddSeconds(2), 1, "state", true, Start)],
            sensors);

        Assert.IsType<NumericPacket<int>>(service.For(A).Latest);
        Assert.IsType<NumericPacket<float>>(service.For(B).Latest);
        Assert.True(Assert.IsType<StatePacket>(service.For(C).Latest).Value);
    }

    [Fact]
    public void A_pushed_reading_no_newer_than_the_latest_is_ignored_and_the_history_is_bounded()
    {
        var api = new SmartXApiClient(new HttpClient(new StubTrustHandler()) { BaseAddress = new Uri("http://stub/") });
        var service = new TelemetryService(api, new ApiConnection(api), TimeProvider.System);
        var meter = new SensorRegistration(A, new DeploymentLocation("Room", "zone-1", "N"), SensorCategory.PowerConsumption, "W", new ValueRange(0, 9000), TelemetryPayloadKind.Integer);
        var sensors = new Dictionary<string, SensorRegistration> { [A] = meter };

        service.ApplyPushed([Reading(A, 100, 10)], sensors);
        service.ApplyPushed([Reading(A, 999, 10), Reading(A, 998, 5)], sensors);

        // The same instant, and an older one, are repeats of what the card already holds.
        Assert.Single(service.For(A).Recent);

        for (var i = 11; i < 700; i++)
        {
            service.ApplyPushed([Reading(A, i, i)], sensors);
        }

        Assert.Equal(256, service.For(A).Recent.Count);
        Assert.Equal(699, ((NumericPacket<int>)service.For(A).Latest!).Value);
    }
}
