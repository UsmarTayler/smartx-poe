namespace SmartX.Shared;

/// <summary>
/// What a batch ingestion did.
/// </summary>
/// <param name="MacAddress">The sensor the batch was attributed to.</param>
/// <param name="Accepted">Readings that passed validation and entered the node's history.</param>
/// <param name="Rejected">Readings that did not.</param>
/// <param name="Retained">How many readings the node's sliding window holds after the batch.</param>
/// <param name="Staging">What the jagged staging layout held, and what it saved.</param>
/// <param name="Errors">One message per rejected reading, naming its position in the batch.</param>
/// <remarks>
/// A batch is reported rather than accepted or refused as a whole. One malformed reading among five
/// hundred is a faulty sample, not a reason to discard the other 499 — so the good ones are stored
/// and the caller is told precisely which were not.
/// <para>
/// <paramref name="Retained"/> can be lower than <paramref name="Accepted"/>, and that is not a
/// bug: a batch larger than the retention window pushes its own earliest readings out as it is
/// ingested. Reporting both makes the sliding window visible rather than surprising.
/// </para>
/// </remarks>
public sealed record TelemetryBatchResult(
    string MacAddress,
    int Accepted,
    int Rejected,
    int Retained,
    StagingStatistics Staging,
    IReadOnlyList<string> Errors);
