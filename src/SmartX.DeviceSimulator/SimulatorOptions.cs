namespace SmartX.DeviceSimulator;

/// <summary>
/// Configuration for the simulated fleet.
/// </summary>
/// <remarks>
/// Bound from the "Simulator" section, which in Docker is supplied as environment variables
/// (<c>Simulator__ApiBaseUrl</c> and friends), so one image can point at any gateway.
/// </remarks>
public sealed class SimulatorOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Simulator";

    /// <summary>Where the gateway is.</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5266";

    /// <summary>How many of the demo devices to run, from the top of the fleet list.</summary>
    /// <remarks>Lower it to watch a handful of devices, or leave it to run the whole forty.</remarks>
    public int DeviceCount { get; set; } = 40;

    /// <summary>How much history to post on first start, so the dashboard is not empty.</summary>
    /// <remarks>
    /// Posted as timestamped readings through the ordinary telemetry endpoint. Ten minutes at the demo
    /// cadences is a few thousand requests, which takes a few seconds and fills every trust sparkline.
    /// </remarks>
    public TimeSpan BackfillDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How many backfill requests may be in flight at once.</summary>
    public int BackfillConcurrency { get; set; } = 8;

    /// <summary>How often each device asks the gateway what it should be doing.</summary>
    /// <remarks>
    /// The device-shadow poll. Five seconds is responsive enough to change a scenario on camera, and cheap:
    /// one small request per device per interval, which is the same order as the telemetry itself.
    /// </remarks>
    public TimeSpan ShadowPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the fleet checks which devices are due to report.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Seed for the generated values, so two runs produce the same readings.</summary>
    public int RandomSeed { get; set; } = 1729;

    /// <summary>How long to keep retrying while the gateway is not answering yet.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Scenario overrides by MAC, for a demo that wants a particular device misbehaving.</summary>
    /// <remarks>
    /// Supplied as <c>Simulator__Scenarios__B8:27:EB:10:00:01=Critical</c>. Anything not named here starts
    /// in the scenario the fleet definition gives it.
    /// </remarks>
    public IDictionary<string, SmartX.Shared.SimulationScenario> Scenarios { get; set; } =
        new Dictionary<string, SmartX.Shared.SimulationScenario>(StringComparer.OrdinalIgnoreCase);
}
