namespace SmartX.Shared;

/// <summary>
/// Payload returned by GET /api/health.
/// </summary>
/// <remarks>
/// This lives in the shared library rather than in either endpoint because the API produces it
/// and the client consumes it, so both sides must agree on the wire format. Declaring the shape
/// twice is how the two halves silently drift apart when a field is later renamed.
/// A record is used for value equality, which makes assertions in the test project trivial.
/// </remarks>
/// <param name="Status">Service state. "ok" when the API is running and able to serve requests.</param>
public sealed record HealthResponse(string Status);
