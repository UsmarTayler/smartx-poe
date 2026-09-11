using SmartX.Api.Trust;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Read access to node trust, for the dashboard.
/// </summary>
/// <remarks>
/// Handlers are synchronous because the engine is: every read is a copy of in-memory state taken
/// under a lock held for microseconds, with no I/O to wait on. Declaring them async would allocate
/// a state machine per request to await nothing.
/// <para>
/// Reads have no side effects. Scores are recomputed by ingestion and by the heartbeat monitor's
/// sweep, never by a GET — so refreshing the dashboard can never be the thing that pushes a node
/// across a threshold and fires an escalation.
/// </para>
/// </remarks>
internal static class TrustEndpoints
{
    internal static RouteGroupBuilder MapTrustEndpoints(this RouteGroupBuilder api)
    {
        var trust = api.MapGroup("/trust");

        trust.MapGet("", ListTrust).WithName("ListTrust");

        // "summary" cannot collide with {mac} below: routing ranks a literal segment above a
        // parameter, so /api/trust/summary always reaches this handler.
        trust.MapGet("/summary", GetSummary).WithName("GetTrustSummary");
        trust.MapGet("/{mac}", GetNodeTrust).WithName("GetNodeTrust");
        trust.MapGet("/{mac}/history", GetNodeTrustHistory).WithName("GetNodeTrustHistory");

        return api;
    }

    /// <summary>Every tracked node, worst first.</summary>
    /// <remarks>
    /// The ordering is the engagement strategy. The node most likely to be at fault is the first row
    /// an operator sees, without searching, filtering or knowing which node to look for.
    /// </remarks>
    private static IResult ListTrust(ITrustEngine engine) => Results.Ok(engine.Snapshot());

    /// <summary>Aggregate confidence and per-band counts.</summary>
    private static IResult GetSummary(ITrustEngine engine) => Results.Ok(engine.Summary());

    /// <summary>One node's score, band and reason.</summary>
    private static IResult GetNodeTrust(string mac, ITrustEngine engine) =>
        engine.TryGet(mac, out var snapshot) ? Results.Ok(snapshot) : NotTracked(mac);

    /// <summary>One node's sampled trust series, oldest first, for its sparkline.</summary>
    private static IResult GetNodeTrustHistory(string mac, ITrustEngine engine) =>
        engine.TryGetHistory(mac, out var history) ? Results.Ok(history) : NotTracked(mac);

    private static IResult NotTracked(string mac) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Node not tracked",
            detail: $"No trust is being tracked for '{mac}'. Nodes are tracked from registration.");
}
