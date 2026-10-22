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
        api.MapGet("/history/{mac}/range", GetRangeAsync).WithName("GetHistoryRange");
        api.MapGet("/history/stats", GetStats).WithName("GetHistoryStats");

        return api;
    }

    /// <summary>
    /// One sensor's raw readings in a window, oldest first: "the moisture readings between 14:00 and 16:00 for Node A".
    /// </summary>
    /// <remarks>
    /// The timeline endpoint downsamples for a chart; this returns the readings themselves, one page at a time. The window is
    /// half-open like every range in the store, and both ends are required, since an unbounded query is exactly what the tree
    /// is there to avoid. <c>metric</c> keeps only readings in that unit, so a sensor that changed its unit does not mix two
    /// quantities in one answer. Only readings are returned: trust samples and events live in the same tree and are not
    /// measurements. A window with more readings than <c>limit</c> comes back truncated, with <c>nextFrom</c> to continue from.
    /// <para>
    /// The store finds the window with one seek. The page is then taken from it, so the cost is O(log n) plus the size of the
    /// window and not of the page, which is bounded by retention (a day of history) and not by what a caller asks for.
    /// </para>
    /// </remarks>
    private static async Task<IResult> GetRangeAsync(
        string mac,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? metric,
        int? limit,
        ISensorRegistry registry,
        IHistoryStore history,
        IOptions<HistoryOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var page = limit ?? settings.DefaultRangeLimit;

        Dictionary<string, string[]> errors = [];

        if (from is null)
        {
            errors["from"] = ["'from' is required. The range includes 'from' and excludes 'to'."];
        }

        if (to is null)
        {
            errors["to"] = ["'to' is required. The range includes 'from' and excludes 'to'."];
        }

        if (from is { } start && to is { } end && start >= end)
        {
            errors["from"] = ["'from' must be earlier than 'to'. The range includes 'from' and excludes 'to'."];
        }

        if (page < 1 || page > settings.MaxRangeLimit)
        {
            errors["limit"] = [$"'limit' must be between 1 and {settings.MaxRangeLimit}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "The history range is not valid");
        }

        var sensor = await registry.FindAsync(mac, cancellationToken);

        if (sensor is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{mac}'.");
        }

        var matching = history.GetRange(sensor.MacAddress, from!.Value, to!.Value)
            .Where(entry => entry.Kind == HistoryKind.Reading)
            .Where(entry => string.IsNullOrEmpty(metric) || string.Equals(entry.Unit, metric, StringComparison.Ordinal))
            .Take(page + 1)
            .ToList();

        var truncated = matching.Count > page;

        return Results.Ok(new HistoryRange(
            sensor.MacAddress,
            from.Value,
            to.Value,
            string.IsNullOrEmpty(metric) ? null : metric,
            [.. matching.Take(page).Select(entry => new HistoryReading(entry.Sequence, entry.Timestamp, entry.Value, entry.Unit))],
            truncated,
            truncated ? matching[page].Timestamp : null));
    }

    /// <summary>How big the history store is and how tall its trees are, next to the shortest a tree of that size could be.</summary>
    private static IResult GetStats(int? largest, IHistoryStore history)
    {
        var count = largest ?? 10;

        if (count < 0 || count > 100)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["largest"] = ["'largest' must be between 0 and 100."] },
                title: "The history statistics request is not valid");
        }

        return Results.Ok(history.GetStats(count));
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

        var events = history.GetEvents(start, end).Where(entry => entry.Kind is not (HistoryKind.Search or HistoryKind.Selection)).ToList();
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
            .Where(entry => entry.Kind is HistoryKind.Search or HistoryKind.Selection || IsOperatorCommand(entry))
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
        var kind = entry.Kind switch
        {
            HistoryKind.Search => ActivityKind.Search,
            HistoryKind.Selection => ActivityKind.Selection,
            _ => ActivityKind.Command,
        };

        return new ActivityEntry(
            entry.Sequence,
            entry.Timestamp,
            kind,
            details.Summary,
            details.Filters,
            details.ResultCount,
            isSearch ? null : entry.MacAddress,
            details.CommandId,
            details.ActiveAlerts ?? []);
    }
}
