namespace SmartX.Shared;

/// <summary>
/// A zone together with everything nested inside it.
/// </summary>
/// <param name="Zone">The zone at this position.</param>
/// <param name="Children">Zones nested directly inside it, empty for a leaf.</param>
/// <remarks>
/// The nested shape the API hands to the client, as opposed to the flat parent-id list the
/// catalogue is built from. The client renders a tree, so sending it a tree saves it from
/// reassembling one — and reassembling it there would mean a second implementation of the same
/// walk, in a second language's idiom, that could disagree with this one.
/// </remarks>
public sealed record ZoneTreeNode(DeploymentZone Zone, IReadOnlyList<ZoneTreeNode> Children);
