using Microsoft.Extensions.Options;

namespace SmartX.DeviceSimulator;

/// <summary>
/// Starts the device simulator.
/// </summary>
/// <remarks>
/// An explicit entry point rather than top-level statements. Top-level statements generate a class called
/// Program in the global namespace, and the API already has one; the test project references both, so two
/// of them would make every reference to Program ambiguous.
/// </remarks>
internal static class SimulatorHost
{
    internal static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services
            .AddOptions<SimulatorOptions>()
            .Bind(builder.Configuration.GetSection(SimulatorOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out _), "Simulator:ApiBaseUrl must be an absolute URL.")
            .Validate(options => options.DeviceCount > 0, "Simulator:DeviceCount must be positive.")
            .Validate(options => options.TickInterval > TimeSpan.Zero, "Simulator:TickInterval must be positive.")
            .Validate(options => options.ShadowPollInterval > TimeSpan.Zero, "Simulator:ShadowPollInterval must be positive.")
            .Validate(options => options.NeighbourReportInterval > TimeSpan.Zero, "Simulator:NeighbourReportInterval must be positive.")
            .ValidateOnStart();

        // Injected rather than read from DateTimeOffset.UtcNow, matching the API, so the fleet can be driven by a
        // controlled clock in a test.
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddSingleton(services => new DeviceKeyring(services.GetRequiredService<IOptions<SimulatorOptions>>().Value.FleetSecret));

        builder.Services.AddHttpClient<GatewayClient>((services, client) =>
        {
            var options = services.GetRequiredService<IOptions<SimulatorOptions>>().Value;

            client.BaseAddress = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/");

            // A device that waits forever on one reading stops reporting the next one. Ten seconds is far longer
            // than the gateway needs to validate and enqueue, and short enough that a hung request self-corrects.
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        builder.Services.AddHostedService<DeviceFleet>();

        await builder.Build().RunAsync();
    }
}
