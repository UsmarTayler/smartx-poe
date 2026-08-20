using System.Net.Http.Json;
using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// The client's only route to the Smart-X API.
/// </summary>
/// <remarks>
/// The client project deliberately holds no reference to SmartX.Api. It knows a base address and
/// the contracts in SmartX.Shared, nothing more, so the two halves are separated in fact and not
/// just in folder layout. Routing every call through one typed client also keeps URL strings out
/// of the components.
/// </remarks>
public sealed class SmartXApiClient(HttpClient http)
{
    /// <summary>
    /// Asks the API whether it is up.
    /// </summary>
    /// <returns>The reported health, or <c>null</c> when the API could not be reached.</returns>
    /// <remarks>
    /// An unreachable API is an expected state during development, not an exceptional one — the
    /// backend is a separate process the user may simply not have started. Returning null lets the
    /// landing page render a "disconnected" banner instead of the exception bubbling up through
    /// the component lifecycle and blanking the page.
    /// </remarks>
    public async Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await http.GetFromJsonAsync<HealthResponse>("api/health", cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
