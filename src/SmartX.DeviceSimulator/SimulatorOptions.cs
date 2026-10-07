namespace SmartX.DeviceSimulator;

/// <summary>
/// Configuration for the simulated fleet.
/// </summary>
/// <remarks>
/// Bound from the "Simulator" section, which in Docker is supplied as environment variables
/// (<c>Simulator__ApiBaseUrl</c> and friends), so one image can point at any gateway.
/// </remarks>
/// <summary>Which scenarios the fleet starts in.</summary>
public enum FleetMix
{
    /// <summary>Each device starts in the scenario the fleet definition gives it.</summary>
    Demo,

    /// <summary>Every device starts healthy.</summary>
    Nominal,
}

public sealed class SimulatorOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Simulator";

    /// <summary>Where the gateway is.</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5266";

    /// <summary>How many of the demo devices to run, from the top of the fleet list.</summary>
    /// <remarks>Lower it to watch a handful of devices, or leave it to run the whole forty.</remarks>
    public int DeviceCount { get; set; } = 48;

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

    /// <summary>How many times faster than normal the fleet reports. 1 is the demo's pace.</summary>
    /// <remarks>
    /// For load testing the dashboard. Each device's reporting interval is divided by this, so 10 makes every device
    /// report ten times as often. A device reports at most once per <see cref="TickInterval"/>, so to go beyond two
    /// readings a second per device, lower the tick as well. Only the live readings speed up: the backfill on start
    /// and each device's registered heartbeat do not, so trust scoring still expects the interval it was registered
    /// with.
    /// </remarks>
    public double ReportingRateMultiplier { get; set; } = 1;

    /// <summary>The chance that an actuator fails to apply a command it was given, from 0 to 1.</summary>
    /// <remarks>Zero by default, so commands succeed and demos are predictable. Raise it to see Failed commands.</remarks>
    public double CommandFailureRate { get; set; }

    /// <summary>Seed for the generated values, so two runs produce the same readings.</summary>
    public int RandomSeed { get; set; } = 1729;

    /// <summary>How long to keep retrying while the gateway is not answering yet.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Which scenarios the fleet starts in.</summary>
    /// <remarks>
    /// <see cref="FleetMix.Demo"/> gives each device the scenario the fleet definition assigns it, so the
    /// dashboard shows a spread of bands. <see cref="FleetMix.Nominal"/> starts every device healthy, for a
    /// calm baseline or a load test that should not be full of deliberate faults.
    /// </remarks>
    public FleetMix Mix { get; set; } = FleetMix.Demo;

    /// <summary>Scenario overrides by MAC, for a demo that wants a particular device misbehaving.</summary>
    /// <remarks>
    /// A MAC contains colons, which cannot appear in an environment variable name, so the address may be
    /// written with underscores or hyphens instead: <c>Simulator__Scenarios__B8_27_EB_10_00_01=Critical</c>.
    /// Applied on top of <see cref="Mix"/>.
    /// </remarks>
    public IDictionary<string, SmartX.Shared.SimulationScenario> Scenarios { get; set; } =
        new Dictionary<string, SmartX.Shared.SimulationScenario>(StringComparer.OrdinalIgnoreCase);
}
