namespace SmartX.Shared;

/// <summary>
/// What a staging buffer is currently holding, and what the jagged layout saved.
/// </summary>
/// <param name="WindowCount">Ingestion windows staged.</param>
/// <param name="TotalReadings">Readings across all windows — the cells actually allocated.</param>
/// <param name="LongestWindow">Readings in the largest window.</param>
/// <param name="RectangularCells">
/// Cells a rectangular <c>T[,]</c> would have needed: every row padded to
/// <paramref name="LongestWindow"/>.
/// </param>
/// <param name="WastedCells">
/// Cells the rectangular layout would have allocated and never written.
/// </param>
/// <param name="WastedBytes">
/// Those cells in bytes, using the inline size of <c>T</c>. This is the number the jagged layout
/// avoids paying.
/// </param>
public readonly record struct StagingStatistics(
    int WindowCount,
    int TotalReadings,
    int LongestWindow,
    long RectangularCells,
    long WastedCells,
    long WastedBytes);
