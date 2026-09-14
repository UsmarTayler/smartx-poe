using SmartX.Api.Simulation;
using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Live control of the mesh simulator.
/// </summary>
/// <remarks>
/// Exists so a fault can be triggered on demand — during a demo, or to watch one node's trust respond —
/// without typing readings by hand. Both routes answer 409 while simulation is disabled, rather than 404,
/// because the route exists and the sensor may too; it is the API's configuration that refuses.
/// </remarks>
internal static class SimulationEndpoints
{
    internal static RouteGroupBuilder MapSimulationEndpoints(this RouteGroupBuilder api)
    {
        var simulation = api.MapGroup("/simulation");

        simulation.MapGet("", List).WithName("ListSimulatedNodes");
        simulation.MapPost("/{mac}/scenario", ChangeScenarioAsync).WithName("ChangeSimulationScenario");

        return api;
    }

    /// <summary>Every simulated sensor and its current scenario.</summary>
    private static IResult List(MeshSimulator simulator) =>
        simulator.IsEnabled ? Results.Ok(simulator.List()) : Disabled();

    /// <summary>Switches one sensor's scenario.</summary>
    private static async Task<IResult> ChangeScenarioAsync(
        string mac,
        ScenarioChangeRequest request,
        MeshSimulator simulator,
        ISensorRegistry registry,
        CancellationToken cancellationToken)
    {
        if (!simulator.IsEnabled)
        {
            return Disabled();
        }

        // An enum can carry an undeclared number even when JSON names are expected.
        if (!Enum.IsDefined(request.Scenario))
        {
            return ScenarioProblem($"'{request.Scenario}' is not a scenario. Use one of: {string.Join(", ", Enum.GetNames<SimulationScenario>())}.");
        }

        if (await registry.FindAsync(mac, cancellationToken) is not { } sensor)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{mac}'.");
        }

        return simulator.TrySetScenario(sensor, request.Scenario, out var status, out var error)
            ? Results.Ok(status)
            : ScenarioProblem(error);
    }

    private static IResult ScenarioProblem(string message) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [nameof(ScenarioChangeRequest.Scenario)] = [message] },
            title: "The scenario cannot be applied");

    private static IResult Disabled() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Simulation is not enabled",
            detail: "Start the API with Simulation:Enabled set to true, for example " +
                    "'dotnet run --project src/SmartX.Api -- --Simulation:Enabled=true'.");
}
