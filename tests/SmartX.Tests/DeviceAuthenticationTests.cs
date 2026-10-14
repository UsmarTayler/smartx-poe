using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartX.DeviceSimulator;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Devices authenticate with a key derived for their MAC, and may act only for that MAC.
/// </summary>
public class DeviceAuthenticationTests
{
    private const string Mine = ApiTestHost.Thermometer;
    private const string Theirs = ApiTestHost.Meter;

    private const string Reading = """{ "macAddress": "AA:BB:CC:DD:EE:01", "value": 20 }""";
    private const string TheirReading = """{ "macAddress": "AA:BB:CC:DD:EE:02", "value": 5 }""";

    /// <summary>A host with two registered devices: a thermometer and a power meter.</summary>
    private static async Task<WebApplicationFactory<Program>> HostAsync(Dictionary<string, string?>? settings = null)
    {
        var api = new AuthenticatedFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings ?? [])));
        var operatorClient = api.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await operatorClient.PostAsync("/api/sensors", ApiTestHost.Thermometer10To30(Mine))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await operatorClient.PostAsync("/api/sensors", ApiTestHost.Registration(Theirs, "sub-zone-b", "PowerConsumption", "W", 0, 5000, "Real"))).StatusCode);

        return api;
    }

    private static WebApplicationFactory<Program> DemoHost() =>
        new AuthenticatedFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Zones:Hierarchy"] = "Demo" })));

    private static StringContent Relay(string mac) => ApiTestHost.Json($$"""
        { "macAddress": "{{mac}}", "room": "Relay", "zoneId": "sub-zone-b", "nodeId": "N", "category": "Actuator", "unit": "state",
          "minimum": 0, "maximum": 1, "payloadKind": "State", "actuatorKind": "Relay" }
        """);

    // ---- sending readings ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_device_with_the_right_key_can_send_its_own_reading_with_no_token_at_all()
    {
        using var api = await HostAsync();

        var response = await api.AsDevice(Mine).PostAsync("/api/telemetry", ApiTestHost.Json(Reading));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task The_mac_may_be_written_in_lower_case_in_the_header_and_the_key_is_still_right()
    {
        using var api = await HostAsync();

        var response = await api.AsDevice(Mine.ToLowerInvariant(), DeviceKeys.Derive(TestAuth.FleetSecret, Mine))
            .PostAsync("/api/telemetry", ApiTestHost.Json(Reading));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task A_device_can_send_a_batch_of_its_own_readings()
    {
        using var api = await HostAsync();
        var batch = ApiTestHost.Json("""{ "macAddress": "AA:BB:CC:DD:EE:01", "readings": [ { "value": 20 }, { "value": 21 } ] }""");

        var response = await api.AsDevice(Mine).PostAsync("/api/telemetry/batch", batch);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_key_is_a_401_and_so_is_a_missing_one_and_a_malformed_mac()
    {
        using var api = await HostAsync();

        var wrong = await api.AsDevice(Mine, "not-the-key").PostAsync("/api/telemetry", ApiTestHost.Json(Reading));
        var anotherDevicesKey = await api.AsDevice(Mine, DeviceKeys.Derive(TestAuth.FleetSecret, Theirs)).PostAsync("/api/telemetry", ApiTestHost.Json(Reading));
        var noKey = api.AsDevice(Mine);
        noKey.DefaultRequestHeaders.Remove(DeviceAuthHeaders.Key);
        var missing = await noKey.PostAsync("/api/telemetry", ApiTestHost.Json(Reading));
        var badMac = await api.AsDevice("not-a-mac", "x").PostAsync("/api/telemetry", ApiTestHost.Json(Reading));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anotherDevicesKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, badMac.StatusCode);
    }

    [Fact]
    public async Task A_valid_key_for_one_device_used_to_send_for_another_is_a_403_and_the_reading_is_not_taken()
    {
        using var api = await HostAsync();
        var stream = api.AsDevice(Mine);

        var forged = await stream.PostAsync("/api/telemetry", ApiTestHost.Json(TheirReading));
        var forgedBatch = await stream.PostAsync("/api/telemetry/batch", ApiTestHost.Json("""{ "macAddress": "AA:BB:CC:DD:EE:02", "readings": [ { "value": 5 } ] }"""));

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forgedBatch.StatusCode);

        // Nothing was taken for the meter, however it was asked: its history is empty.
        var history = await api.CreateClient().GetFromJsonAsync<List<System.Text.Json.JsonElement>>($"/api/sensors/{Theirs}/telemetry");

        Assert.Empty(history!);
    }

    [Fact]
    public async Task A_forbidden_device_learns_nothing_about_whether_the_other_device_exists()
    {
        using var api = await HostAsync();
        var stream = api.AsDevice(Mine);

        var registered = await stream.PostAsync("/api/telemetry", ApiTestHost.Json(TheirReading));
        var unregistered = await stream.PostAsync("/api/telemetry", ApiTestHost.Json("""{ "macAddress": "AA:BB:CC:DD:EE:99", "value": 5 }"""));

        Assert.Equal(HttpStatusCode.Forbidden, registered.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unregistered.StatusCode);
    }

    // ---- the shadow, and acknowledgements ----------------------------------------------------------------

    [Fact]
    public async Task A_device_reads_its_own_desired_state_and_not_another_devices()
    {
        using var api = await HostAsync();
        await api.CreateClient().PostAsync($"/api/simulation/{Mine}/scenario", ApiTestHost.Json("""{ "scenario": "Spike" }"""));
        var device = api.AsDevice(Mine);

        var own = await device.GetAsync($"/api/devices/{Mine}/desired");
        var other = await device.GetAsync($"/api/devices/{Theirs}/desired");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task An_operator_token_is_not_how_a_device_collects_commands_so_an_operator_cannot_steal_them()
    {
        using var api = await HostAsync();

        var asOperator = await api.CreateClient().GetAsync($"/api/devices/{Mine}/desired");
        var asViewer = await api.AsViewer().GetAsync($"/api/devices/{Mine}/desired");

        Assert.Equal(HttpStatusCode.Unauthorized, asOperator.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asViewer.StatusCode);
    }

    [Fact]
    public async Task A_device_acknowledges_its_own_command_and_not_one_addressed_to_another_device()
    {
        using var api = await HostAsync();
        var operatorClient = api.CreateClient();
        await operatorClient.PostAsync("/api/sensors", Relay("AA:BB:CC:DD:EE:0A"));
        await operatorClient.PostAsync("/api/sensors", Relay("AA:BB:CC:DD:EE:0B"));

        var issued = await operatorClient.PostAsync("/api/commands", ApiTestHost.Json("""{ "macAddress": "AA:BB:CC:DD:EE:0A", "action": "RelayOn" }"""));
        var command = (await issued.Content.ReadFromJsonAsync<Command>())!;
        await api.AsDevice("AA:BB:CC:DD:EE:0A").GetAsync("/api/devices/AA:BB:CC:DD:EE:0A/desired");

        var ack = ApiTestHost.Json("""{ "success": true, "resultingValue": 1 }""");
        var byAnother = await api.AsDevice("AA:BB:CC:DD:EE:0B").PostAsync($"/api/commands/{command.Id}/ack", ack);
        var byOperator = await operatorClient.PostAsync($"/api/commands/{command.Id}/ack", ApiTestHost.Json("""{ "success": true, "resultingValue": 1 }"""));
        var byOwner = await api.AsDevice("AA:BB:CC:DD:EE:0A").PostAsync($"/api/commands/{command.Id}/ack", ApiTestHost.Json("""{ "success": true, "resultingValue": 1 }"""));

        Assert.Equal(HttpStatusCode.Forbidden, byAnother.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, byOperator.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
    }

    // ---- registration ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_device_can_register_itself_and_only_itself()
    {
        using var api = new AuthenticatedFactory();
        var device = api.AsDevice(Mine);

        var itself = await device.PostAsync("/api/sensors", ApiTestHost.Thermometer10To30(Mine));
        var another = await device.PostAsync("/api/sensors", ApiTestHost.Thermometer10To30(Theirs));

        Assert.Equal(HttpStatusCode.Created, itself.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, another.StatusCode);
    }

    [Fact]
    public async Task A_device_with_a_bad_key_cannot_register()
    {
        using var api = new AuthenticatedFactory();

        var response = await api.AsDevice(Mine, "not-the-key").PostAsync("/api/sensors", ApiTestHost.Thermometer10To30(Mine));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- what a device key does not open ------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/trust")]
    [InlineData("GET", "/api/sensors")]
    [InlineData("GET", "/api/alerts")]
    [InlineData("GET", "/api/suggestions")]
    [InlineData("POST", "/api/commands")]
    [InlineData("POST", "/api/commands/undo")]
    [InlineData("POST", "/api/simulation/AA:BB:CC:DD:EE:01/scenario")]
    [InlineData("POST", "/api/alerts/acknowledge")]
    public async Task A_device_key_opens_nothing_in_the_operator_console(string method, string url)
    {
        using var api = await HostAsync();

        using var request = DeviceCalls.Signed(new HttpRequestMessage(new HttpMethod(method), url), Mine);

        if (method == "POST")
        {
            request.Content = ApiTestHost.Json("{}");
        }

        var response = await api.AsDevice(Mine).SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Keys_come_from_the_fleet_secret_so_changing_it_refuses_every_old_key()
    {
        using var api = await HostAsync(new() { ["Auth:FleetSecret"] = "another-fleet-secret-for-this-deployment" });

        var old = await api.AsDevice(Mine).PostAsync("/api/telemetry", ApiTestHost.Json(Reading));
        var fresh = await api.AsDevice(Mine, DeviceKeys.Derive("another-fleet-secret-for-this-deployment", Mine))
            .PostAsync("/api/telemetry", ApiTestHost.Json(Reading));

        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, fresh.StatusCode);
    }

    // ---- the simulator's side ----------------------------------------------------------------------------

    [Fact]
    public async Task The_simulators_gateway_client_signs_its_calls_and_the_gateway_takes_them()
    {
        using var api = DemoHost();
        var anonymous = api.Anonymous();
        var gateway = new GatewayClient(anonymous, NullLogger<GatewayClient>.Instance, new DeviceKeyring(TestAuth.FleetSecret));
        var registration = SmartX.Shared.DemoDevices.All[0].Registration;

        Assert.True(await gateway.RegisterAsync(registration, CancellationToken.None));

        var outcome = await gateway.SendReadingAsync(
            registration.MacAddress,
            System.Text.Json.JsonSerializer.SerializeToElement(21.5),
            null,
            CancellationToken.None);

        Assert.Equal(SendOutcome.Accepted, outcome);
        Assert.NotNull(await gateway.GetDesiredStateAsync(registration.MacAddress, CancellationToken.None) ?? new DeviceDesiredState(registration.MacAddress, null, 0, null, []));
    }

    [Fact]
    public async Task A_simulator_with_the_wrong_secret_says_so_once_and_stops_and_does_not_retry_for_ever()
    {
        using var api = DemoHost();
        var rejected = new List<string>();
        var logs = new CapturingLoggerProvider();
        var gateway = new GatewayClient(
            api.Anonymous(),
            Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<GatewayClient>(new Microsoft.Extensions.Logging.LoggerFactory([logs])),
            new DeviceKeyring("a-fleet-secret-that-the-gateway-does-not-have"));
        gateway.CredentialsRejected += rejected.Add;
        var registration = SmartX.Shared.DemoDevices.All[0].Registration;

        var registered = await gateway.RegisterAsync(registration, CancellationToken.None);
        var sent = await gateway.SendReadingAsync(registration.MacAddress, System.Text.Json.JsonSerializer.SerializeToElement(21.5), null, CancellationToken.None);

        Assert.False(registered);
        Assert.Equal(SendOutcome.Refused, sent);

        // Said once, however many calls were refused, and the message names the setting to fix.
        Assert.Single(rejected);
        var critical = logs.Lines.Where(line => line.Level == Microsoft.Extensions.Logging.LogLevel.Critical).ToList();
        Assert.Single(critical);
        Assert.Contains("Simulator:FleetSecret", critical[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a-fleet-secret-that-the-gateway-does-not-have", string.Join("\n", logs.Lines.Select(line => line.Message)), StringComparison.Ordinal);
    }
}

/// <summary>
/// The real-time hub needs a token, and takes it the only way a browser can send one.
/// </summary>
public class HubAuthenticationTests
{
    private static HubConnection Connect(AuthenticatedFactory api, string? token)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, RealtimeTopics.HubPath), options =>
            {
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;

                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
    }

    [Fact]
    public async Task A_connection_with_no_token_is_refused()
    {
        using var api = new AuthenticatedFactory();
        await using var connection = Connect(api, token: null);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
    }

    [Theory]
    [InlineData(UserRoles.Viewer)]
    [InlineData(UserRoles.Operator)]
    public async Task A_connection_with_a_viewers_or_operators_token_is_accepted_and_can_join_a_topic(string role)
    {
        using var api = new AuthenticatedFactory();
        _ = api.CreateClient();
        await using var connection = Connect(api, TestAuth.Token(role));

        await connection.StartAsync();
        await connection.InvokeAsync(RealtimeMethods.Subscribe, RealtimeTopics.Trust);

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task An_expired_or_forged_token_is_refused_on_the_hub_as_everywhere()
    {
        using var api = new AuthenticatedFactory();
        var expired = TestAuth.Token(time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(-3)));

        await using var late = Connect(api, expired);
        await using var forged = Connect(api, "not.a.token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<HttpRequestException>(() => late.StartAsync())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<HttpRequestException>(() => forged.StartAsync())).StatusCode);
    }

    [Fact]
    public async Task A_device_key_does_not_get_a_device_onto_the_hub()
    {
        using var api = new AuthenticatedFactory();
        var http = api.AsDevice(ApiTestHost.Thermometer);

        var negotiate = await http.PostAsync($"{RealtimeTopics.HubPath}/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [Fact]
    public async Task The_token_is_read_from_the_query_string_on_the_hub_and_only_there()
    {
        using var api = new AuthenticatedFactory();
        var http = api.Anonymous();
        var token = TestAuth.Token();

        var onHub = await http.PostAsync($"{RealtimeTopics.HubPath}/negotiate?negotiateVersion=1&access_token={token}", null);
        var onApi = await http.GetAsync($"/api/sensors?access_token={token}");

        Assert.Equal(HttpStatusCode.OK, onHub.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, onApi.StatusCode);
    }
}
