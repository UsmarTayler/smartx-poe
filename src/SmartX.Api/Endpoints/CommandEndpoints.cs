using SmartX.Api.Commands;
using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Issuing, tracking, acknowledging and undoing commands to actuators.
/// </summary>
/// <remarks>
/// Issuing a command answers <c>202 Accepted</c>, never 200 or 201: the gateway has taken responsibility for
/// delivering it, and nothing has happened to the device yet. The device collects it on its next check-in
/// (the same call that fetches its desired state) and confirms with <c>POST /api/commands/{id}/ack</c>.
/// </remarks>
internal static class CommandEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaximumLimit = 1000;

    internal static RouteGroupBuilder MapCommandEndpoints(this RouteGroupBuilder api)
    {
        var commands = api.MapGroup("/commands");

        commands.MapPost("", IssueAsync).WithName("IssueCommand");
        commands.MapPost("/zone", IssueForZoneAsync).WithName("IssueZoneCommand");
        commands.MapGet("", List).WithName("ListCommands");
        commands.MapGet("/log", Log).WithName("GetCommandLog");
        commands.MapGet("/undo-stack", UndoStack).WithName("GetUndoStack");
        commands.MapPost("/undo", Undo).WithName("UndoCommand");
        commands.MapGet("/composite/{id:guid}", GetComposite).WithName("GetCompositeCommand");
        commands.MapGet("/{id:guid}", Get).WithName("GetCommand");
        commands.MapPost("/{id:guid}/ack", Acknowledge).WithName("AcknowledgeCommand");

        return api;
    }

    private static async Task<IResult> IssueAsync(
        IssueCommandRequest request,
        ISensorRegistry registry,
        CommandService commands,
        CancellationToken cancellationToken)
    {
        if (await registry.FindAsync(request.MacAddress, cancellationToken) is not { } sensor)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{request.MacAddress}'.");
        }

        var issue = commands.Issue(sensor, request.Action, request.Level);

        return issue.Command is { } command
            ? Results.Accepted($"/api/commands/{command.Id}", command)
            : Refused(issue.Error!);
    }

    private static async Task<IResult> IssueForZoneAsync(
        ZoneCommandRequest request,
        ISensorRegistry registry,
        IZoneStore zones,
        CommandService commands,
        CancellationToken cancellationToken)
    {
        var catalogue = await zones.GetCatalogueAsync(cancellationToken);
        var sensors = await registry.ListAsync(cancellationToken);

        var issue = commands.IssueForZone(request.ZoneId, request.Action, request.Level, catalogue, sensors);

        return issue switch
        {
            { Result: { } result } => Results.Accepted($"/api/commands/composite/{result.CompositeId}", result),
            { Failure: IssueFailure.UnknownZone } => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Zone not found",
                detail: issue.Error),
            _ => Refused(issue.Error!),
        };
    }

    private static IResult Refused(string reason) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [nameof(IssueCommandRequest.Action)] = [reason] },
            title: "The command cannot be sent");

    private static IResult List(ICommandStore store, string? mac, CommandState? state, int? limit) =>
        Results.Ok(store.List(mac, state, Clamp(limit)));

    private static IResult Get(Guid id, ICommandStore store, ICommandLog log) =>
        store.Find(id) is { } command
            ? Results.Ok(new CommandDetail(command, [.. log.Query(null, id, MaximumLimit).Reverse()]))
            : NotFound(id);

    private static IResult Log(ICommandLog log, string? mac, Guid? commandId, int? limit) =>
        Results.Ok(log.Query(mac, commandId, Clamp(limit)));

    private static IResult GetComposite(Guid id, CommandService commands)
    {
        var children = commands.Composite(id);

        return children.Count == 0
            ? NotFound(id)
            : Results.Ok(new CompositeCommandResult(id, string.Empty, children[0].Action, children, CommandService.StatusOf(children)));
    }

    private static IResult Acknowledge(Guid id, CommandAckRequest request, CommandService commands)
    {
        var result = commands.Acknowledge(id, request);

        return result.Outcome switch
        {
            AckOutcome.Applied or AckOutcome.Repeated => Results.Ok(result.Command),
            AckOutcome.NotFound => NotFound(id),
            _ => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The command cannot take that acknowledgement",
                detail: result.Message),
        };
    }

    private static IResult UndoStack(CommandService commands) => Results.Ok(commands.UndoStack());

    /// <summary>Pops the most recent action and undoes it.</summary>
    /// <remarks>
    /// The body always says exactly what happened, whatever the status. 200 when everything was undone or
    /// partly undone, 409 when the undo was refused, and 404 when there was nothing on the stack. A refusal is
    /// a conflict with the device's state, not a server fault, so it is never a 500.
    /// </remarks>
    private static IResult Undo(CommandService commands)
    {
        var result = commands.Undo();

        return result.Outcome switch
        {
            UndoOutcome.NothingToUndo => Results.Json(result, statusCode: StatusCodes.Status404NotFound),
            UndoOutcome.Refused => Results.Json(result, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Ok(result),
        };
    }

    private static IResult NotFound(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Command not found",
            detail: $"No command has id {id}.");

    private static int Clamp(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);
}
