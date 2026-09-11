using SmartX.Shared;

namespace SmartX.Api.Trust;

/// <summary>One outcome and when it was observed.</summary>
/// <remarks>
/// A struct, so a node's outcome window is one flat array of these rather than an array of
/// references to hundreds of small heap objects that would each have to be allocated per packet.
/// The timestamp is the gateway's arrival time, not the device's own clock: trust is about how the
/// node behaves as seen from here, and a device whose clock has drifted must not be able to age its
/// own failures into irrelevance.
/// </remarks>
internal readonly record struct OutcomeRecord(TrustOutcome Outcome, DateTimeOffset ObservedAt);
