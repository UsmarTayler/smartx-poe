namespace SmartX.Shared;

/// <summary>
/// The state an operator wants a device to be in: the "device shadow", as AWS IoT calls it, or the
/// "device twin" in Azure's terms.
/// </summary>
/// <remarks>
/// The simulator runs as its own process, so the API cannot reach into it to change a scenario. It does what
/// real IoT platforms do instead: the API stores the desired state, and each device polls for its own and
/// applies it. The device is the only thing that ever changes the device.
/// <para>
/// <see cref="Version"/> increases on every change, so a device can tell a new instruction from the one it
/// already applied without comparing the whole document.
/// </para>
/// </remarks>
/// <param name="MacAddress">The device this applies to.</param>
/// <param name="Scenario">How the operator wants it to behave.</param>
/// <param name="Version">Increases by one on every change.</param>
/// <param name="UpdatedAt">When the desired state was last changed.</param>
public sealed record DeviceDesiredState(
    string MacAddress,
    SimulationScenario Scenario,
    long Version,
    DateTimeOffset UpdatedAt);
