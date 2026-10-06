using Microsoft.Extensions.Options;
using SmartX.Api.History;
using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Timelines, the mesh-wide event stream, and the record of what operators did.
/// </summary>
internal static class HistoryEndpoints
{
    internal static RouteGroupBuilder MapHistoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/sensors/{mac}/timeline", GetTimelineAsync).WithName("GetSensorTimeline");
        api.MapGet("/timeline/events", GetEvents).WithName("GetTimelineEvents");
        api.MapGet("/activity", GetActivity).WithName("GetActivity");

        return api;
    }

    /// <summary>
    /// One sensor's readings over a range, downsampled into at most <c>maxPoints</c> buckets.
    /// </summary>
    /// <remarks>
    /// <c>from</c> is inclusive and <c>to</c> exclusive, the same half-open range the history store uses. When
    /// they are omitted the range is the last <see cref="HistoryOptions.DefaultTimelineWindow"/> up to now. A
    /// registered sensor with no readings in the range is a valid answer — an empty timeline, with its markers
    /// if it has any — and only an unregistered one is a 404.
    /// </remarks>
    private static async Task<IResult> GetTimelineAsync(
        string mac,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? maxPoints,
        ISensorRegistry registry,
        IHistoryStore history,
        IOptions<HistoryOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var sensor = await registry.FindAsync(mac, cancellationToken);

        if (sensor is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{mac}'.");
        }

        var end = to ?? time.GetUtcNow();
        var start = from ?? end - settings.DefaultTimelineWindow;
        var points = maxPoints ?? settings.DefaultMaxPoints;

        Dictionary<string, string[]> errors = [];

        if (start >= end)
        {
            errors["from"] = ["'from' must be earlier than 'to'. The range includes 'from' and excludes 'to'."];
        }

        if (points < 1 || points > settings.MaxPointsLimit)
        {
            errors["maxPoints"] = [$"'maxPoints' must be between 1 and {settings.MaxPointsLimit}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "The timeline range is not valid");
        }

        var entries = history.GetRange(sensor.MacAddress, start, end);

        return Results.Ok(TimelineBuilder.Build(sensor, entries, start, end, points));
    }

    /// <summary>Everything that happened across the mesh in a range: commands, alerts and trust transitions.</summary>
    /// <remarks>
    /// Time order, oldest first. When more events fall in the range than <c>limit</c> allows, the most recent
    /// ones are returned and <c>truncated</c> says so, because the end of the range is where an operator is
    /// looking. Searches are not part of this stream: they are what operators did and not what happened to the
    /// mesh, and are returned by the activity endpoint.
    /// </remarks>
    private static IResult GetEvents(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        IHistoryStore history,
        IOptions<HistoryOptions> options,
        TimeProvider time)
    {
        var settings = options.Value;
        var end = to ?? time.GetUtcNow();
        var start = from ?? end - settings.DefaultTimelineWindow;
        var count = limit ?? settings.DefaultEventLimit;

        Dictionary<string, string[]> errors = [];

        if (start >= end)
        {
            errors["from"] = ["'from' must be earlier than 'to'. The range includes 'from' and excludes 'to'."];
        }

        if (count < 1 || count > settings.MaxEventLimit)
        {
            errors["limit"] = [$"'limit' must be between 1 and {settings.MaxEventLimit}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "The event range is not valid");
        }

        var events = history.GetEvents(start, end).Where(entry => entry.Kind != HistoryKind.Search).ToList();
        var truncated = events.Count > count;

        if (truncated)
        {
            events = events[^count..];
        }

        return Results.Ok(new TimelineEvents(
            start,
            end,
            [.. events.Select(entry => new TimelineEvent(
                entry.Sequence,
                entry.Timestamp,
                entry.MacAddress,
                entry.Kind,
                entry.Event!.Summary,
                entry.Event.Detail))],
            truncated));
    }

    /// <summary>What operators did in a range — searches and commands — with the alerts active at the time.</summary>
    /// <remarks>
    /// A command appears here when an operator caused it: issued, cancelled or undone. Its delivery,
    /// acknowledgement and timeout are the device's doing and stay in the command log and the event stream.
    /// </remarks>
    private static IResult GetActivity(
        DateTimeOffset? from,
        DateTimeOffset? to,
        IHistoryStore history,
        IOptions<HistoryOptions> options,
        TimeProvider time)
    {
        var settings = options.Value;
        var end = to ?? time.GetUtcNow();
        var start = from ?? end - settings.DefaultTimelineWindow;

        if (start >= end)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["from"] = ["'from' must be earlier than 'to'. The range includes 'from' and excludes 'to'."] },
                title: "The activity range is not valid");
        }

        var activity = history.GetEvents(start, end)
            .Where(entry => entry.Kind == HistoryKind.Search || IsOperatorCommand(entry))
            .Select(ToActivity)
            .ToList();

        return Results.Ok(activity);
    }

    private static bool IsOperatorCommand(HistoryEntry entry) =>
        entry.Kind == HistoryKind.Command
        && entry.Event?.CommandEvent is CommandEvent.Issued or CommandEvent.Cancelled or CommandEvent.Reverted;

    private static ActivityEntry ToActivity(HistoryEntry entry)
    {
        var details = entry.Event!;
        var isSearch = entry.Kind == HistoryKind.Search;

        return new ActivityEntry(
            entry.Sequence,
            entry.Timestamp,
            isSearch ? ActivityKind.Search : ActivityKind.Command,
            details.Summary,
            details.Filters,
            details.ResultCount,
            isSearch ? null : entry.MacAddress,
            details.CommandId,
            details.ActiveAlerts ?? []);
    }
}
