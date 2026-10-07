using System.Collections.Concurrent;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using SmartX.Client.CommandStream;
using SmartX.Client.Realtime;
using SmartX.Client.Services;
using SmartX.Client.State;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// An operator API that answers exactly what a test says and records what it was asked.
/// </summary>
/// <remarks>
/// The components under test never touch <c>HttpClient</c>; they go through <see cref="IOperatorApi"/>, so a test hands them
/// this instead. Every call is counted, which is how "rapid typing is one request" and "nothing is sent before the operator
/// confirms" are checked: by what the API was actually asked, not by what the component says it did.
/// </remarks>
public sealed class FakeOperatorApi : IOperatorApi
{
    private readonly ConcurrentQueue<DeviceSearchFilters> _searches = new();
    private readonly ConcurrentQueue<(string Mac, CommandAction Action, double? Level)> _commands = new();
    private readonly ConcurrentQueue<(string Zone, CommandAction Action)> _zonePreviews = new();
    private readonly ConcurrentQueue<(string Zone, CommandAction Action)> _zoneIssues = new();
    private readonly ConcurrentQueue<string> _selections = new();
    private int _undoCalls;
    private int _timelineCalls;

    public Func<DeviceSearchFilters, DeviceSearchResponse> Search { get; set; } = filters => new DeviceSearchResponse(filters, 0, []);

    public Func<string, SensorTimeline> Timeline { get; set; } = StreamKit.EmptyTimeline;

    public Func<string, CommandAction, double?, Command> Issue { get; set; } =
        (mac, action, level) => StreamKit.Cmd(mac, ActuatorKind.Valve, action, CommandState.Pending);

    public Func<string, CommandAction, double?, ZonePreview> Preview { get; set; } =
        (zone, action, level) => new ZonePreview(zone, zone, action, level, []);

    public Func<string, CommandAction, double?, CompositeCommandResult> IssueZone { get; set; } =
        (zone, action, level) => new CompositeCommandResult(Guid.NewGuid(), zone, action, [], CompositeStatus.InProgress);

    public Func<IReadOnlyList<UndoStackEntry>> Stack { get; set; } = () => [];

    public Func<UndoResult> Undo { get; set; } = () => new UndoResult(UndoOutcome.NothingToUndo, "Nothing to undo.", null, []);

    public IReadOnlyList<DeviceSearchFilters> Searched => [.. _searches];

    public IReadOnlyList<(string Mac, CommandAction Action, double? Level)> CommandsIssued => [.. _commands];

    public IReadOnlyList<(string Zone, CommandAction Action)> ZonePreviews => [.. _zonePreviews];

    public IReadOnlyList<(string Zone, CommandAction Action)> ZoneIssues => [.. _zoneIssues];

    public IReadOnlyList<string> Selections => [.. _selections];

    public int UndoCalls => Volatile.Read(ref _undoCalls);

    public int TimelineCalls => Volatile.Read(ref _timelineCalls);

    public Task<ApiResult<DeviceSearchResponse>> SearchDevicesAsync(DeviceSearchFilters filters, CancellationToken cancellationToken = default)
    {
        _searches.Enqueue(filters);

        return Task.FromResult(ApiResult<DeviceSearchResponse>.Success(Search(filters)));
    }

    public Task<ApiResult<SensorTimeline>> GetTimelineAsync(
        string macAddress, DateTimeOffset from, DateTimeOffset to, int maxPoints, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _timelineCalls);

        return Task.FromResult(ApiResult<SensorTimeline>.Success(Timeline(macAddress)));
    }

    public Task<ApiResult<Command>> IssueCommandAsync(string macAddress, CommandAction action, double? level, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue((macAddress, action, level));

        return Task.FromResult(ApiResult<Command>.Success(Issue(macAddress, action, level)));
    }

    public Task<ApiResult<ZonePreview>> PreviewZoneAsync(string zoneId, CommandAction action, double? level, CancellationToken cancellationToken = default)
    {
        _zonePreviews.Enqueue((zoneId, action));

        return Task.FromResult(ApiResult<ZonePreview>.Success(Preview(zoneId, action, level)));
    }

    public Task<ApiResult<CompositeCommandResult>> IssueZoneCommandAsync(
        string zoneId, CommandAction action, double? level, CancellationToken cancellationToken = default)
    {
        _zoneIssues.Enqueue((zoneId, action));

        return Task.FromResult(ApiResult<CompositeCommandResult>.Success(IssueZone(zoneId, action, level)));
    }

    public Task<ApiResult<IReadOnlyList<UndoStackEntry>>> GetUndoStackAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<UndoStackEntry>>.Success(Stack()));

    public Task<ApiResult<UndoResult>> UndoAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _undoCalls);

        return Task.FromResult(ApiResult<UndoResult>.Success(Undo()));
    }

    public Task<ApiResult<CommandLimits>> GetCommandLimitsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<CommandLimits>.Success(new CommandLimits(20, 30, 60)));

    public Task<ApiResult<ActiveAlert>> AcknowledgeAlertAsync(AlertKey key, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<ActiveAlert>.Success(StreamKit.Alert(key.Mac, key.Kind)));

    public Task<ApiResult<DeviceDesiredState>> SetScenarioAsync(string macAddress, SimulationScenario scenario, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<DeviceDesiredState>.Failure(new ApiProblem(404, "Not used by this test")));

    public Task<ApiResult<IReadOnlyList<ActivityEntry>>> GetActivityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<ActivityEntry>>.Success([]));

    public Task<ApiProblem?> RecordSelectionAsync(string macAddress, CancellationToken cancellationToken = default)
    {
        _selections.Enqueue(macAddress);

        return Task.FromResult<ApiProblem?>(null);
    }
}

