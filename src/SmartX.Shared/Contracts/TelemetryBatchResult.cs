namespace SmartX.Shared;

/// <summary>
/// What a batch ingestion did.
/// </summary>
/// <param name="MacAddress">The sensor the batch was attributed to.</param>
/// <param name="Accepted">Readings that passed validation and were queued for processing.</param>
/// <param name="Rejected">Readings that did not.</param>
/// <param name="Staging">What the jagged staging layout held, and what it saved.</param>
/// <param name="Errors">One message per rejected reading, naming its position in the batch.</param>
/// <remarks>
/// A batch is reported rather than accepted or refused as a whole. One malformed reading among five
/// hundred is a faulty sample, not a reason to discard the other 499 — so the good ones go through
/// and the caller is told precisely which were not.
/// <para>
/// <paramref name="Accepted"/> counts readings the gateway took responsibility for, not readings already
/// stored. They are retained and scored by the ingestion worker just after this response is written, and a
/// batch larger than the retention window will still push its own earliest readings out of the node's
/// sliding buffer as it is processed.
/// </para>
/// </remarks>
public sealed record TelemetryBatchResult(
    string MacAddress,
    int Accepted,
    int Rejected,
    StagingStatistics Staging,
    IReadOnlyList<string> Errors);
