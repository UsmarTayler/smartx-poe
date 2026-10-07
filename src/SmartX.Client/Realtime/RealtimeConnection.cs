namespace SmartX.Client.Realtime;

/// <summary>
/// The client's one real-time connection, and the state machine around it.
/// </summary>
/// <remarks>
/// <b>One connection for the whole app, owned here.</b> Not one per feed and not one per page, for three reasons.
/// A browser allows only a handful of connections to one host, and a connection per feed would spend them on the
/// same server. Each connection would negotiate, authenticate and be kept alive separately, multiplying the work
/// the server does for one tab. And connection state would be several facts, so the app bar could say "live" while
/// the alerts feed was reconnecting. With one connection there is one state, shown once. Registered scoped, which
/// in Blazor WebAssembly means one per browser tab for the tab's whole life; the feeds are built on it and pages
/// never see it, so a page opening or closing cannot open or close the connection.
/// <para>
/// <b>The lifecycle.</b> It starts <see cref="RealtimeState.Connecting"/>. If the first attempt succeeds the state is
/// <see cref="RealtimeState.Live"/>. If it fails the state is <see cref="RealtimeState.Polling"/> (the feeds poll,
/// because the hub cannot be reached) and the connection keeps trying in the background, so a proxy that was
/// blocking WebSockets and is fixed is noticed without a reload. Once connected, a drop is
/// <see cref="RealtimeState.Reconnecting"/> while SignalR retries on a schedule that never ends; if that lasts
/// longer than <see cref="RealtimeClientOptions.ReconnectingFallbackAfter"/> the state becomes Polling while the
/// retries continue. Every time the connection comes up, first or after a drop, <see cref="Connected"/> is raised,
/// and that is what makes each feed join its topic and take a fresh snapshot.
/// </para>
/// <para>
/// Nothing here knows what a feed is. It reports state and connectedness, and hands out message handlers.
/// </para>
/// </remarks>
public sealed class RealtimeConnection(IRealtimeHub hub, TimeProvider time, RealtimeClientOptions options) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();

    private bool _started;
    private CancellationTokenSource? _fallbackTimer;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised every time the connection is up: the first time, and after every reconnect. Feeds join their topic
    /// and take a snapshot in response, because anything pushed while the connection was down is lost.
    /// </summary>
    public event Action? Connected;

    /// <summary>Where the connection stands.</summary>
    public RealtimeState State { get; private set; } = RealtimeState.Connecting;

    /// <summary>
    /// The transport the connection negotiated (<c>WebSockets</c>, <c>ServerSentEvents</c> or
    /// <c>LongPolling</c>), once connected.
    /// </summary>
    public string? Transport { get; private set; }

    /// <summary>Whether the hub connection is up right now.</summary>
    public bool IsConnected => hub.IsConnected;

    /// <summary>
    /// Starts connecting, if it has not started. Returns at once; it does not wait for the connection.
    /// </summary>
    /// <remarks>
    /// A page opening must never wait on the network: it renders the feed's cached state straight away and the
    /// connection comes up behind it. Calling this again does nothing, which is what lets every feed call it
    /// without coordinating.
    /// </remarks>
    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            hub.Reconnecting += OnReconnecting;
            hub.Reconnected += OnReconnected;
            hub.Closed += OnClosed;
        }

        _ = ConnectAsync(_lifetime.Token);
    }

    /// <summary>Listens for a server-to-client method.</summary>
    public IDisposable On<TMessage>(string method, Action<TMessage> handler) => hub.On(method, handler);

    /// <summary>Joins a topic on the server. Does nothing if the connection is not up; feeds join again on connect.</summary>
    public async Task JoinAsync(string topic, CancellationToken cancellationToken = default)
    {
        if (hub.IsConnected)
        {
            await hub.InvokeAsync(SmartX.Shared.RealtimeMethods.Subscribe, topic, cancellationToken);
        }
    }

    /// <summary>Leaves a topic on the server. Best effort: if the connection is down the server has already forgotten us.</summary>
    public async Task LeaveAsync(string topic, CancellationToken cancellationToken = default)
    {
        if (!hub.IsConnected)
        {
            return;
        }

        try
        {
            await hub.InvokeAsync(SmartX.Shared.RealtimeMethods.Unsubscribe, topic, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The connection dropped between the check and the call. A dropped connection leaves every group.
        }
    }

    // ---- the state machine --------------------------------------------------------------------------------

    /// <summary>
    /// Tries to connect, again and again, until it does. Runs on its own, never awaited by a page.
    /// </summary>
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await hub.StartAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Could not connect at all. The dashboard must keep working, so the feeds poll, and this
                    // keeps trying so that it can upgrade as soon as the hub is reachable.
                    SetState(RealtimeState.Polling);
                    await Task.Delay(options.UpgradeRetryInterval, time, cancellationToken);

                    continue;
                }

                await OnUpAsync(cancellationToken);

                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed.
        }
    }

    private async Task OnUpAsync(CancellationToken cancellationToken)
    {
        CancelFallbackTimer();

        // For display and diagnosis only. A failure to ask must never stop the connection being used.
        try
        {
            Transport = await hub.InvokeForStringAsync(SmartX.Shared.RealtimeMethods.GetTransport, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Transport = null;
        }

        SetState(RealtimeState.Live);
        Connected?.Invoke();
    }

    private void OnReconnecting()
    {
        SetState(RealtimeState.Reconnecting);

        // If the drop outlasts this, the feeds poll for the rest of it. The reconnection carries on regardless.
        var timer = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _fallbackTimer, timer);
        previous?.Cancel();
        previous?.Dispose();

        _ = FallBackToPollingAfterAsync(timer.Token);
    }

    private async Task FallBackToPollingAfterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(options.ReconnectingFallbackAfter, time, cancellationToken);

            if (State == RealtimeState.Reconnecting)
            {
                SetState(RealtimeState.Polling);
            }
        }
        catch (OperationCanceledException)
        {
            // It reconnected first.
        }
    }

    private void OnReconnected() => _ = OnUpAsync(_lifetime.Token);

    private void OnClosed()
    {
        // Closed for good: automatic reconnection gave up or the server ended the connection. With the schedule
        // used here that is rare, but it must not strand the app: poll, and start connecting from scratch.
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        CancelFallbackTimer();
        SetState(RealtimeState.Polling);
        _ = ConnectAsync(_lifetime.Token);
    }

    private void CancelFallbackTimer()
    {
        var timer = Interlocked.Exchange(ref _fallbackTimer, null);
        timer?.Cancel();
        timer?.Dispose();
    }

    private void SetState(RealtimeState state)
    {
        lock (_gate)
        {
            if (State == state)
            {
                return;
            }

            State = state;
        }

        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        CancelFallbackTimer();

        hub.Reconnecting -= OnReconnecting;
        hub.Reconnected -= OnReconnected;
        hub.Closed -= OnClosed;

        await hub.DisposeAsync();
        _lifetime.Dispose();
    }
}