/// <summary>A chart width that does not depend on a browser.</summary>
public sealed class FixedChartSizing(int width = 480) : IChartSizing
{
    public Task<int> WidthAsync() => Task.FromResult(width);
}

/// <summary>Builders for the messages and records the Command Stream tests use.</summary>
public static class StreamKit
{
    public const string Valve = "B8:27:EB:10:00:19";
    public const string Pump = "B8:27:EB:10:00:21";
    public const string Meter = "B8:27:EB:10:00:0F";

    public static readonly DateTimeOffset T0 = ClientRealtimeKit.Start;

    public static Command Cmd(
        string mac,
        ActuatorKind kind,
        CommandAction action,
        CommandState state,
        Guid? id = null,
        bool observed = true,
        Guid? composite = null,
        string? failure = null) =>
        new(id ?? Guid.NewGuid(), mac, kind, action, null, CommandActionRules.TargetValue(action, null), T0, state, 1, observed,
            CompositeId: composite, FailureReason: failure);

    public static ActiveAlert Alert(string mac, AlertKind kind, long suppressed = 0) => ClientRealtimeKit.Alert(mac, kind, suppressed);

    public static PriorityAlert Critical(long sequence, string mac = Meter, double value = 250, int secondsAfterStart = 0) =>
        new(sequence, mac, "power spike", 0, value, "kW", T0.AddSeconds(secondsAfterStart), T0.AddSeconds(secondsAfterStart));

    public static ReadingsBatch Batch(long sequence, params ReadingPush[] readings) => new(sequence, readings, 0);

    public static ReadingPush Reading(string mac, double value, int second) => ClientRealtimeKit.Reading(mac, value, second);

    public static SensorRegistration Actuator(string mac, string room, ActuatorKind kind) =>
        new(mac, new DeploymentLocation(room, "zone-1", "N"), SensorCategory.Actuator, "state", new ValueRange(0, 1),
            TelemetryPayloadKind.State, null, kind);

    public static SensorTimeline EmptyTimeline(string mac) =>
        new(mac, "kW", TimelineShape.Buckets, T0, T0.AddHours(1), 60, 0, [], [], []);

    /// <summary>
    /// Ten one-minute buckets of about 20, with a spike of 900 in the sixth that the average alone would hide, and one event
    /// of each kind.
    /// </summary>
    public static SensorTimeline Spike(string mac = Meter) =>
        new(
            mac,
            "kW",
            TimelineShape.Buckets,
            T0,
            T0.AddMinutes(10),
            60,
            600,
            [
                .. Enumerable.Range(0, 10).Select(minute =>
                {
                    var spike = minute == 5;

                    return new TimelineBucket(T0.AddMinutes(minute), T0.AddMinutes(minute + 1), 18, spike ? 900 : 22, spike ? 34 : 20, 60);
                }),
            ],
            [],
            [
                new TimelineMarker(T0.AddMinutes(1), HistoryKind.Command, "ValveClose issued", null),
                new TimelineMarker(T0.AddMinutes(5), HistoryKind.AlertRaised, "OutOfRange raised", "900 kW"),
                new TimelineMarker(T0.AddMinutes(7), HistoryKind.AlertCleared, "OutOfRange cleared", null),
                new TimelineMarker(T0.AddMinutes(8), HistoryKind.TrustTransition, "Nominal to Degrading", null),
            ]);
}

/// <summary>
/// A bUnit context wired the way the app is: one connection, the real feeds on it, the real Command Stream services, and
/// only the two edges replaced, the API (by <see cref="FakeOperatorApi"/>) and the clock (by a fake a test advances).
/// </summary>
/// <remarks>
/// <b>What is real and what is not.</b> The feeds, <see cref="LiveStream"/>, <see cref="DeviceSearchService"/> and
/// <see cref="AppState"/> are the shipped classes, so a test of pause or pin or debounce exercises the shipped logic and
/// the component together. The hub is the fake hub from <see cref="ClientRig"/>, which lets a test deliver a push at a
/// chosen moment the way the server would.
/// </remarks>
public abstract class StreamComponentTests : IAsyncLifetime
{
    // Composed and not inherited. xUnit disposes a test class through IDisposable when it has one, and a BunitContext's
    // synchronous Dispose throws for MudBlazor's services, which are async-only. Owning the context here means it is only
    // ever disposed through DisposeAsync.
    private readonly BunitContext _context = new();

