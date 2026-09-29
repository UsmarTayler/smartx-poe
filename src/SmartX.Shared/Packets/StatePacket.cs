using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// A reading from a device with two states — a switch, a relay, a valve.
/// </summary>
/// <remarks>
/// Closes <see cref="TelemetryPacket{T}"/> over <see cref="bool"/> rather than staying generic,
/// because there is only one payload type an actuator can report. Fixing it at the class level
/// means callers write <c>StatePacket</c> instead of <c>StatePacket&lt;bool&gt;</c>, and the type
/// system rules out a state packet carrying a float.
/// <para>
/// <b>Why the operator set differs from <see cref="NumericPacket{T}"/>.</b> This is the part of the
/// design that follows from the data rather than from habit. Adding two switch positions is not a
/// question anyone asks: <c>true + true</c> has no physical reading behind it, and <c>bool</c> is
/// not an <see cref="System.Numerics.INumber{TSelf}"/>, so the language would not permit it even if
/// it were wanted. What is asked of a group of actuators is whether <i>any</i> of them is engaged,
/// or whether <i>all</i> of them are — so <c>|</c> and <c>&amp;</c> are the operators that carry
/// meaning here, and they are the ones overloaded.
/// </para>
/// <para>
/// Note that <c>||</c> and <c>&amp;&amp;</c> are deliberately absent. Overloading them would require
/// also defining <c>operator true</c> and <c>operator false</c>, and their entire purpose is
/// short-circuiting — not evaluating the right operand. Both operands here are readings that have
/// already been taken, so there is nothing to skip and the short-circuit form would only imply a
/// laziness that does not exist.
/// </para>
/// </remarks>
public sealed class StatePacket : TelemetryPacket<bool>
{
    /// <summary>The unit assumed for an actuator that does not specify one.</summary>
    /// <remarks>
    /// An actuator has no physical unit in the way a thermometer does, but the base class requires
    /// one and the operators below use it to reject mismatched combinations. A named default is
    /// clearer at the call site than repeating a bare string, and it still allows a caller to
    /// distinguish, say, "open/closed" valves from "on/off" relays.
    /// </remarks>
    public const string DefaultUnit = "state";

    /// <summary>Creates an actuator reading.</summary>
    /// <param name="nodeId">MAC address of the originating node.</param>
    /// <param name="value"><see langword="true"/> when the actuator is engaged.</param>
    /// <param name="timestamp">When the reading was taken.</param>
    /// <param name="unit">
    /// What the two states represent. Defaults to <see cref="DefaultUnit"/>.
    /// </param>
    /// <remarks>
    /// No category parameter: an actuator is always <see cref="SensorCategory.Actuator"/>, so
    /// accepting one would only allow callers to state something false.
    /// </remarks>
    [JsonConstructor]
    public StatePacket(
        string nodeId,
        bool value,
        DateTimeOffset timestamp,
        string unit = DefaultUnit)
        : base(nodeId, value, timestamp, SensorCategory.Actuator, unit)
    {
    }

    /// <summary>Is any actuator in the group engaged?</summary>
    /// <returns>
    /// A packet that is engaged when either operand is, carrying the later of the two timestamps.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    /// <exception cref="InvalidOperationException">The operands represent different states.</exception>
    /// <remarks>
    /// Returns a <see cref="StatePacket"/> rather than a bare <see cref="bool"/> so that the result
    /// can be combined again — <c>a | b | c</c> reduces a whole group — and so the combined reading
    /// keeps its timestamp. Returning <c>bool</c> would discard when the group was last observed,
    /// which is the difference between "the pump is running" and "the pump was running at 14:03".
    /// </remarks>
    public static StatePacket operator |(StatePacket left, StatePacket right)
    {
        EnsureCombinable(left, right, "|");

        return new StatePacket(
            CombineNodeIds(left, right),
            left.Value || right.Value,
            LaterOf(left, right),
            left.Unit);
    }

    /// <summary>Is every actuator in the group engaged?</summary>
    /// <returns>
    /// A packet that is engaged only when both operands are, carrying the later of the two
    /// timestamps.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    /// <exception cref="InvalidOperationException">The operands represent different states.</exception>
    /// <remarks>
    /// The interlock case: a process may only run when every safety switch in the group reports
    /// engaged.
    /// </remarks>
    public static StatePacket operator &(StatePacket left, StatePacket right)
    {
        EnsureCombinable(left, right, "&");

        return new StatePacket(
            CombineNodeIds(left, right),
            left.Value && right.Value,
            LaterOf(left, right),
            left.Unit);
    }

    /// <summary>Tests two packets for value equality across every field.</summary>
    /// <remarks>
    /// Not required by the brief, but present so the two packet types behave alike. A codebase where
    /// <c>numericA == numericB</c> compares readings while <c>stateA == stateB</c> quietly compares
    /// references is a codebase with a bug waiting in it. The comparison itself is inherited from
    /// <see cref="TelemetryPacket{T}.Equals(TelemetryPacket{T}?)"/>.
    /// </remarks>
    public static bool operator ==(StatePacket? left, StatePacket? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Tests two packets for inequality. The negation of <c>==</c>.</summary>
    public static bool operator !=(StatePacket? left, StatePacket? right) => !(left == right);

    /// <inheritdoc />
    /// <remarks>
    /// Declared here only because C# requires a type that overloads <c>==</c> to declare its own
    /// override; inheriting one does not satisfy the compiler. The logic stays on the base class.
    /// </remarks>
    public override bool Equals(object? obj) => base.Equals(obj);

    /// <inheritdoc />
    /// <remarks>Declared alongside <see cref="Equals(object?)"/> for the same reason.</remarks>
    public override int GetHashCode() => base.GetHashCode();

    /// <inheritdoc />
    /// <remarks>
    /// A state is engaged or not. Reporting it as 1 and 0 would let a numeric threshold be compared against
    /// it, and "humidity below 35" has no meaning for a door contact, so this reports that there is no
    /// number rather than inventing one.
    /// </remarks>
    public override bool TryGetNumericValue(out double value)
    {
        value = 0;

        return false;
    }
}
