using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartX.Client.Realtime;
using SmartX.Client.Services;
using SmartX.Client.State;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// A hub that does exactly what a test says: refuses to connect, drops, delivers a message at a chosen moment.
/// </summary>
/// <remarks>
/// A real <c>HubConnection</c> cannot be made to deliver a message <i>between</i> two lines of a test, and the
/// interleavings the client has to be correct under (a push landing while a snapshot is in flight, a drop in the
/// middle of a resync) are exactly those.
/// </remarks>
public sealed class FakeRealtimeHub : IRealtimeHub
{
    private readonly Lock _gate = new();
    private readonly List<(string Method, Delegate Handler)> _handlers = [];
    private readonly List<(string Method, string Argument)> _calls = [];
    private readonly HashSet<string> _subscribed = [];

    private bool _connected;
    private int _startCalls;

    public event Action? Reconnecting;

    public event Action? Reconnected;

    public event Action? Closed;

    /// <summary>When true, <see cref="StartAsync"/> throws, as when the hub cannot be reached.</summary>
    public volatile bool FailStart;

    public string Transport { get; set; } = "WebSockets";

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connected;
            }
        }
    }

    public int StartCalls => Volatile.Read(ref _startCalls);

    /// <summary>Handlers registered right now for a method.</summary>
    public int HandlerCount(string method)
    {
        lock (_gate)
        {
            return _handlers.Count(entry => entry.Method == method);
        }
    }

    public int TotalHandlers
    {
        get
        {
            lock (_gate)
            {
                return _handlers.Count;
            }
        }
    }

    /// <summary>The topics joined right now.</summary>
    public IReadOnlyCollection<string> Subscribed
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscribed];
            }
        }
    }

    /// <summary>Every call the client made to the hub, in order, as text. For a failure message.</summary>
    public string CallLog
    {
        get
        {
            lock (_gate)
            {
                return string.Join(", ", _calls.Select(call => $"{call.Method}({call.Argument})"));
            }
        }
    }

    public int CallsTo(string method, string? argument = null)
    {
        lock (_gate)
        {
            return _calls.Count(call => call.Method == method && (argument is null || call.Argument == argument));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCalls);

        if (FailStart)
        {
            throw new HttpRequestException("The hub could not be reached.");
        }

        lock (_gate)
        {
            _connected = true;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _connected = false;
        }

        return Task.CompletedTask;
    }

    public IDisposable On<TMessage>(string method, Action<TMessage> handler)
    {
        var entry = (method, (Delegate)handler);

        lock (_gate)
        {
            _handlers.Add(entry);
        }

        return new Registration(this, entry);
    }

    public Task InvokeAsync(string method, string argument, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add((method, argument));

            if (method == RealtimeMethods.Subscribe)
            {
                _subscribed.Add(argument);
            }
            else if (method == RealtimeMethods.Unsubscribe)
            {
                _subscribed.Remove(argument);
            }
        }

        return Task.CompletedTask;
    }

    public Task<string> InvokeForStringAsync(string method, CancellationToken cancellationToken) => Task.FromResult(Transport);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Delivers a pushed message to every handler registered for the method, as the server would.</summary>
    public void Deliver<TMessage>(string method, TMessage message)
    {
        List<Action<TMessage>> targets;

        lock (_gate)
        {
            targets = [.. _handlers.Where(entry => entry.Method == method).Select(entry => (Action<TMessage>)entry.Handler)];
        }

        foreach (var target in targets)
        {
            target(message);
        }
    }

    /// <summary>The connection drops and SignalR starts retrying. Everything joined is forgotten by the server.</summary>
    public void Drop()
    {
        lock (_gate)
        {
            _connected = false;
            _subscribed.Clear();
        }

        Reconnecting?.Invoke();
    }

    /// <summary>Automatic reconnection succeeds.</summary>
    public void Restore()
    {
        lock (_gate)
        {
            _connected = true;
        }

        Reconnected?.Invoke();
    }

    /// <summary>The connection closes for good.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _connected = false;
            _subscribed.Clear();
        }

        Closed?.Invoke();
    }

    private sealed class Registration(FakeRealtimeHub hub, (string Method, Delegate Handler) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (hub._gate)
            {
                hub._handlers.Remove(entry);
            }
        }
    }
}

/// <summary>
/// Snapshots that a test controls: what they contain, whether they fail, and when the response is delivered.
/// </summary>
/// <remarks>
/// <b>The content is fixed when the call starts, and the response can be held back.</b> That models a real
/// snapshot exactly: the server reads its state at the moment the request is handled, and the response reaches the
/// client some time later, during which pushes keep arriving.
/// </remarks>
public sealed class FakeSnapshots : IRealtimeSnapshots
{
    private TaskCompletionSource? _trustGate;
    private TaskCompletionSource? _alertGate;
    private int _trustCalls;
    private int _alertCalls;
    private int _commandCalls;
    private int _pipelineCalls;
    private int _streamCalls;

