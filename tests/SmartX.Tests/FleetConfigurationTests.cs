using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.DeviceSimulator;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Covers configuring the simulated fleet from environment variables, which is how Docker does it.
/// </summary>
public class FleetConfigurationTests
{
    /// <summary>Binds simulator options the way the host does: from a flat set of environment-style keys.</summary>
    private static SimulatorOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

        return configuration.GetSection(SimulatorOptions.SectionName).Get<SimulatorOptions>() ?? new SimulatorOptions();
    }

    [Fact]
    public void Defaults_run_the_whole_fleet_with_its_demo_mix()
    {
        var options = Bind();

        Assert.Equal(55, options.DeviceCount);
        Assert.Equal(FleetMix.Demo, options.Mix);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ShadowPollInterval);
    }

    [Fact]
    public void Every_setting_can_be_supplied_the_way_an_environment_variable_supplies_it()
    {
        // Environment variables arrive with "__" already turned into ":", so these are the keys the host sees.
        var options = Bind(
            ("Simulator:ApiBaseUrl", "http://api:8080"),
            ("Simulator:DeviceCount", "12"),
            ("Simulator:Mix", "Nominal"),
            ("Simulator:BackfillDuration", "00:02:00"),
            ("Simulator:Scenarios:B8_27_EB_10_00_01", "Critical"));

        Assert.Equal("http://api:8080", options.ApiBaseUrl);
        Assert.Equal(12, options.DeviceCount);
        Assert.Equal(FleetMix.Nominal, options.Mix);
        Assert.Equal(TimeSpan.FromMinutes(2), options.BackfillDuration);
        Assert.Equal(SimulationScenario.Critical, options.Scenarios["B8_27_EB_10_00_01"]);
    }

    [Fact]
    public async Task A_nominal_mix_and_a_named_override_decide_each_devices_starting_scenario()
    {
        using var api = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Zones:Hierarchy"] = "Demo" })));

        var options = Bind(
            ("Simulator:DeviceCount", "3"),
            ("Simulator:Mix", "Nominal"),
            ("Simulator:BackfillDuration", "00:00:00"),
            ("Simulator:Scenarios:B8_27_EB_10_00_02", "Critical"));

        var fleet = new TestableFleet(new GatewayClient(api.CreateClient(), NullLogger<GatewayClient>.Instance), options);

        await fleet.RegisterAsync();

        var scenarios = fleet.Devices.ToDictionary(device => device.MacAddress, device => device.Scenario);

        Assert.Equal(3, scenarios.Count);
        Assert.Equal(SimulationScenario.Nominal, scenarios["B8:27:EB:10:00:01"]);
        Assert.Equal(SimulationScenario.Critical, scenarios["B8:27:EB:10:00:02"]);

        // Device 3 is a Nominal device in the demo mix already, so a nominal mix leaves it as it was, and
        // device 2 would have been a Drift device — the override replaced it.
        Assert.Equal(SimulationScenario.Nominal, scenarios["B8:27:EB:10:00:03"]);
    }

    /// <summary>Exposes the fleet's registration step and its devices, without starting the reporting loop.</summary>
    private sealed class TestableFleet
    {
        private readonly DeviceFleet _fleet;

        public TestableFleet(GatewayClient gateway, SimulatorOptions options) =>
            _fleet = new DeviceFleet(
                gateway,
                Options.Create(options),
                TimeProvider.System,
                new NoopLifetime(),
                NullLogger<DeviceFleet>.Instance);

        public IReadOnlyList<SimulatedDevice> Devices => _fleet.DevicesForTest;

        public Task RegisterAsync() => _fleet.RegisterFleetForTestAsync();
    }

    private sealed class NoopLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
