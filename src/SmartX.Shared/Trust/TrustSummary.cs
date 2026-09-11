namespace SmartX.Shared;

/// <summary>
/// Confidence in the mesh as a whole.
/// </summary>
/// <param name="MeshConfidence">Mean trust across tracked nodes, or null when none are tracked.</param>
/// <param name="LowestScore">The worst single score, or null when none are tracked.</param>
/// <param name="WorstNode">The node holding that score, or null when none are tracked.</param>
/// <param name="NodeCount">Nodes tracked.</param>
/// <param name="Nominal">Nodes in the nominal band.</param>
/// <param name="Degrading">Nodes in the degrading band.</param>
/// <param name="Fault">Nodes in the fault band.</param>
/// <remarks>
/// The mean is reported alongside the lowest score deliberately. An average is a poor alarm: forty
/// healthy nodes and one dead one average to about 0.93, which reads as fine. The lowest score and
/// the per-band counts are what stop a single fault disappearing into a comfortable mean.
/// <para>
/// Null rather than 0.5 for an empty mesh, because 0.5 would be a statement about nodes that do not
/// exist.
/// </para>
/// </remarks>
public sealed record TrustSummary(
    double? MeshConfidence,
    double? LowestScore,
    string? WorstNode,
    int NodeCount,
    int Nominal,
    int Degrading,
    int Fault);