    public Func<TrustSnapshot> Trust { get; set; } = () => new TrustSnapshot(0, ClientRealtimeKit.Summary(), []);

    public Func<AlertSnapshot> Alerts { get; set; } =
        () => new AlertSnapshot(0, [], new AlertStats(0, 0, 0, 0, 0), []);

    public Func<CommandSnapshot> Commands { get; set; } = () => new CommandSnapshot(0, []);

    public Func<PipelineUpdate> Pipeline { get; set; } =
        () => new PipelineUpdate(0, new PipelineStats([], 10, 0, DateTimeOffset.UnixEpoch));

    public Func<StreamSnapshot> Stream { get; set; } = () => new StreamSnapshot(0, []);

    public Func<SuggestionsUpdate> Suggestions { get; set; } =
        () => new SuggestionsUpdate(0, [], [], [], ClientRealtimeKit.Start);

    private TaskCompletionSource? _suggestionGate;
    private int _suggestionCalls;

    public int SuggestionCalls => Volatile.Read(ref _suggestionCalls);

    /// <summary>The next suggestions snapshot's response is held until the returned action is called.</summary>
    public Action HoldNextSuggestions()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _suggestionGate = gate;

        return () => gate.TrySetResult();
    }

    public async Task<ApiResult<SuggestionsUpdate>> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _suggestionCalls);

        var content = Suggestions();
        var gate = Interlocked.Exchange(ref _suggestionGate, null);

        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return ApiResult<SuggestionsUpdate>.Success(content);
    }

    public volatile bool FailTrust;
    public volatile bool FailAlerts;

    public int TrustCalls => Volatile.Read(ref _trustCalls);

    public int AlertCalls => Volatile.Read(ref _alertCalls);

    public int CommandCalls => Volatile.Read(ref _commandCalls);

    public int PipelineCalls => Volatile.Read(ref _pipelineCalls);

    public int StreamCalls => Volatile.Read(ref _streamCalls);

    /// <summary>The next trust snapshot's response is held until the returned action is called.</summary>
    public Action HoldNextTrust()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _trustGate = gate;

        return () => gate.TrySetResult();
    }

    /// <summary>The next alert snapshot's response is held until the returned action is called.</summary>
    public Action HoldNextAlerts()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _alertGate = gate;

        return () => gate.TrySetResult();
    }

    public async Task<ApiResult<TrustSnapshot>> GetTrustAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _trustCalls);

        if (FailTrust)
        {
            return ApiResult<TrustSnapshot>.Failure(ApiProblem.Unreachable());
        }

        var content = Trust();
        var gate = Interlocked.Exchange(ref _trustGate, null);

        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return ApiResult<TrustSnapshot>.Success(content);
    }

    public async Task<ApiResult<AlertSnapshot>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _alertCalls);

        if (FailAlerts)
        {
            return ApiResult<AlertSnapshot>.Failure(ApiProblem.Unreachable());
        }

        var content = Alerts();
        var gate = Interlocked.Exchange(ref _alertGate, null);

        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return ApiResult<AlertSnapshot>.Success(content);
    }

    public Task<ApiResult<CommandSnapshot>> GetCommandsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _commandCalls);

        return Task.FromResult(ApiResult<CommandSnapshot>.Success(Commands()));
    }

    public Task<ApiResult<PipelineUpdate>> GetPipelineAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pipelineCalls);

        return Task.FromResult(ApiResult<PipelineUpdate>.Success(Pipeline()));
    }

    public Task<ApiResult<StreamSnapshot>> GetStreamAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _streamCalls);

        return Task.FromResult(ApiResult<StreamSnapshot>.Success(Stream()));
    }

    public Func<string, DeviceSnapshot> Device { get; set; } = mac => new DeviceSnapshot(0, mac, []);

    public int DeviceCalls => Volatile.Read(ref _deviceCalls);

    private int _deviceCalls;

    public Task<ApiResult<DeviceSnapshot>> GetDeviceAsync(string macAddress, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _deviceCalls);

        return Task.FromResult(ApiResult<DeviceSnapshot>.Success(Device(macAddress)));
    }
}

/// <summary>An HTTP handler that answers the polling feed's two trust requests and counts them.</summary>
public sealed class StubTrustHandler : HttpMessageHandler
{
    private int _requests;

    public Func<IReadOnlyList<NodeTrustSnapshot>> Nodes { get; set; } = () => [];

    public volatile bool Fail;

    public int Requests => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);

        if (Fail)
        {
            throw new HttpRequestException("The API could not be reached.");
        }

        var path = request.RequestUri!.AbsolutePath;
        var nodes = Nodes();

        object body = path.EndsWith("/summary", StringComparison.Ordinal) ? ClientRealtimeKit.Summary(nodes) : nodes;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>Everything a client test needs, built the way the app builds it.</summary>
public sealed class ClientRig : IAsyncDisposable
{
    private ClientRig()
    {
    }

