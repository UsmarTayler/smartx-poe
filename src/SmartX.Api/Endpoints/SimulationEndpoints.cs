using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Device management: what an operator wants a device to do, and how the device finds out.
/// </summary>
/// <remarks>
/// <b>The device-shadow pattern.</b> AWS IoT calls it a device shadow and Azure calls it a device twin, but
/// the shape is the same: the platform stores a desired state, the device polls for its own and applies it,
/// and the device is the only thing that ever changes the device.
/// <para>
/// In Part 1 the simulator ran inside the API, so a scenario change was a method call. The simulator is now
/// a separate program that may be on another machine, asleep, or not yet started, so a method call is no
/// longer available — and pushing to it would need the gateway to hold a connection open to every device.
/// Polling a shadow is what real fleets do, and Part 3 reuses this same channel to acknowledge commands.
/// </para>
/// </remarks>
internal static class SimulationEndpoints
{
    internal static RouteGroupBuilder MapSimulationEndpoints(this RouteGroupBuilder api)
    {
        // Kept at its Part 1 address so anything written against it still works; what changed is that it now
        // records an intention instead of reaching into a running simulator.
        api.MapPost("/simulation/{mac}/scenario", SetScenarioAsync).WithName("SetDeviceScenario");
        api.MapGet("/simulation", ListAsync).WithName("ListDesiredState");

        // The device's own view: one document, for one device, polled by that device.
        api.MapGet("/devices/{mac}/desired", GetDesiredAsync).WithName("GetDeviceDesiredState");

        return api;
    }

    /// <summary>Records the scenario an operator wants a device to run.</summary>
    private static async Task<IResult> SetScenarioAsync(
        string mac,
        ScenarioChangeRequest request,
        ISensorRegistry registry,
        IDeviceShadowStore shadows,
        CancellationToken cancellationToken)
    {
        // An enum can carry an undeclared number even when JSON names are expected.
        if (!Enum.IsDefined(request.Scenario))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    [nameof(ScenarioChangeRequest.Scenario)] =
                        [$"'{request.Scenario}' is not a scenario. Use one of: {string.Join(", ", Enum.GetNames<SimulationScenario>())}."],
                },
                title: "The scenario cannot be applied");
        }

        // Checked against the registry so a typo answers 404 rather than quietly creating a shadow for a
        // device that does not exist.
        if (await registry.FindAsync(mac, cancellationToken) is not { } sensor)
        {
            return NotRegistered(mac);
        }

        var desired = await shadows.SetAsync(sensor.MacAddress, request.Scenario, cancellationToken);

        // 202 rather than 200: the gateway has recorded the intention, but the device applies it on its next
        // poll, so the change has not happened yet at the moment this is answered.
        return TypedResults.Accepted((string?)null, desired);
    }

    /// <summary>Every device an operator has asked something of.</summary>
    private static async Task<IResult> ListAsync(IDeviceShadowStore shadows, CancellationToken cancellationToken) =>
        Results.Ok(await shadows.ListAsync(cancellationToken));

    /// <summary>One device's desired state, polled by the device itself.</summary>
    /// <remarks>
    /// Answers 404 when nothing has been asked of the device, which a device reads as "carry on as you are".
    /// That keeps the common case a single cheap request with no body.
    /// </remarks>
    private static async Task<IResult> GetDesiredAsync(
        string mac,
        IDeviceShadowStore shadows,
        CancellationToken cancellationToken) =>
        await shadows.FindAsync(mac, cancellationToken) is { } desired
            ? Results.Ok(desired)
            : NotRegistered(mac);

    private static IResult NotRegistered(string mac) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "No desired state",
            detail: $"No desired state is recorded for '{mac}'.");
}
