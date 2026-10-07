using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Client.Realtime;

/// <summary>
/// What every feed offers a page: a change event and the means to start and stop.
/// </summary>
/// <remarks>
/// The same shape <see cref="ITrustFeed"/> has always had, so a page treats every feed alike: read
/// <c>Current</c>, re-render on <see cref="Updated"/>, start when it opens and stop when it is disposed.
/// </remarks>
public interface IFeed : IAsyncDisposable
{
    /// <summary>Raised whenever the feed's current state is replaced.</summary>
    event Action? Updated;

    /// <summary>Begins delivering updates. Each call is matched by a <see cref="StopAsync"/>.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends one page's interest. The feed stops delivering when the last page has stopped.</summary>
    Task StopAsync();

    /// <summary>Fetches the current state now.</summary>
    Task RefreshNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What a feed can say about its own subscriptions, so that "no subscription leaks" is something a test can check.
/// </summary>
public interface IFeedDiagnostics
{
    /// <summary>How many subscribers the feed's Updated event has. One per page that is showing it.</summary>
    int ListenerCount { get; }

    /// <summary>How many handlers the feed has registered on the connection. Fixed while active, zero otherwise.</summary>
    int HubHandlerCount { get; }

    /// <summary>How many pages currently want the feed.</summary>
    int ActivePages { get; }
}

/// <summary>
/// The machinery every real-time feed shares: join a topic, take a snapshot, apply only the pushes that are newer,
/// and poll when the hub cannot be reached.
/// </summary>
/// <typeparam name="TSnapshot">The snapshot message the topic's HTTP endpoint returns.</typeparam>
/// <remarks>
/// <b>Snapshot plus stream is the correctness rule.</b> A push sent while the connection was down is lost, and
/// reconnecting alone does not recover it. So on every connect and every reconnect a feed does three things, in this
/// order:
/// <list type="number">
/// <item><description>Joins the topic, so pushes start arriving.</description></item>
/// <item><description>Fetches a snapshot of the current state over HTTP.</description></item>
/// <item><description>
/// Applies pushed messages only if their sequence number is newer than what it already holds.
/// </description></item>
/// </list>
/// The third step is what the rest depends on. A message sent just before the snapshot was taken can reach the
/// client just after the snapshot does, and applying it would overwrite newer state with older. So pushes that
/// arrive while the snapshot is being fetched are held, not applied; when the snapshot lands, those at or below its
/// sequence number are discarded as already included and the rest are applied in order; from then on a push is
/// applied only if its sequence is above the last one applied.
/// <para>
/// <b>Pages start and stop, they never connect.</b> <see cref="StartAsync"/> and <see cref="StopAsync"/> are
/// reference counted and serialised. Two pages can want the same feed at once, and a page that is disposed after
/// the next one has already opened (which rapid navigation does) must not take the feed away from it. The hub
/// handlers are registered once when the first page starts and removed when the last stops, so however a user
/// clicks around there is exactly one handler per message type, never one per visit.
/// </para>
/// <para>
/// <b>The last state is kept.</b> Stopping a feed stops the traffic, not the cache. A page that opens again renders
/// <c>Current</c> at once and the snapshot refreshes it behind the scenes: no blank frame, no reload.
/// </para>
/// </remarks>
public abstract class RealtimeFeed<TSnapshot> : IFeed, IFeedDiagnostics
    where TSnapshot : class, IRealtimeMessage
{
    private enum Phase
    {
        /// <summary>No page is using the feed.</summary>
        Idle,

        /// <summary>A snapshot is being fetched. Pushes that arrive are held.</summary>
        Syncing,

        /// <summary>Snapshot applied; pushes are applied if newer.</summary>
        Live,

        /// <summary>The hub is unreachable; snapshots are polled.</summary>
        Fallback,
    }

    private readonly RealtimeClientOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _state = new();
    private readonly List<IDisposable> _registrations = [];
    private readonly List<(long Sequence, Action Apply)> _held = [];

    private int _active;
    private Phase _phase = Phase.Idle;
    private long _applied;
    private int _epoch;
    private CancellationTokenSource? _activation;
    private CancellationTokenSource? _polling;

    protected RealtimeFeed(RealtimeConnection connection, TimeProvider time, RealtimeClientOptions options)
    {
        Connection = connection;
        _time = time;
        _options = options;
    }

    /// <inheritdoc />
    public event Action? Updated;

    /// <summary>The one connection this feed is built on.</summary>
    protected RealtimeConnection Connection { get; }

    /// <summary>The clock, for stamping when state arrived. Injected so a test can control it.</summary>
    protected TimeProvider Clock => _time;

    /// <summary>How many Updated subscribers there are. For tests: one per active page, never more.</summary>
    public int ListenerCount => Updated?.GetInvocationList().Length ?? 0;

    /// <summary>How many hub handlers this feed has registered. One per message type while active, zero otherwise.</summary>
    public int HubHandlerCount
    {
        get
        {
            lock (_state)
            {
                return _registrations.Count;
            }
        }
    }

    /// <summary>How many pages currently want this feed.</summary>
    public int ActivePages => Volatile.Read(ref _active);

    /// <summary>Whether the feed is delivering by polling because the hub is unreachable.</summary>
    public bool IsPolling
    {
        get
        {
            lock (_state)
            {
                return _phase == Phase.Fallback;
            }
        }
    }

    /// <summary>The topic this feed joins.</summary>
    protected abstract string Topic { get; }

    /// <summary>Fetches the current state over HTTP.</summary>
    protected abstract Task<ApiResult<TSnapshot>> FetchSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Replaces what the feed holds with a snapshot. Called with the feed's state lock held.</summary>
    protected abstract void ApplySnapshot(TSnapshot snapshot);

    /// <summary>Registers the feed's message handlers through <see cref="Listen{TMessage}"/>.</summary>
    protected abstract void RegisterHandlers();

    /// <summary>Records that a snapshot could not be fetched. Called with the feed's state lock held.</summary>
    protected virtual void OnFetchFailed(ApiProblem problem)
    {
    }

    /// <summary>Raises <see cref="Updated"/>. Never call it with the state lock held.</summary>
    protected virtual void Raise() => Updated?.Invoke();

    // ---- pushed messages ----------------------------------------------------------------------------------

    /// <summary>
    /// Listens for one kind of pushed message, applying it only if it is newer than what the feed holds.
    /// </summary>
    /// <param name="method">The server-to-client method name.</param>
    /// <param name="apply">Applies the message. Called with the feed's state lock held.</param>
    /// <param name="accept">
    /// Decides whether the message is this feed's at all. Two topics can share a client method, and a message for another
    /// topic must be ignored before the sequence rule sees it, because its sequence number means nothing here.
    /// </param>
    protected void Listen<TMessage>(string method, Action<TMessage> apply, Func<TMessage, bool>? accept = null)
        where TMessage : IRealtimeMessage
    {
        var registration = Connection.On<TMessage>(method, message => Receive(message, apply, accept));

        lock (_state)
        {
            _registrations.Add(registration);
        }
    }

    private void Receive<TMessage>(TMessage message, Action<TMessage> apply, Func<TMessage, bool>? accept)
        where TMessage : IRealtimeMessage
    {
        if (accept is not null && !accept(message))
        {
            return;
        }

        lock (_state)
        {
            switch (_phase)
            {
                case Phase.Idle:
                    return;

                case Phase.Syncing:
                    // A snapshot is on its way. Whether this message is older or newer than it cannot be known
                    // yet, so it is held until the snapshot says.
                    _held.Add((message.Sequence, () => apply(message)));

                    return;
            }

            // At or below what is already held: already reflected, or older than something that is. Discarding it
            // is what stops a late message overwriting newer state.
            if (message.Sequence <= _applied)
            {
                return;
            }

            apply(message);
            _applied = message.Sequence;
        }

        Raise();
    }

    // ---- starting and stopping ----------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        bool first;

        await _lifecycle.WaitAsync(cancellationToken);

        try
        {
            first = ++_active == 1;

            if (first)
            {
                Activate();
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        // Outside the lifecycle lock, so a page being disposed is never held up behind a slow snapshot.
        if (first)
        {
            await BeginAsync();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();

        try
        {
            if (_active == 0 || --_active > 0)
            {
                return;
            }

            await DeactivateAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void Activate()
    {
        lock (_state)
        {
            _activation = new CancellationTokenSource();
            _phase = Phase.Syncing;
            _held.Clear();
            _epoch++;
        }

        RegisterHandlers();

        Connection.Connected += OnConnected;
        Connection.StateChanged += OnStateChanged;

        // Returns at once. Opening a page never waits on the network.
        Connection.EnsureStarted();
    }

    /// <summary>Picks the way to get data, from the connection's state now.</summary>
    private async Task BeginAsync()
    {
        switch (Connection.State)
        {
            case RealtimeState.Live:
                await ResyncAsync();

                break;

            case RealtimeState.Polling:
                await StartFallbackAsync();

                break;

            // Connecting, or reconnecting: the connection will say when it is up, and the feed syncs then. The
            // last state it holds is on screen meanwhile.
        }
    }

    private async Task DeactivateAsync()
    {
        List<IDisposable> registrations;

        lock (_state)
        {
            _phase = Phase.Idle;
            _epoch++;
            _held.Clear();
            registrations = [.. _registrations];
            _registrations.Clear();
        }

        _activation?.Cancel();
        _activation?.Dispose();
        _activation = null;

        Connection.Connected -= OnConnected;
        Connection.StateChanged -= OnStateChanged;

        foreach (var registration in registrations)
        {
            registration.Dispose();
        }

        await StopFallbackAsync();
        await Connection.LeaveAsync(Topic);
    }

    // ---- the snapshot and the stream ----------------------------------------------------------------------

    private void OnConnected()
    {
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        // Up again, first time or after a drop. Whatever was pushed in between is gone, so: stop polling if it was
        // polling, join, snapshot.
        _ = Task.Run(async () =>
        {
            await StopFallbackAsync();
            await ResyncAsync();
        });
    }

    private void OnStateChanged()
    {
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        if (Connection.State == RealtimeState.Polling)
        {
            _ = Task.Run(StartFallbackAsync);
        }
    }

    /// <summary>Joins the topic and takes a snapshot, applying anything pushed meanwhile that is newer.</summary>
    protected async Task ResyncAsync()
    {
        int epoch;
        CancellationToken token;

        lock (_state)
        {
            if (_phase == Phase.Idle || _activation is null)
            {
                return;
            }

            epoch = ++_epoch;
            _phase = Phase.Syncing;
            _held.Clear();
            token = _activation.Token;
        }

        try
        {
            // 1. Join, so pushes start. 2. Snapshot. Joining first is what leaves no gap: anything that happens
            // after the join is either in the snapshot or pushed to us, and the sequence number settles which.
            await Connection.JoinAsync(Topic, token);

            // A page can leave while the join is in flight. The leave then went out first and this join arrived after
            // it, which would leave the server sending a topic nobody is listening to. Undo it.
            if (Volatile.Read(ref _active) == 0)
            {
                await Connection.LeaveAsync(Topic);

                return;
            }

            var result = await FetchSnapshotAsync(token);

            var finished = false;
            var failed = false;

            lock (_state)
            {
                if (epoch != _epoch || _phase == Phase.Idle)
                {
                    // Superseded by a newer resync, or the last page left. Nothing from this run is applied.
                    return;
                }

                if (result.IsSuccess)
                {
                    ApplySnapshot(result.Value);
                    _applied = result.Value.Sequence;

                    // 3. Pushes held while the snapshot was in flight: at or below its sequence they are already
                    // in it and are dropped; the rest are applied in order.
                    foreach (var (sequence, apply) in _held.OrderBy(entry => entry.Sequence))
                    {
                        if (sequence > _applied)
                        {
                            apply();
                            _applied = sequence;
                        }
                    }

                    _held.Clear();
                    _phase = Phase.Live;
                    finished = true;
                }
                else
                {
                    OnFetchFailed(result.Problem);
                    failed = true;
                }
            }

            if (finished || failed)
            {
                Raise();
            }

            if (failed)
            {
                _ = RetryLaterAsync(epoch, token);
            }
        }
        catch (OperationCanceledException)
        {
            // A page left while the snapshot was in flight.
        }
        catch (Exception)
        {
            // The join failed, usually because the connection dropped in the moment between the check and the
            // call. The connection raises Connected again when it is back, which resyncs; retrying as well covers
            // a failure that is not a dropped connection.
            _ = RetryLaterAsync(epoch, token);
        }
    }

    private async Task RetryLaterAsync(int epoch, CancellationToken token)
    {
        try
        {
            await Task.Delay(_options.SnapshotRetryInterval, _time, token);

            bool stillWanted;

            lock (_state)
            {
                stillWanted = epoch == _epoch && _phase == Phase.Syncing;
            }

            if (stillWanted && Connection.State == RealtimeState.Live)
            {
                await ResyncAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <inheritdoc />
    public virtual async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        if (IsPolling)
        {
            await PollOnceAsync(cancellationToken);

            return;
        }

        await ResyncAsync();
    }

    // ---- fallback -----------------------------------------------------------------------------------------

    /// <summary>
    /// The hub cannot be reached: deliver by polling instead, until the connection comes back.
    /// </summary>
    protected async Task StartFallbackAsync()
    {
        lock (_state)
        {
            if (_phase is Phase.Idle or Phase.Fallback)
            {
                return;
            }

            _phase = Phase.Fallback;
            _epoch++;
            _held.Clear();
        }

        await BeginPollingAsync();
    }

    /// <summary>Starts polling. The default asks for a snapshot every poll interval.</summary>
    protected virtual async Task BeginPollingAsync()
    {
        var polling = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _polling, polling);
        previous?.Cancel();
        previous?.Dispose();

        await PollOnceAsync(polling.Token);

        _ = PollLoopAsync(polling.Token);
    }

    /// <summary>Stops polling, if it is. Safe to call at any time.</summary>
    protected async Task StopFallbackAsync()
    {
        var polling = Interlocked.Exchange(ref _polling, null);
        polling?.Cancel();
        polling?.Dispose();

        await EndPollingAsync();
    }

    /// <summary>Stops whatever <see cref="BeginPollingAsync"/> started, beyond the default loop.</summary>
    protected virtual Task EndPollingAsync() => Task.CompletedTask;

    private async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await PollOnceAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Polling stopped.
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        var result = await FetchSnapshotAsync(token);

        lock (_state)
        {
            if (_phase != Phase.Fallback)
            {
                return;
            }

            if (result.IsSuccess)
            {
                ApplySnapshot(result.Value);
                _applied = result.Value.Sequence;
            }
            else
            {
                OnFetchFailed(result.Problem);
            }
        }

        Raise();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();

        try
        {
            if (_active > 0)
            {
                _active = 0;
                await DeactivateAsync();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
