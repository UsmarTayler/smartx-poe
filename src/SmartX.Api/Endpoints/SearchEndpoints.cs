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
    /// The query binds enums from their names, so a misspelt band or category is a 400 from the framework
    /// before this runs. An unknown zone is a 404, matching the zone-wide command endpoint: the question has no
    /// answer because the thing asked about does not exist, which is different from asking about a zone that is
    /// empty.
    /// </para>
    /// </remarks>
    private static async Task<IResult> SearchAsync(
        string? q,
        SensorCategory? category,
        ActuatorKind? actuatorKind,
        string? zone,
        TrustBand? band,
        AlertKind? alert,
        DeviceIndex index,
        IZoneStore zones,
        ActivityRecorder activity,
        CancellationToken cancellationToken)
    {
        var filters = new DeviceSearchFilters(
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            category,
            actuatorKind,
            string.IsNullOrWhiteSpace(zone) ? null : zone.Trim(),
            band,
            alert);

        var catalogue = await zones.GetCatalogueAsync(cancellationToken);

        if (filters.Zone is { } zoneId && !catalogue.TryFind(zoneId, out _))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Zone not found",
                detail: $"No zone has id '{zoneId}'.");
        }

        var devices = index.Search(filters, catalogue);

        activity.RecordSearch(filters, devices.Count);

        return Results.Ok(new DeviceSearchResponse(filters, devices.Count, devices));
    }
}
