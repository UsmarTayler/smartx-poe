using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>One zone as the registration form offers it: a pickable id with its full path.</summary>
/// <param name="Id">The zone id the request carries.</param>
/// <param name="Name">The zone's own name.</param>
/// <param name="Path">Every ancestor's name down to this one, such as "Facility A › Zone 1 › Sub-Zone B".</param>
/// <param name="Depth">Distance from the root, used to indent the option.</param>
/// <param name="Constraints">What this level itself imposes.</param>
public sealed record ZoneOption(string Id, string Name, string Path, int Depth, ZoneConstraints Constraints);

/// <summary>Turns the API's nested zone tree into an ordered list the picker can render.</summary>
public static class ZoneOptions
{
    /// <summary>Flattens the tree depth-first, so every zone is listed directly beneath its parent.</summary>
    /// <remarks>
    /// Recursive for the same reason the server's walks are: a sub-zone is listed exactly as a zone
    /// is. It needs no cycle or depth guard of its own, because the tree it receives was already
    /// built by the server's guarded descent — a cyclic fragment is simply absent from it.
    /// <para>
    /// Each option carries its full path rather than just its name. "Zone 1" alone is ambiguous as
    /// soon as two facilities both have one, and choosing the wrong one would register a sensor under
    /// the wrong constraints.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ZoneOption> Flatten(IReadOnlyList<ZoneTreeNode> roots)
    {
        var options = new List<ZoneOption>();

        foreach (var root in roots)
        {
            Visit(root, parentPath: null, depth: 0, options);
        }

        return options;
    }

    private static void Visit(ZoneTreeNode node, string? parentPath, int depth, List<ZoneOption> options)
    {
        var path = parentPath is null ? node.Zone.Name : $"{parentPath} › {node.Zone.Name}";

        options.Add(new ZoneOption(node.Zone.Id, node.Zone.Name, path, depth, node.Zone.Constraints));

        // Base case: a leaf has no children, so the loop body never runs.
        foreach (var child in node.Children)
        {
            Visit(child, path, depth + 1, options);
        }
    }
}
