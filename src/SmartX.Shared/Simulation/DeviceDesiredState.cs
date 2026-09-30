namespace SmartX.Shared;

/// <summary>
/// What a device finds when it checks in: the state an operator wants it in, and any commands waiting for it.
/// </summary>
/// <remarks>
/// The "device shadow", as AWS IoT calls it, or the "device twin" in Azure's terms. The simulator runs as its
/// own process, so the API cannot reach into it to change a scenario or send a command. It does what real IoT
/// platforms do instead: the API stores what an operator wants, and each device polls for its own and applies
/// it. The device is the only thing that ever changes the device.
/// <para>
/// <b>One check-in, not two.</b> Commands travel in this same response rather than behind a second endpoint,
/// because a real ESP32 wakes, makes one call, acts on what it finds, and goes back to sleep. A second polling
/// loop would double the traffic and the battery cost for no benefit.
/// </para>
/// <para>
/// <see cref="Version"/> increases on every change of scenario, so a device can tell a new instruction from
/// the one it already applied without comparing the whole document. A device with commands waiting and no
/// scenario ever set gets version 0 and a null scenario.
/// </para>
/// </remarks>
/// <param name="MacAddress">The device this applies to.</param>
/// <param name="Scenario">How the operator wants it to behave, or null if nothing has been asked.</param>
/// <param name="Version">Increases by one on every change of scenario. 0 when none has been set.</param>
/// <param name="UpdatedAt">When the scenario was last changed.</param>
/// <param name="Commands">
/// Commands the device is fetching now. Each is handed over exactly once: the gateway marks it delivered as it
/// answers and never sends it again.
/// </param>
public sealed record DeviceDesiredState(
    string MacAddress,
    SimulationScenario? Scenario,
    long Version,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<Command>? Commands = null)
{
    /// <summary>The commands, never null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Command> CommandList => Commands ?? [];
}