    protected StreamComponentTests(int streamCapacity = 500)
    {
        Rig = ClientRig.Create();
        Time = new FakeTimeProvider(StreamKit.T0);
        Api = new FakeOperatorApi();
        Options = new CommandStreamOptions { StreamCapacity = streamCapacity };

        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        var Services = _context.Services;

        Services.AddMudServices();
        Services.AddSingleton(Rig.State);
        Services.AddSingleton<TimeProvider>(Time);
        Services.AddSingleton(Options);
        Services.AddSingleton<IOperatorApi>(Api);
        Services.AddSingleton<ITrustFeed>(Rig.Trust);
        Services.AddSingleton<IAlertFeed>(Rig.Alerts);
        Services.AddSingleton<ICommandFeed>(Rig.Commands);
        Services.AddSingleton<ITelemetryStream>(Rig.Stream);
        Services.AddSingleton<IPipelineFeed>(Rig.Pipeline);
        Services.AddSingleton<IChartSizing>(new FixedChartSizing());
        Services.AddSingleton<IDeviceFeedFactory>(new DeviceFeedFactory(Rig.Connection, Rig.Snapshots, TimeProvider.System, Rig.Options));

        Search = new DeviceSearchService(Api, Rig.State, Time, Options);
        Stream = new LiveStream(Rig.Stream, Rig.Alerts, Rig.State, Time, Options);
        Catalog = new SensorService(new SmartXApiClient(new HttpClient(Rig.Http) { BaseAddress = new Uri("http://stub/") }), Rig.Api);

        Services.AddSingleton(Search);
        Services.AddSingleton(Stream);
        Services.AddSingleton(Catalog);
    }

    protected ClientRig Rig { get; }

    protected IRenderedComponent<TComponent> Render<TComponent>(Action<ComponentParameterCollectionBuilder<TComponent>>? parameters = null)
        where TComponent : Microsoft.AspNetCore.Components.IComponent => _context.Render(parameters);

    protected Task DisposeComponentsAsync() => _context.DisposeComponentsAsync();

    protected FakeTimeProvider Time { get; }

    protected FakeOperatorApi Api { get; }

    protected CommandStreamOptions Options { get; }

    protected DeviceSearchService Search { get; }

    protected LiveStream Stream { get; }

    protected SensorService Catalog { get; }

    protected AppState State => Rig.State;

    /// <summary>The components that open popovers (every select) need the provider the app layout supplies.</summary>
    protected void RenderPopoverHost() => Render<MudBlazor.MudPopoverProvider>();


    /// <summary>
    /// Starts the feeds a component will start and waits for their snapshots, before the component is drawn.
    /// </summary>
    /// <remarks>
    /// A snapshot arriving after a component is drawn redraws it, and a test that has just found a button then clicks a handler
    /// that no longer exists. That is a race in the test and not in the component, so the test takes the snapshots out of the
    /// way first. The component's own start is then a no-op, because starts are reference counted.
    /// </remarks>
    protected async Task PreloadFeedsAsync()
    {
        await Rig.Trust.StartAsync();
        await Rig.Alerts.StartAsync();
        await Rig.Commands.StartAsync();

        await ClientRig.UntilAsync(
            () => Rig.Trust.Current.IsLoaded && Rig.Alerts.Current.IsLoaded && Rig.Commands.Current.IsLoaded,
            "the feeds' snapshots");
        await ListeningAsync(Rig, RealtimeMethods.CommandChanged);
    }

    /// <summary>
    /// Waits until a component has stopped redrawing for a moment, so what a test finds is what is on the screen.
    /// </summary>
    /// <remarks>
    /// Used before the first interaction, to close the window between finding an element and clicking it. It only ever
    /// delays a test; no assertion depends on how long it waited.
    /// </remarks>
    protected static async Task SettledAsync<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        var last = -1;
        var stableSince = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var now = cut.RenderCount;

            if (now != last)
            {
                last = now;
                stableSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(100))
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Waits for the hub to have handlers for a method, which is when a feed has started listening.</summary>
    protected static Task ListeningAsync(ClientRig rig, string method) =>
        ClientRig.UntilAsync(() => rig.Hub.HandlerCount(method) > 0, $"a listener for {method}");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await Stream.DisposeAsync();
        await Rig.DisposeAsync();
    }
}
