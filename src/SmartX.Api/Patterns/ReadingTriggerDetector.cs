using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Patterns;

/// <summary>
/// Turns a stream of readings into triggers: crossings of a sensor's watch levels, and the start of a critical episode.
/// </summary>
/// <remarks>
/// <b>Watch levels come from the sensor's registered range.</b> "Moisture Sensor B drops below 20%" needs a notion of 20%,
/// and the registration already says what range the sensor reports in, so the low level is 20% of the way up that range and
/// the high level 80% of the way, both configurable. A sensor registered for 0 to 100 has a low level of 20; one registered
/// for 0 to 50 has one of 10. Nothing has to be configured per sensor.
/// <para>
/// <b>Hysteresis.</b> A reading hovering around a level would cross it on every packet and produce a trigger for each. So a
/// crossing fires once and the level is then disarmed, and it is re-armed only when the reading has come back past the level
/// by a margin (below 20% fires, and it is not armed again until the reading is above 25%). A reading that wobbles between
/// 19 and 22 is one trigger, not a dozen. This is how real alarm systems avoid flapping, and without it the engine would
/// count one dip as a dozen separate events and learn from the noise.
/// </para>
/// <para>
/// <b>A critical episode is one trigger.</b> A device in a critical state reports critical readings poll after poll. A set of
/// the devices currently critical makes the first one a trigger and the rest nothing, because <c>Add</c> on a member already
/// present returns false, and a non-critical reading removes the device so the next episode counts again.
/// </para>
/// One lock, held for a few dictionary lookups; this runs on the ingestion worker's thread for every reading.
/// </remarks>
internal sealed class ReadingTriggerDetector(IDeviceDirectory devices, IOptions<PatternOptions> options)
{
    private readonly Lock _gate = new();
    private readonly PatternOptions _options = options.Value;

    // Per sensor: whether each watch level has fired and not yet been re-armed.
    private readonly Dictionary<string, WatchState> _watch = new(StringComparer.Ordinal);

    // Devices in a critical episode right now. A set, so a repeat is recognised by Add returning false.
    private readonly HashSet<string> _critical = new(StringComparer.Ordinal);

    /// <summary>Looks at one numeric reading and returns the trigger it makes, if any.</summary>
    public TriggerEvent? ObserveReading(string macAddress, double value, DateTimeOffset at)
    {
        if (!devices.TryDescribe(macAddress, out var profile) || profile.IsState)
        {
            return null;
        }

        var span = profile.RangeMaximum - profile.RangeMinimum;

        if (span <= 0 || double.IsNaN(value))
        {
            return null;
        }

        var lowLevel = profile.RangeMinimum + _options.WatchLowFraction * span;
        var lowRearm = profile.RangeMinimum + (_options.WatchLowFraction + _options.RearmMargin) * span;
        var highLevel = profile.RangeMinimum + _options.WatchHighFraction * span;
        var highRearm = profile.RangeMinimum + (_options.WatchHighFraction - _options.RearmMargin) * span;

        lock (_gate)
        {
            ref var state = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_watch, macAddress, out _);

            if (state.LowFired)
            {
                // Re-armed only once the reading is back above the level by the margin, not the moment it touches it.
                state.LowFired = value <= lowRearm;
            }
            else if (value < lowLevel)
            {
                state.LowFired = true;

                return new TriggerEvent(TriggerKind.ThresholdCrossing, macAddress, "Low", at);
            }

            if (state.HighFired)
            {
                state.HighFired = value >= highRearm;
            }
            else if (value > highLevel)
            {
                state.HighFired = true;

                return new TriggerEvent(TriggerKind.ThresholdCrossing, macAddress, "High", at);
            }

            return null;
        }
    }

    /// <summary>Looks at whether a reading was critical and returns a trigger for the first of an episode.</summary>
    /// <param name="macAddress">The device.</param>
    /// <param name="reason">Why the reading was critical, or null when it was not.</param>
    /// <param name="at">When it was processed.</param>
    public TriggerEvent? ObserveCritical(string macAddress, string? reason, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (reason is null)
            {
                _critical.Remove(macAddress);

                return null;
            }

            return _critical.Add(macAddress)
                ? new TriggerEvent(TriggerKind.CriticalReading, macAddress, reason, at)
                : null;
        }
    }

    /// <summary>Whether each watch level of one sensor has fired and is waiting to be re-armed.</summary>
    private struct WatchState
    {
        public bool LowFired;
        public bool HighFired;
    }
}
