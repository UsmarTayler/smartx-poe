using System.Net.Http.Json;
using SmartX.Client.Services;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// The cost of opening the telemetry page, measured against the real API.
/// </summary>
/// <remarks>
/// The same method as <see cref="TrustFeedRequestTests"/>: the client talks to an in-process API through a handler
/// that counts every request it sends, so an extra request added anywhere on the load path fails the test.
/// </remarks>
public class TelemetryLoadRequestTests
{
    private sealed class CountingHandler : DelegatingHandler
    {
        private int _count;

        public int Count => _count;

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);

            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(48)]
    public async Task Opening_the_page_is_one_request_however_many_sensors_there_are(int sensorCount)
    {
        using var api = New();
        var setup = api.CreateClient();
        List<SensorRegistration> sensors = [];

        for (var sensor = 0; sensor < sensorCount; sensor++)
        {
            var mac = $"AA:BB:CC:00:20:{sensor:X2}";
            (await setup.PostAsync("/api/sensors", Thermometer10To30(mac))).EnsureSuccessStatusCode();
            await setup.PostAsync("/api/telemetry", Reading(mac, "21.5"));
            await setup.PostAsync("/api/telemetry", Reading(mac, "22.5"));

            sensors.Add(new SensorRegistration(
                mac, new DeploymentLocation("Server Room 2", "sub-zone-b", "NODE-1"), SensorCategory.Environmental, "°C",
                new ValueRange(10, 30), TelemetryPayloadKind.Real));
        }

        await DrainAsync(setup, sensorCount * 2);

        var counter = new CountingHandler();
        var client = new SmartXApiClient(api.CreateDefaultClient(counter));
        var service = new TelemetryService(client, new ApiConnection(client), TimeProvider.System);

        await service.LoadAllAsync(sensors);

        // One request, not one per sensor.
        Assert.Equal(1, counter.Count);
        Assert.Equal(["/api/telemetry/recent"], counter.Paths);

        // And one request is only a fix if every card still has its data.
        Assert.All(sensors, sensor =>
        {
            var state = service.For(sensor.MacAddress);

            Assert.True(state.IsLoaded);
            Assert.Equal(2, state.Recent.Count);
            Assert.True(state.Recent[0].Timestamp <= state.Recent[1].Timestamp);
        });
    }

    [Fact]
    public async Task The_batched_endpoint_has_an_entry_for_every_registered_sensor_including_ones_with_no_readings()
    {
        using var api = New();
        var client = api.CreateClient();
        await client.PostAsync("/api/sensors", Thermometer10To30("AA:BB:CC:00:21:01"));
        await client.PostAsync("/api/sensors", Thermometer10To30("AA:BB:CC:00:21:02"));
        await client.PostAsync("/api/telemetry", Reading("AA:BB:CC:00:21:01", "20"));
        await DrainAsync(client, 1);

        var recent = await client.GetFromJsonAsync<Dictionary<string, List<ITelemetryPacket>>>("/api/telemetry/recent");

        Assert.Equal(["AA:BB:CC:00:21:01", "AA:BB:CC:00:21:02"], recent!.Keys.Order());
        Assert.Single(recent["AA:BB:CC:00:21:01"]);
        Assert.Empty(recent["AA:BB:CC:00:21:02"]);
    }

    [Fact]
    public async Task The_batched_endpoint_on_an_empty_gateway_is_empty_and_the_single_sensor_endpoint_still_works()
    {
        using var api = New();
        var client = api.CreateClient();

        var empty = await client.GetFromJsonAsync<Dictionary<string, List<ITelemetryPacket>>>("/api/telemetry/recent");
        Assert.Empty(empty!);

        await client.PostAsync("/api/sensors", Thermometer10To30());
        var single = await client.GetAsync($"/api/sensors/{Thermometer}/telemetry");

        Assert.Equal(System.Net.HttpStatusCode.OK, single.StatusCode);
    }
}
