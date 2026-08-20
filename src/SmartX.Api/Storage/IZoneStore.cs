using SmartX.Shared;

namespace SmartX.Api.Storage;

/// <summary>
/// Where the deployment hierarchy comes from.
/// </summary>
/// <remarks>
/// Returns the whole catalogue rather than exposing per-zone queries, because both recursive walks
/// need arbitrary access to the tree as they go. Handing back a built
/// <see cref="IZoneCatalogue"/> also keeps the indexing cost in one place.
/// </remarks>
public interface IZoneStore
{
    /// <summary>The current deployment hierarchy.</summary>
    Task<IZoneCatalogue> GetCatalogueAsync(CancellationToken cancellationToken = default);
}
