using SmartX.Api.Ingestion;

namespace SmartX.Api.Endpoints;

/// <summary>
/// What the ingestion pipeline is doing right now.
/// </summary>
/// <remarks>
/// Exists so the split between the two lanes can be seen rather than asserted. A marker, or an operator,
/// can watch the standard lane back up under load while the critical lane stays at a depth of zero and its
/// average wait stays in single-digit milliseconds.
/// </remarks>
internal static class PipelineEndpoints
{
    internal static RouteGroupBuilder MapPipelineEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/pipeline/stats", (TelemetryIngestionQueue queue) => Results.Ok(queue.Stats()))
            .WithName("GetPipelineStats");

        return api;
    }
}
