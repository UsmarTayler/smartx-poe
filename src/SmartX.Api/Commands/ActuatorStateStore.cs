using System.Collections.Concurrent;

namespace SmartX.Api.Commands;

/// <summary>Where an observation of an actuator's state came from.</summary>
internal enum ObservationSource
{
    /// <summary>The actuator reported it in its own telemetry.</summary>
    Telemetry,

    /// <summary>The actuator reported it when confirming a command.</summary>
    Acknowledgement
}

/// <summary>The latest state the gateway has seen an actuator in.</summary>
/// <param name="MacAddress">The actuator.</param>
/// <param name="Value">Its state: 1 for on or open, 0 for off or closed, or a dimmer's level.</param>
/// <param name="ObservedAt">When that state was observed.</param>
/// <param name="Source">Whether it came from telemetry or from an acknowledgement.</param>
internal sealed record ObservedActuatorState(string MacAddress, double Value, DateTimeOffset ObservedAt, ObservationSource Source);

/// <summary>
/// Holds the latest observed state of every actuator, so a command can record the state it replaces.
/// </summary>
/// <remarks>
/// "Observed" is the point. The gateway does not know what state an actuator is in; it only knows what the
/// actuator last <i>said</i>. An actuator that has never reported has no state here, and a command aimed at it
/// records that honestly instead of inventing one, which is what lets undo refuse instead of guess.
/// </remarks>
internal interface IActuatorStateStore
{
    /// <summary>Records an observation, unless a newer one is already held.</summary>
    void Observe(string macAddress, double value, DateTimeOffset observedAt, ObservationSource source);

    /// <summary>The latest observation for an actuator.</summary>
    bool TryGet(string macAddress, out ObservedActuatorState state);
}

internal sealed class InMemoryActuatorStateStore : IActuatorStateStore
{
    private readonly ConcurrentDictionary<string, ObservedActuatorState> _latest = new(StringComparer.Ordinal);

    public void Observe(string macAddress, double value, DateTimeOffset observedAt, ObservationSource source)
    {
        var candidate = new ObservedActuatorState(macAddress, value, observedAt, source);

        // The later observation wins, whichever arrived first. A backfilled reading processed after a live
        // one must not roll the actuator back to an older state.
        _latest.AddOrUpdate(
            macAddress,
            candidate,
            (_, existing) => observedAt >= existing.ObservedAt ? candidate : existing);
    }

    public bool TryGet(string macAddress, out ObservedActuatorState state) =>
        _latest.TryGetValue(macAddress, out state!);
}
