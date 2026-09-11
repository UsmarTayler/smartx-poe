namespace SmartX.Shared;

/// <summary>
/// Confidence in the mesh as a whole.
/// </summary>
/// <param name="MeshConfidence">Mean trust across nodes that have evidence, or null when none do.</param>
/// <param name="LowestScore">The lowest score among nodes that have evidence, or null.</param>
/// <param name="WorstNode">The most urgent node that has evidence, or null.</param>
/// <param name="NodeCount">Every tracked node, unproven ones included.</param>
/// <param name="Nominal">Nodes in the nominal band.</param>
/// <param name="Unproven">Nodes with no evidence yet.</param>
/// <param name="Degrading">Nodes in the degrading band.</param>
/// <param name="Fault">Nodes in the fault band.</param>
/// <remarks>
/// The mean is reported alongside the lowest score deliberately. An average is a poor alarm: forty
/// healthy nodes and one dead one average to about 0.93, which reads as fine. The lowest score and
/// the per-band counts are what stop a single fault disappearing into a comfortable mean.
/// <para>
/// Unproven nodes are counted but kept out of the confidence figures. Their 0.5 is the absence of
/// evidence, not a measurement, and averaging it in would drag a healthy mesh toward 0.5 every time
/// a batch of sensors was registered — and could make an unproven node the "lowest score" while a
/// genuinely failing one sat above it.
/// </para>
/// </remarks>
public sealed record TrustSummary(
    double? MeshConfidence,
    double? LowestScore,
    string? WorstNode,
    int NodeCount,
    int Nominal,
    int Unproven,
    int Degrading,
    int Fault);
