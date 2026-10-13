using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.Ingestion;
using SmartX.Api.Realtime;
using SmartX.Api.Trust;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// The API in memory with a real hub, and real <see cref="HubConnection"/> clients connected to it.
/// </summary>
/// <remarks>
/// The timers are set to an hour, so nothing flushes on its own. A test that wants a batched topic to flush calls
/// the broadcaster's flush itself, which makes "these arrived as one message" a deterministic statement and not a
/// race against a 250 ms clock.
/// <para>
/// Clients use long polling: the in-memory test server speaks it without extra plumbing, and it is the same hub,
/// the same groups and the same messages as over WebSockets. The negotiated transport is a property of the
/// network path, and is verified separately against the real nginx in the Docker stack.
/// </para>
/// </remarks>
internal sealed class RealtimeHost : IAsyncDisposable
{
    private readonly List<HubConnection> _connections = [];

    private RealtimeHost(WebApplicationFactory<Program> factory) => Factory = factory;

    public WebApplicationFactory<Program> Factory { get; }

    public IServiceProvider Services => Factory.Services;

    public RealtimeBroadcaster Broadcaster => Services.GetRequiredService<RealtimeBroadcaster>();

    public AlertTracker Alerts => Services.GetRequiredService<AlertTracker>();

    public ITrustEngine Trust => Services.GetRequiredService<ITrustEngine>();

    public CommandService Commands => Services.GetRequiredService<CommandService>();

    public ProcessedReadings Readings => Services.GetRequiredService<ProcessedReadings>();

    public HttpClient Http { get; private set; } = null!;

    public static RealtimeHost Create(Dictionary<string, string?>? extra = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["Realtime:FlushInterval"] = "01:00:00",
            ["Realtime:PipelineInterval"] = "01:00:00",
            ["Trust:HeartbeatCheckInterval"] = "01:00:00",
        };

        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));

        var host = new RealtimeHost(factory);

        // Creating the client starts the host, and with it the broadcaster and the hub.
        host.Http = factory.CreateClient();

        return host;
    }

    /// <summary>A new client, connected, recording every message it is sent.</summary>
    public async Task<TestClient> ConnectAsync(params string[] topics)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, RealtimeTopics.HubPath), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        _connections.Add(connection);

        var client = new TestClient(connection);
        await connection.StartAsync();

        foreach (var topic in topics)
        {
            await connection.InvokeAsync(RealtimeMethods.Subscribe, topic);
        }

        return client;
    }

    public async Task RegisterSensorAsync(string mac)
    {
        var response = await Http.PostAsync("/api/sensors", Thermometer10To30(mac));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }

    public static NumericPacket<int> Reading(string mac, int value) =>
        new(mac, value, DateTimeOffset.UtcNow, SensorCategory.PowerConsumption, "W");

    public void Publish(string mac, int value, CriticalAlert? alert = null) =>
        Readings.Publish(new ProcessedReading(Reading(mac, value), 0, DateTimeOffset.UtcNow, alert));

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        Http.Dispose();
        await Factory.DisposeAsync();
    }

    /// <summary>A hub client that keeps every message by method name.</summary>
    internal sealed class TestClient
    {
        private readonly ConcurrentQueue<(string Method, object Message)> _received = new();

        public TestClient(HubConnection connection)
        {
            Connection = connection;

            connection.On<TrustUpdate>(RealtimeMethods.TrustUpdated, message => _received.Enqueue((RealtimeMethods.TrustUpdated, message)));
            connection.On<AlertUpdate>(RealtimeMethods.AlertsUpdated, message => _received.Enqueue((RealtimeMethods.AlertsUpdated, message)));
            connection.On<PriorityAlert>(RealtimeMethods.PriorityAlerted, message => _received.Enqueue((RealtimeMethods.PriorityAlerted, message)));
            connection.On<CommandUpdate>(RealtimeMethods.CommandChanged, message => _received.Enqueue((RealtimeMethods.CommandChanged, message)));
            connection.On<PipelineUpdate>(RealtimeMethods.PipelineUpdated, message => _received.Enqueue((RealtimeMethods.PipelineUpdated, message)));
            connection.On<ReadingsBatch>(RealtimeMethods.ReadingsArrived, message => _received.Enqueue((RealtimeMethods.ReadingsArrived, message)));
            connection.On<SuggestionsUpdate>(RealtimeMethods.SuggestionsUpdated, message => _received.Enqueue((RealtimeMethods.SuggestionsUpdated, message)));
        }

        public HubConnection Connection { get; }

        public IReadOnlyList<T> Messages<T>(string method) =>
            [.. _received.Where(entry => entry.Method == method).Select(entry => (T)entry.Message)];

        public int Count => _received.Count;

        /// <summary>Waits until a method has been called at least <paramref name="count"/> times.</summary>
        /// <remarks>The timeout only guards against a hang. No test result depends on how long a message took.</remarks>
        public async Task<IReadOnlyList<T>> WaitForAsync<T>(string method, int count = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);

            while (DateTime.UtcNow < deadline)
            {
                var messages = Messages<T>(method);

                if (messages.Count >= count)
                {
                    return messages;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Expected {count} {method} message(s); received {Messages<T>(method).Count}.");
        }

        /// <summary>
        /// Gives anything still in flight a moment to arrive, so that "nothing came" can be asserted.
        /// </summary>
        /// <remarks>
        /// The one place a test waits for time to pass, because absence cannot be waited for any other way. It
        /// is only ever used after the messages that <i>should</i> have arrived have been seen, so the wait is on
        /// top of a connection already known to be delivering.
        /// </remarks>
        public static Task SettleAsync() => Task.Delay(300);
    }
}
