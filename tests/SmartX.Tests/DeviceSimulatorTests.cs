using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartX.DeviceSimulator;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the device simulator against a real API, over HTTP.
/// </summary>
/// <remarks>
/// The simulator's HTTP client is pointed at the API's in-memory test server, so every request below goes
/// through routing, validation and the queues exactly as a device's would. Nothing here calls an API service
/// directly, which is the property the simulator exists to have.
/// </remarks>
public class DeviceSimulatorTests
{
    /// <summary>An API serving the larger hierarchy the demo fleet is deployed into.</summary>
    private static WebApplicationFactory<Program> DemoApi() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Zones:Hierarchy"] = "Demo" })));

    private static GatewayClient Gateway(HttpClient http) => new(http, NullLogger<GatewayClient>.Instance);

    [Fact]
    public async Task Every_device_in_the_fleet_registers_against_the_demo_hierarchy()
    {
        // The fleet is validated by the API's own zone rules, over HTTP, exactly as a real device's
        // registration would be. This is what stops the fleet drifting away from the zones it lives in.
        using var api = DemoApi();
        var gateway = Gateway(api.CreateClient());

        foreach (var device in DemoDevices.All)
        {
            Assert.True(await gateway.RegisterAsync(device.Registration, CancellationToken.None), $"{device.MacAddress} was refused.");
        }

        var sensors = await api.CreateClient().GetFromJsonAsync<List<SensorRegistration>>("/api/sensors");

        Assert.Equal(DemoDevices.All.Count, sensors?.Count);
        Assert.Equal(Enum.GetValues<SensorCategory>(), sensors!.Select(sensor => sensor.Category).Distinct().Order());
        Assert.Equal(Enum.GetValues<TelemetryPayloadKind>(), sensors!.Select(sensor => sensor.PayloadKind).Distinct().Order());
    }

    [Fact]
    public async Task Registering_the_same_device_twice_is_a_success_both_times()
    {
        // Idempotent across restarts: a restarted simulator meets a gateway that already knows its devices,
        // and a 409 is the gateway confirming that, not a failure.
        using var api = DemoApi();
        var http = api.CreateClient();
        var gateway = Gateway(http);
        var device = DemoDevices.All[0];

        Assert.True(await gateway.RegisterAsync(device.Registration, CancellationToken.None));
        Assert.True(await gateway.RegisterAsync(device.Registration, CancellationToken.None));

        Assert.Single((await http.GetFromJsonAsync<List<SensorRegistration>>("/api/sensors"))!);
    }

    [Fact]
    public async Task A_registration_the_gateway_refuses_is_reported_as_a_failure()
    {
        // The default hierarchy has no sub-zone-c, so the gateway refuses it. That must not be swallowed the
        // way a 409 is.
        using var api = New();
        var gateway = Gateway(api.CreateClient());

        Assert.False(await gateway.RegisterAsync(DemoDevices.All[7].Registration, CancellationToken.None));
    }

    [Fact]
    public async Task One_nominal_reading_from_every_device_is_accepted_by_the_gateway()
    {
        // Proves the simulated readings are good telemetry: the right JSON type for each registration and
        // inside its range, judged by the API's real validation rather than by the simulator's own opinion.
        using var api = DemoApi();
        var http = api.CreateClient();
        var gateway = Gateway(http);
        var random = new Random(1);

        foreach (var definition in DemoDevices.All)
        {
            await gateway.RegisterAsync(definition.Registration, CancellationToken.None);
            var device = new SimulatedDevice(definition);

            var outcome = await gateway.SendReadingAsync(
                device.MacAddress, device.NextReading(SimulationScenario.Nominal, random), timestamp: null, CancellationToken.None);

            Assert.True(outcome == SendOutcome.Accepted, $"{device.MacAddress} ({definition.Registration.PayloadKind}) was {outcome}.");
        }

        await DrainAsync(http, expectedProcessed: DemoDevices.All.Count);
    }

