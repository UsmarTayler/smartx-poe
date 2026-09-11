using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>Narrows the trust list by band and by text, keeping the API's worst-first order.</summary>
/// <remarks>
/// A service rather than logic in the list component, so the rule for what "matches" is written once
/// and the component only renders what it is given. Filtering never re-sorts: the order is the
/// engagement strategy, and it is decided on the server, not by whatever the client happens to show.
/// </remarks>
public static class TrustFilter
{
    public static IReadOnlyList<NodeTrustSnapshot> Apply(
        IReadOnlyList<NodeTrustSnapshot> nodes,
        IReadOnlySet<TrustBand> hiddenBands,
        string search) =>
        [.. nodes.Where(node =>
            !hiddenBands.Contains(node.Band)
            && (search.Length == 0
                || node.MacAddress.Contains(search, StringComparison.OrdinalIgnoreCase)
                || node.Reason.Contains(search, StringComparison.OrdinalIgnoreCase)))];
}
