using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Liveness probe for the API.
/// </summary>
/// <remarks>
/// Endpoints are registered through extension methods on the route group rather than inline in
/// Program.cs. Each future feature area gets its own file and a single Map* call, which keeps
/// Program.cs a wiring file instead of growing into one long list of lambdas.
/// </remarks>
internal static class HealthEndpoints
{
    internal static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
           .WithName("GetHealth");

        return api;
    }
}
