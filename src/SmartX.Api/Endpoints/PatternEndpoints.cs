using SmartX.Api.Patterns;
using SmartX.Api.Search;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// What the system has learned about what operators do, the investigations opened when a node falls into fault, and the
/// report of an operator opening a device.
/// </summary>
internal static class PatternEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    internal static RouteGroupBuilder MapPatternEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/patterns", GetPatterns).WithName("GetPatterns");
        api.MapGet("/patterns/for/{mac}", GetPatternsForDevice).WithName("GetPatternsForDevice");
        api.MapGet("/investigations", ListInvestigations).WithName("ListInvestigations");
        api.MapGet("/investigations/{id:guid}", GetInvestigation).WithName("GetInvestigation");
        api.MapPost("/investigations/{id:guid}/verdict", GiveVerdict).WithName("GiveInvestigationVerdict");
        api.MapPost("/activity/selection", RecordSelection).WithName("RecordSelection");

        return api;
    }

    /// <summary>The learned associations, best first.</summary>
    /// <remarks>
    /// Only associations that have cleared the support and lift thresholds are returned unless <c>includeWeak</c> is set,
    /// which is for looking at why something did not qualify. The thresholds in force come back with them, so a client can
    /// say what a pattern had to clear.
    /// </remarks>
    private static IResult GetPatterns(
        string? trigger,
        string? mac,
        string? level,
        bool? includeWeak,
        int? limit,
        IPatternEngine patterns,
        DeviceDirectory devices,
        TimeProvider time)
    {
        Dictionary<string, string[]> errors = [];

        var triggerKind = ParseChoice<TriggerKind>(nameof(trigger), trigger, errors);
        var patternLevel = ParseChoice<PatternLevel>(nameof(level), level, errors);

        if (limit is < 1 or > MaxLimit)
        {
            errors[nameof(limit)] = [$"'limit' must be between 1 and {MaxLimit}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "The pattern filters are not valid");
        }

        string? device = null;

        if (!string.IsNullOrWhiteSpace(mac))
        {
            if (!MacAddressValidator.TryNormalise(mac, out var normalised) || !devices.TryDescribe(normalised, out _))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Device not found",
                    detail: $"No sensor is registered with address '{mac}'.");
            }

            device = normalised;
        }

        var found = patterns.Associations(
            new PatternQuery(triggerKind, device, patternLevel, includeWeak ?? false),
            time.GetUtcNow());

        return Results.Ok(new PatternsResponse([.. found.Take(limit ?? DefaultLimit)], patterns.Settings));
    }

    /// <summary>What tends to follow the triggers seen on one device, from the device's own history where it has enough and from its kind in its zone where it does not.</summary>
    private static IResult GetPatternsForDevice(string mac, IPatternEngine patterns, DeviceDirectory devices, TimeProvider time)
    {
        if (!MacAddressValidator.TryNormalise(mac, out var normalised) || !devices.TryDescribe(normalised, out _))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Device not found",
                detail: $"No sensor is registered with address '{mac}'.");
        }

        return Results.Ok(patterns.ForDevice(normalised, time.GetUtcNow()));
    }

    private static IResult ListInvestigations(string? status, int? limit, InvestigationService investigations)
    {
        Dictionary<string, string[]> errors = [];

        var parsed = ParseChoice<InvestigationStatus>(nameof(status), status, errors);

        if (limit is < 1 or > MaxLimit)
        {
            errors[nameof(limit)] = [$"'limit' must be between 1 and {MaxLimit}."];
        }

        return errors.Count > 0
            ? Results.ValidationProblem(errors, title: "The investigation filters are not valid")
            : Results.Ok(investigations.List(parsed, limit ?? DefaultLimit));
    }

    private static IResult GetInvestigation(Guid id, InvestigationService investigations) =>
        investigations.Get(id) is { } found
            ? Results.Ok(found)
            : Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Investigation not found",
                detail: $"No investigation has id '{id}'.");

    /// <summary>Records what an operator concluded. A later verdict replaces an earlier one and undoes what it changed.</summary>
    private static IResult GiveVerdict(Guid id, VerdictRequest request, InvestigationService investigations)
    {
        var result = investigations.GiveVerdict(id, request);

        return result.Outcome switch
        {
            VerdictOutcome.Recorded => Results.Ok(result.Investigation),
            VerdictOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Investigation not found",
                detail: $"No investigation has id '{id}'."),
            _ => Results.ValidationProblem(
                new Dictionary<string, string[]> { [nameof(request.Cause)] = [result.Error ?? "The verdict is not valid."] },
                title: "The verdict is not valid"),
        };
    }

    /// <summary>Records that an operator opened a device. The client sends it on a deliberate click, so each one is real.</summary>
    private static IResult RecordSelection(SelectionRequest request, DeviceDirectory devices, ActivityRecorder activity)
    {
        if (request is null
            || !MacAddressValidator.TryNormalise(request.MacAddress, out var mac)
            || !devices.TryDescribe(mac, out var profile))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Device not found",
                detail: "No sensor is registered with that address.");
        }

        activity.RecordSelection(mac, profile.Room);

        return Results.NoContent();
    }

    private static T? ParseChoice<T>(string name, string? text, Dictionary<string, string[]> errors)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value))
        {
            return value;
        }

        errors[name] = [$"'{text}' is not a {name}. Choose one of: {string.Join(", ", Enum.GetNames<T>())}."];

        return null;
    }
}
