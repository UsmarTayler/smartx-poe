using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>What the user entered in a sensor's send control.</summary>
/// <param name="Number">The reading, for a numeric sensor.</param>
/// <param name="State">The reading, for an actuator.</param>
/// <param name="Display">Exactly what the user entered, for the outcome message.</param>
/// <remarks>
/// Nullable value types rather than one field of type <c>object</c>, so one event can carry either
/// shape of reading without boxing it. The registration's payload kind says which field is meaningful.
/// </remarks>
public readonly record struct ReadingInput(double? Number, bool State, string Display);

/// <summary>What happened to the last reading sent by hand.</summary>
/// <param name="Accepted">Whether the API accepted it.</param>
/// <param name="Message">A sentence naming the value and, when refused, why.</param>
/// <param name="At">When the answer came back.</param>
public sealed record SendOutcome(bool Accepted, string Message, DateTimeOffset At);

/// <summary>Everything the telemetry view shows for one sensor.</summary>
public sealed class SensorTelemetry
{
    /// <summary>The node's retained readings, oldest first, as the ring buffer holds them.</summary>
    public IReadOnlyList<ITelemetryPacket> Recent { get; internal set; } = [];

    public SendOutcome? LastOutcome { get; internal set; }

    public bool IsSending { get; internal set; }

    public bool IsLoaded { get; internal set; }

    public ApiProblem? LoadProblem { get; internal set; }

    /// <summary>The most recent reading, if there is one.</summary>
    public ITelemetryPacket? Latest => Recent.Count == 0 ? null : Recent[^1];
}
