using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// The deployment hierarchy.
/// </summary>
internal static class ZoneEndpoints
{
    internal static RouteGroupBuilder MapZoneEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/zones", GetTreeAsync).WithName("GetZoneTree");

        return api;
    }

    /// <summary>
    /// The hierarchy as nested trees, one per root.
    /// </summary>
    /// <remarks>
    /// Returns the tree rather than the flat parent-id list the catalogue is built from. The client
    /// renders a hierarchy, and reassembling one there would mean a second implementation of the
    /// same descent — in a different language's idiom, free to disagree with this one about what a
    /// cyclic fragment or a dangling parent means.
    /// </remarks>
    private static async Task<IResult> GetTreeAsync(
        IZoneStore zones,
        CancellationToken cancellationToken)
    {
        var catalogue = await zones.GetCatalogueAsync(cancellationToken);

        return Results.Ok(catalogue.BuildTree());
    }
}
