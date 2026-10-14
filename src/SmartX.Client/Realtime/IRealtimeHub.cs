using Microsoft.AspNetCore.SignalR.Client;

namespace SmartX.Client.Realtime;

/// <summary>
/// The few things the client needs from a hub connection.
/// </summary>
/// <remarks>
/// A seam, so that everything above it (the connection's state machine, the feeds, the sequence gate, the
/// fallback) can be tested against a hub that does exactly what the test says: drops when told, delivers messages
/// in a chosen order, refuses to connect. A real <c>HubConnection</c> cannot be made to deliver a message
/// <i>between</i> two lines of a test, and the interleavings this client has to be correct under are exactly those.
/// <para>
/// Narrow on purpose. Calls take a string and return a string because that is all the hub's methods use, and it
/// keeps <see cref="object"/> out of the interface.
/// </para>
/// </remarks>
public interface IRealtimeHub : IAsyncDisposable
{
    /// <summary>Raised when the connection is lost and automatic reconnection begins.</summary>
    event Action? Reconnecting;

    /// <summary>Raised when automatic reconnection succeeds.</summary>
    event Action? Reconnected;

    /// <summary>Raised when the connection is closed for good: reconnection gave up, or the server ended it.</summary>
    event Action? Closed;

    /// <summary>Whether the connection is up right now.</summary>
    bool IsConnected { get; }

    /// <summary>Opens the connection. Throws if it cannot be opened.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Closes the connection.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>Listens for a server-to-client method. Disposing the result stops listening.</summary>
    IDisposable On<TMessage>(string method, Action<TMessage> handler);

    /// <summary>Calls a hub method that takes one string and returns nothing.</summary>
    Task InvokeAsync(string method, string argument, CancellationToken cancellationToken);

    /// <summary>Calls a hub method that takes nothing and returns a string.</summary>
    Task<string> InvokeForStringAsync(string method, CancellationToken cancellationToken);
}

/// <summary>
/// A reconnection schedule that never ends.
/// </summary>
/// <remarks>
/// <see cref="HubConnectionBuilder"/>'s no-argument automatic reconnect tries four times and then closes the
/// connection permanently. This returns a delay for every attempt, whatever its number, using the last configured
/// delay once the list runs out, so a connection that is down for an hour is still trying at the end of it.
/// </remarks>
public sealed class RealtimeRetryPolicy(IReadOnlyList<TimeSpan> delays) : IRetryPolicy
{
    private readonly IReadOnlyList<TimeSpan> _delays = delays.Count > 0 ? delays : [TimeSpan.FromSeconds(5)];

    /// <summary>The wait before attempt number <paramref name="attempt"/>, counting from zero.</summary>
    public TimeSpan DelayFor(long attempt) => _delays[(int)Math.Min(attempt, _delays.Count - 1)];

    /// <inheritdoc />
    public TimeSpan? NextRetryDelay(RetryContext retryContext) => DelayFor(retryContext.PreviousRetryCount);
}

/// <summary><see cref="IRealtimeHub"/> over a real SignalR <see cref="HubConnection"/>.</summary>
public sealed class SignalRHub : IRealtimeHub
{
    private readonly HubConnection _connection;

    /// <summary>Builds a connection to a hub. It is not opened until <see cref="StartAsync"/>.</summary>
    /// <param name="hubUri">Where the hub is.</param>
    /// <param name="options">How to reconnect.</param>
    /// <param name="accessToken">
    /// Asked for the token on every connection attempt, the first and each reconnect. A browser cannot put a header on a WebSocket,
    /// so SignalR sends the result as <c>?access_token=</c> in the URL, which the gateway reads for the hub's path only.
    /// </param>
    public SignalRHub(Uri hubUri, RealtimeClientOptions options, Func<Task<string?>>? accessToken = null)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUri, connection =>
            {
                if (accessToken is not null)
                {
                    connection.AccessTokenProvider = accessToken;
                }
            })
            .WithAutomaticReconnect(new RealtimeRetryPolicy(options.ReconnectDelays))
            .Build();

        _connection.Reconnecting += _ =>
        {
            Reconnecting?.Invoke();

            return Task.CompletedTask;
        };

        _connection.Reconnected += _ =>
        {
            Reconnected?.Invoke();

            return Task.CompletedTask;
        };

        _connection.Closed += _ =>
        {
            Closed?.Invoke();

            return Task.CompletedTask;
        };
    }

    /// <inheritdoc />
    public event Action? Reconnecting;

    /// <inheritdoc />
    public event Action? Reconnected;

    /// <inheritdoc />
    public event Action? Closed;

    /// <inheritdoc />
    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _connection.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _connection.StopAsync(cancellationToken);

    /// <inheritdoc />
    public IDisposable On<TMessage>(string method, Action<TMessage> handler) => _connection.On(method, handler);

    /// <inheritdoc />
    public Task InvokeAsync(string method, string argument, CancellationToken cancellationToken) =>
        _connection.InvokeAsync(method, argument, cancellationToken);

    /// <inheritdoc />
    public Task<string> InvokeForStringAsync(string method, CancellationToken cancellationToken) =>
        _connection.InvokeAsync<string>(method, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