    [Fact]
    public async Task The_type_violation_scenario_sends_readings_the_gateway_rejects_and_scores()
    {
        using var api = DemoApi();
        var http = api.CreateClient();
        var gateway = Gateway(http);
        var random = new Random(3);

        var definition = DemoDevices.All.First(device => device.Registration.PayloadKind == TelemetryPayloadKind.Integer);
        await gateway.RegisterAsync(definition.Registration, CancellationToken.None);
        var device = new SimulatedDevice(definition);

        var outcomes = new List<SendOutcome>();

        for (var i = 0; i < 60; i++)
        {
            outcomes.Add(await gateway.SendReadingAsync(
                device.MacAddress, device.NextReading(SimulationScenario.TypeViolation, random), timestamp: null, CancellationToken.None));
        }

        Assert.Contains(SendOutcome.Refused, outcomes);
        Assert.Contains(SendOutcome.Accepted, outcomes);

        var trust = await http.GetFromJsonAsync<NodeTrustSnapshot>($"/api/trust/{device.MacAddress}");
        Assert.True(trust?.RecentFailures > 0);
    }

    [Fact]
    public async Task The_critical_scenario_sends_valid_readings_that_land_in_the_critical_lane()
    {
        // The device does not label a reading critical. It sends an extreme value that is still inside its
        // range, and the gateway's thresholds decide. Both a moisture crash and a power spike must be caught.
        using var api = DemoApi();
        var http = api.CreateClient();
        var gateway = Gateway(http);
        var random = new Random(5);

        var criticalDevices = DemoDevices.All.Where(device => device.Scenario == SimulationScenario.Critical).ToList();
        Assert.Equal(2, criticalDevices.Count);

        var sent = 0;

        foreach (var definition in criticalDevices)
        {
            await gateway.RegisterAsync(definition.Registration, CancellationToken.None);
            var device = new SimulatedDevice(definition);

            for (var i = 0; i < 200; i++)
            {
                var outcome = await gateway.SendReadingAsync(
                    device.MacAddress, device.NextReading(SimulationScenario.Critical, random), timestamp: null, CancellationToken.None);

                Assert.Equal(SendOutcome.Accepted, outcome);
                sent++;
            }
        }

        await DrainAsync(http, expectedProcessed: sent);

        var stats = await http.GetFromJsonAsync<PipelineStats>("/api/pipeline/stats");
        var critical = stats!.Lanes.Single(lane => lane.Lane == "critical").Processed;

        // Roughly one reading in seven is an emergency: enough to see, not a flood.
        Assert.InRange(critical, 20, 100);
    }

    [Fact]
    public async Task A_desired_state_change_reaches_the_device_and_takes_effect()
    {
        using var api = DemoApi();
        var http = api.CreateClient();
        var gateway = Gateway(http);

        var definition = DemoDevices.All.First(device => device.Registration.PayloadKind == TelemetryPayloadKind.Real);
        await gateway.RegisterAsync(definition.Registration, CancellationToken.None);
        var device = new SimulatedDevice(definition);

        // Nothing asked of it yet: the device carries on as it is.
        Assert.Null(await gateway.GetDesiredStateAsync(device.MacAddress, CancellationToken.None));

        await http.PostAsync($"/api/simulation/{device.MacAddress}/scenario", Json("""{ "scenario": "Disconnect" }"""));

        var desired = await gateway.GetDesiredStateAsync(device.MacAddress, CancellationToken.None);

        Assert.NotNull(desired);
        Assert.True(device.TryApply(desired, DateTimeOffset.UtcNow));
        Assert.Equal(SimulationScenario.Disconnect, device.Scenario);
        Assert.Equal(desired.Version, device.AppliedVersion);
    }

