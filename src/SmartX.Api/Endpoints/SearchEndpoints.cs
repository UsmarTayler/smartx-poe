using SmartX.Api.Search;
using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Finding devices by what they are, where they are and what is wrong with them.
/// </summary>
internal static class SearchEndpoints
{
    internal static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/devices/search", SearchAsync).WithName("SearchDevices");

        return api;
    }

    /// <summary>
    /// Finds the devices matching every filter given.
    /// </summary>
    /// <remarks>
    /// Every filter is optional and they combine with AND. The brief asks for devices to be found "based on
    /// their operational categories or active alert states", and the useful questions combine them: the pumps
    /// that are disconnected, the valves in Zone 3 that are faulted.
    /// <para>
    /// The enum filters arrive as text and are parsed here, ignoring case, so <c>actuatorKind=valve</c> works as
    /// well as <c>Valve</c> and a misspelling gets a 400 that lists the choices. The framework's own enum binding
    /// is case-sensitive and answers a bad value with an empty 400, which tells a caller nothing. An unknown
    /// zone is a 404, matching the zone-wide command endpoint: the question has no answer because the thing
    /// asked about does not exist, which is different from asking about a zone that is empty.
    /// </para>
    /// </remarks>
    private static async Task<IResult> SearchAsync(
        string? q,
        string? category,
        string? actuatorKind,
        string? zone,
        string? band,
        string? alert,
        DeviceIndex index,
        IZoneStore zones,
        ActivityRecorder activity,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string[]> errors = [];

        var filters = new DeviceSearchFilters(
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            ParseChoice<SensorCategory>(nameof(category), category, errors),
            ParseChoice<ActuatorKind>(nameof(actuatorKind), actuatorKind, errors),
            string.IsNullOrWhiteSpace(zone) ? null : zone.Trim(),
            ParseChoice<TrustBand>(nameof(band), band, errors),
            ParseChoice<AlertKind>(nameof(alert), alert, errors));

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "The search filters are not valid");
        }

        var catalogue = await zones.GetCatalogueAsync(cancellationToken);

        if (filters.Zone is { } zoneId && !catalogue.TryFind(zoneId, out _))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Zone not found",
                detail: $"No zone has id '{zoneId}'.");
        }

        var devices = index.Search(filters, catalogue);

        activity.RecordSearch(filters, devices);

        return Results.Ok(new DeviceSearchResponse(filters, devices.Count, devices));
    }

    /// <summary>Reads an optional enum filter, ignoring case, and records an error that lists the choices when it is wrong.</summary>
    private static T? ParseChoice<T>(string name, string? text, Dictionary<string, string[]> errors)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // IsDefined as well as TryParse: TryParse accepts any number, so "99" would otherwise become a member
        // that does not exist.
        if (Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value))
        {
            return value;
        }

        errors[name] = [$"'{text}' is not a {name}. Choose one of: {string.Join(", ", Enum.GetNames<T>())}."];

        return null;
    }
}