    public FakeRealtimeHub Hub { get; } = new();

    public FakeSnapshots Snapshots { get; } = new();

    public StubTrustHandler Http { get; } = new();

    public RealtimeClientOptions Options { get; } = new()
    {
        // Short, so a test that waits for a retry or a fallback waits for milliseconds, not for the production
        // cadence. The behaviour is identical; only the clock is faster.
        UpgradeRetryInterval = TimeSpan.FromMilliseconds(40),
        ReconnectingFallbackAfter = TimeSpan.FromMilliseconds(150),
        PollInterval = TimeSpan.FromMilliseconds(40),
        SnapshotRetryInterval = TimeSpan.FromMilliseconds(40),
    };

    public AppState State { get; } = new();

    public ApiConnection Api { get; private set; } = null!;

    public RealtimeConnection Connection { get; private set; } = null!;

    public SignalRTrustFeed Trust { get; private set; } = null!;

    public AlertFeed Alerts { get; private set; } = null!;

    public CommandFeed Commands { get; private set; } = null!;

    public TelemetryStream Stream { get; private set; } = null!;

    public PipelineFeed Pipeline { get; private set; } = null!;

    public SuggestionFeed Suggestions { get; private set; } = null!;

    public PollingTrustFeed Polling { get; private set; } = null!;

    /// <summary>A rig whose hub is reachable unless told otherwise.</summary>
    public static ClientRig Create(Action<ClientRig>? configure = null)
    {
        var rig = new ClientRig();
        configure?.Invoke(rig);

        var http = new HttpClient(rig.Http) { BaseAddress = new Uri("http://stub/") };
        var api = new SmartXApiClient(http);
        rig.Api = new ApiConnection(api);

        // The polling feed reads its interval, in whole seconds, from configuration. One second is the shortest.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TrustFeed:PollIntervalSeconds"] = "1" })
            .Build();

        rig.Connection = new RealtimeConnection(rig.Hub, TimeProvider.System, rig.Options);
        rig.Polling = new PollingTrustFeed(api, rig.Api, rig.State, TimeProvider.System, configuration);
        rig.Trust = new SignalRTrustFeed(rig.Connection, rig.Snapshots, rig.Polling, rig.State, rig.Api, TimeProvider.System, rig.Options);
        rig.Alerts = new AlertFeed(rig.Connection, rig.Snapshots, TimeProvider.System, rig.Options);
        rig.Commands = new CommandFeed(rig.Connection, rig.Snapshots, TimeProvider.System, rig.Options);
        rig.Stream = new TelemetryStream(rig.Connection, rig.Snapshots, TimeProvider.System, rig.Options);
        rig.Pipeline = new PipelineFeed(rig.Connection, rig.Snapshots, TimeProvider.System, rig.Options);
        rig.Suggestions = new SuggestionFeed(rig.Connection, rig.Snapshots, TimeProvider.System, rig.Options);

        return rig;
    }

    /// <summary>Waits until a condition holds. The timeout guards a hang; no result depends on how long it took.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public Task WaitForStateAsync(RealtimeState state) =>
        UntilAsync(() => Connection.State == state, $"the connection to be {state} (it is {Connection.State})");

    public async ValueTask DisposeAsync()
    {
        await Trust.DisposeAsync();
        await Alerts.DisposeAsync();
        await Commands.DisposeAsync();
        await Stream.DisposeAsync();
        await Pipeline.DisposeAsync();
        await Suggestions.DisposeAsync();
        await Polling.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

/// <summary>Builders for the messages client tests send.</summary>
public static class ClientRealtimeKit
{
    public static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public static NodeTrustSnapshot Node(string mac, TrustBand band = TrustBand.Nominal, double score = 0.9, string reason = "ok") =>
        new(mac, score, band, reason, Start, Start, Start, 3, 0, []);

    public static TrustSummary Summary(IReadOnlyList<NodeTrustSnapshot>? nodes = null) =>
        new(0.9, 0.9, null, nodes?.Count ?? 0, nodes?.Count(n => n.Band == TrustBand.Nominal) ?? 0, 0, 0, nodes?.Count(n => n.Band == TrustBand.Fault) ?? 0);

    public static TrustSnapshot TrustSnapshot(long sequence, params NodeTrustSnapshot[] nodes) =>
        new(sequence, Summary(nodes), nodes);

    public static TrustUpdate TrustUpdate(long sequence, params NodeTrustSnapshot[] nodes) =>
        new(sequence, Summary(nodes), nodes);

    public static ActiveAlert Alert(string mac, AlertKind kind, long suppressed = 0) =>
        new(new AlertKey(mac, kind), "Room", Start, suppressed, false, null);

    public static ReadingPush Reading(string mac, double value, int secondsAfterStart = 0) =>
        new(mac, Start.AddSeconds(secondsAfterStart), value, "W", false, Start.AddSeconds(secondsAfterStart));
}