    [Fact]
    public async Task A_device_refuses_an_instruction_it_cannot_carry_out_but_still_records_the_version()
    {
        // A door contact has no range to drift out of. It must decline rather than start sending nonsense,
        // and it must not ask for the same instruction again on every poll.
        using var api = DemoApi();
        var http = api.CreateClient();
        var gateway = Gateway(http);

        var definition = DemoDevices.All.First(device => device.Registration.PayloadKind == TelemetryPayloadKind.State);
        await gateway.RegisterAsync(definition.Registration, CancellationToken.None);
        var device = new SimulatedDevice(definition);
        var before = device.Scenario;

        await http.PostAsync($"/api/simulation/{device.MacAddress}/scenario", Json("""{ "scenario": "Drift" }"""));
        var desired = await gateway.GetDesiredStateAsync(device.MacAddress, CancellationToken.None);

        Assert.False(device.TryApply(desired!, DateTimeOffset.UtcNow));
        Assert.Equal(before, device.Scenario);
        Assert.Equal(desired!.Version, device.AppliedVersion);
    }

    [Fact]
    public async Task Reconnecting_a_disconnected_device_makes_it_report_at_once()
    {
        using var api = DemoApi();
        var gateway = Gateway(api.CreateClient());
        var device = new SimulatedDevice(DemoDevices.All.First(d => d.Scenario == SimulationScenario.Disconnect));
        await gateway.RegisterAsync(device.Registration, CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        device.NextDue = now.AddHours(1);

        device.TryApply(new DeviceDesiredState(device.MacAddress, SimulationScenario.Nominal, 1, now), now);

        Assert.Equal(now, device.NextDue);
    }

    [Fact]
    public async Task A_busy_gateway_is_reported_as_backpressure_not_as_a_refusal()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Ingestion:MaxQueueDepth"] = "1", ["Zones:Hierarchy"] = "Demo" }));

            builder.ConfigureServices(services => services.Remove(services.Single(service =>
                service.ImplementationType == typeof(SmartX.Api.Ingestion.TelemetryProcessor))));
        });

        var gateway = Gateway(api.CreateClient());
        var definition = DemoDevices.All[0];
        await gateway.RegisterAsync(definition.Registration, CancellationToken.None);
        var device = new SimulatedDevice(definition);
        var random = new Random(1);

        var first = await gateway.SendReadingAsync(device.MacAddress, device.NextReading(SimulationScenario.Nominal, random), null, CancellationToken.None);
        var second = await gateway.SendReadingAsync(device.MacAddress, device.NextReading(SimulationScenario.Nominal, random), null, CancellationToken.None);

        Assert.Equal(SendOutcome.Accepted, first);
        Assert.Equal(SendOutcome.Busy, second);
    }

    [Fact]
    public async Task A_timestamped_reading_is_stored_with_the_time_it_was_taken()
    {
        // Backfill depends on this: history posted after the fact must keep its own timestamps.
        using var api = DemoApi();
        var http = api.CreateClient();
        var gateway = Gateway(http);
        var definition = DemoDevices.All[0];
        await gateway.RegisterAsync(definition.Registration, CancellationToken.None);
        var device = new SimulatedDevice(definition);

        var taken = DateTimeOffset.UtcNow.AddMinutes(-9);
        await gateway.SendReadingAsync(device.MacAddress, device.NextReading(SimulationScenario.Nominal, new Random(1)), taken, CancellationToken.None);
        await DrainAsync(http, expectedProcessed: 1);

        var history = await http.GetFromJsonAsync<List<ITelemetryPacket>>($"/api/sensors/{device.MacAddress}/telemetry");

        Assert.Equal(taken, Assert.Single(history!).Timestamp);
    }

    [Fact]
    public async Task Waiting_for_the_gateway_succeeds_once_it_is_up()
    {
        using var api = DemoApi();
        var gateway = Gateway(api.CreateClient());

        Assert.True(await gateway.WaitForGatewayAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Waiting_for_a_gateway_that_never_comes_up_gives_up_instead_of_hanging()
    {
        // Nothing listens on port 1. The simulator must retry with backoff and then report failure, not
        // crash on the first refused connection and not wait for ever.
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1/"), Timeout = TimeSpan.FromSeconds(1) };
        var gateway = Gateway(http);

        var started = DateTimeOffset.UtcNow;
        var up = await gateway.WaitForGatewayAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(up);
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task The_fleet_starts_with_the_gateway_not_yet_up_and_registers_once_it_is()
    {
        // The Docker race in miniature: compose starts the simulator before the API is ready. The gateway
        // answers 503 for its first few requests, then comes up, and the fleet must not have given up.
        using var api = DemoApi();
        var real = api.CreateClient();
        var refusals = 0;

        var flaky = new HttpClient(new FlakyHandler(real, refusalsBeforeUp: 3, () => refusals++))
        {
            BaseAddress = real.BaseAddress,
        };

        var gateway = Gateway(flaky);

        Assert.True(await gateway.WaitForGatewayAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(3, refusals);
        Assert.True(await gateway.RegisterAsync(DemoDevices.All[0].Registration, CancellationToken.None));
    }

    [Fact]
    public async Task A_device_the_gateway_has_not_heard_of_is_reported_as_not_registered()
    {
        using var api = DemoApi();
        var gateway = Gateway(api.CreateClient());
        var device = new SimulatedDevice(DemoDevices.All[0]);

        var outcome = await gateway.SendReadingAsync(
            device.MacAddress, device.NextReading(SimulationScenario.Nominal, new Random(1)), timestamp: null, CancellationToken.None);

        Assert.Equal(SendOutcome.NotRegistered, outcome);
    }

    [Fact]
    public async Task A_gateway_restart_is_recovered_from_by_registering_the_whole_fleet_including_silent_devices()
    {
        // The gateway keeps its device list in memory, so a restart wipes it while the fleet is still running.
        // Swapping a fresh, empty API in under a running fleet is exactly that. Thirteen devices reach the
        // Disconnect device at index 10, which never sends a reading and so can never hear a 404 itself.
        using var before = DemoApi();
        using var after = DemoApi();
        var swappable = new SwappableHandler(before.CreateClient());
        var gateway = Gateway(new HttpClient(swappable) { BaseAddress = before.CreateClient().BaseAddress });

        var fleet = new DeviceFleet(
            gateway,
            Microsoft.Extensions.Options.Options.Create(new SimulatorOptions { DeviceCount = 13 }),
            TimeProvider.System,
            new NoopHostLifetime(),
            NullLogger<DeviceFleet>.Instance);

        await fleet.RegisterFleetForTestAsync();

        var silent = fleet.DevicesForTest.Single(device => device.Scenario == SimulationScenario.Disconnect);
        var reporter = fleet.DevicesForTest[0];
        var reading = reporter.NextReading(SimulationScenario.Nominal, new Random(1));

        Assert.True(await fleet.SendWithBackpressureAsync(reporter, reading, timestamp: null, CancellationToken.None));

        // The gateway "restarts": same address, no memory of anything.
        swappable.Inner = after.CreateClient();
        Assert.Empty((await swappable.Inner.GetFromJsonAsync<List<SensorRegistration>>("/api/sensors"))!);

        // One reporting device hears the 404, and that is enough to bring every device back.
        Assert.True(await fleet.SendWithBackpressureAsync(reporter, reading, timestamp: null, CancellationToken.None));

        var registered = (await swappable.Inner.GetFromJsonAsync<List<SensorRegistration>>("/api/sensors"))!
            .Select(sensor => sensor.MacAddress)
            .ToHashSet();

        Assert.Equal(13, registered.Count);
        Assert.Contains(silent.MacAddress, registered);
    }

    [Fact]
    public async Task Many_devices_hearing_the_same_404_cause_one_re_registration_not_one_each()
    {
        using var before = DemoApi();
        using var after = DemoApi();
        var swappable = new SwappableHandler(before.CreateClient());
        var counter = new CountingHandler(swappable);
        var gateway = Gateway(new HttpClient(counter) { BaseAddress = before.CreateClient().BaseAddress });

        var fleet = new DeviceFleet(
            gateway,
            Microsoft.Extensions.Options.Options.Create(new SimulatorOptions { DeviceCount = 13 }),
            TimeProvider.System,
            new NoopHostLifetime(),
            NullLogger<DeviceFleet>.Instance);

        await fleet.RegisterFleetForTestAsync();
        swappable.Inner = after.CreateClient();
        counter.Registrations = 0;

        var random = new Random(1);
        var senders = fleet.DevicesForTest
            .Where(device => device.Scenario != SimulationScenario.Disconnect)
            .Select(device => fleet.SendWithBackpressureAsync(
                device, device.NextReading(SimulationScenario.Nominal, random), timestamp: null, CancellationToken.None));

        await Task.WhenAll(senders);

        // Thirteen registrations in one sweep. Twelve devices each doing their own sweep would be 156.
        Assert.Equal(13, counter.Registrations);
    }

    [Fact]
    public async Task A_silent_device_can_be_switched_back_to_nominal_because_its_control_channel_keeps_polling()
    {
        // A Disconnect device stops sending telemetry, but the harness keeps polling its shadow. If it did
        // not, an operator could never bring a silent device back: nothing would ever read the instruction.
        using var api = DemoApi();
        var http = api.CreateClient();
        var gateway = Gateway(http);
        var fleet = new DeviceFleet(
            gateway,
            Microsoft.Extensions.Options.Options.Create(new SimulatorOptions { DeviceCount = 13 }),
            TimeProvider.System,
            new NoopHostLifetime(),
            NullLogger<DeviceFleet>.Instance);

        await fleet.RegisterFleetForTestAsync();
        var silent = fleet.DevicesForTest.Single(device => device.Scenario == SimulationScenario.Disconnect);
        var now = DateTimeOffset.UtcNow;

        // While silent: no telemetry.
        await fleet.TickDeviceForTestAsync(silent, now, CancellationToken.None);
        Assert.Empty((await http.GetFromJsonAsync<List<ITelemetryPacket>>($"/api/sensors/{silent.MacAddress}/telemetry"))!);

        // The operator brings it back.
        await http.PostAsync($"/api/simulation/{silent.MacAddress}/scenario", Json("""{ "scenario": "Nominal" }"""));

        // Its next poll hears the instruction; the tick after that reports.
        await fleet.TickDeviceForTestAsync(silent, now.AddSeconds(6), CancellationToken.None);
        Assert.Equal(SimulationScenario.Nominal, silent.Scenario);

        await fleet.TickDeviceForTestAsync(silent, now.AddSeconds(7), CancellationToken.None);
        await DrainAsync(http, expectedProcessed: 1);

        Assert.NotEmpty((await http.GetFromJsonAsync<List<ITelemetryPacket>>($"/api/sensors/{silent.MacAddress}/telemetry"))!);
    }

    /// <summary>Counts registrations passing through, and forwards everything.</summary>
    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Registrations;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/sensors")
            {
                Interlocked.Increment(ref Registrations);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Forwards to whichever client it currently holds, so a test can replace the gateway mid-run.</summary>
    private sealed class SwappableHandler(HttpClient inner) : HttpMessageHandler
    {
        public HttpClient Inner { get; set; } = inner;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };

            return await Inner.SendAsync(forwarded, cancellationToken);
        }
    }

    private sealed class NoopHostLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    /// <summary>Answers 503 to the first few requests, then forwards everything to a real client.</summary>
    private sealed class FlakyHandler(HttpClient inner, int refusalsBeforeUp, Action onRefusal) : HttpMessageHandler
    {
        private int _seen;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _seen) <= refusalsBeforeUp)
            {
                onRefusal();

                throw new HttpRequestException("connection refused");
            }

            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };

            return await inner.SendAsync(forwarded, cancellationToken);
        }
    }
}
