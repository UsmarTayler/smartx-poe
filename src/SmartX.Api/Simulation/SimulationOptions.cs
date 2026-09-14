namespace SmartX.Api.Simulation;

/// <summary>
/// Configuration for the demo mesh: the seeded zones and sensors, their backfilled history, and the
/// simulator that keeps telemetry flowing.
/// </summary>
/// <remarks>
/// Bound from the "Simulation" section. <see cref="Enabled"/> is off by default, so a plain
/// <c>dotnet run</c> starts with empty stores and the brief's four-zone hierarchy — which is what manual
/// testing and every automated test expect. Turning it on replaces the zone hierarchy with the larger demo
/// one and fills the registry before the API accepts its first request.
/// </remarks>
public sealed class SimulationOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Simulation";

    /// <summary>Seed the demo mesh on startup and run the simulator.</summary>
    public bool Enabled { get; set; }

    /// <summary>How far back each seeded sensor's history is replayed.</summary>
    /// <remarks>
    /// Twenty minutes fills the whole trust sparkline (120 samples, ten seconds apart) and spans two
    /// half-lives, so the scores on the first page load reflect settled evidence rather than a start-up
    /// ramp from 0.5.
    /// </remarks>
    public TimeSpan BackfillDuration { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>How often the simulator checks which sensors are due to report.</summary>
    /// <remarks>
    /// A tick is not a reading: each sensor reports on its own cadence, set a fifth inside its heartbeat
    /// interval. The tick has to be finer than that margin — one second for a five-second sensor — or a
    /// reading could land late enough to count as a missed heartbeat.
    /// </remarks>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Seed for the random values, so two runs produce the same readings.</summary>
    public int RandomSeed { get; set; } = 1729;
}
