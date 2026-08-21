using System.Numerics;
using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// A reading whose payload is a magnitude — a temperature, a power draw, a flow rate.
/// </summary>
/// <typeparam name="T">
/// The numeric payload type, such as <see cref="float"/>, <see cref="int"/> or
/// <see cref="decimal"/>.
/// <para>
/// <b>Why <see cref="INumber{TSelf}"/> is what allows arithmetic without boxing.</b> The problem
/// this constraint solves is that a plain <c>T</c> supports no operators at all — the compiler has
/// no reason to believe an arbitrary type can be added. Before generic math there were three ways
/// out, and all three cost something:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Store the payload as <c>object</c> and cast at each use. Every store boxes and every read
/// unboxes: one heap allocation per reading, plus a runtime type check per access.
/// </description></item>
/// <item><description>
/// Use <c>dynamic</c>. The addition is then resolved by the DLR at runtime, which boxes the
/// operands, allocates call-site caches, and moves what should be a compile-time error into
/// production.
/// </description></item>
/// <item><description>
/// Abandon generics and write a separate class per numeric type, duplicating every operator.
/// </description></item>
/// </list>
/// <para>
/// <see cref="INumber{TSelf}"/> removes the need for any of them. It is a static-abstract interface:
/// it declares that <typeparamref name="T"/> itself provides <c>+</c>, <c>-</c>, <c>&gt;</c> and the
/// rest as static members. The constraint therefore gives the compiler a static guarantee that
/// <c>left.Value + right.Value</c> is a legal operation on <typeparamref name="T"/>, and the JIT
/// emits the specialised instruction for the concrete type — a single <c>add</c> for
/// <c>NumericPacket&lt;int&gt;</c>, an <c>addss</c> for <c>NumericPacket&lt;float&gt;</c>. The
/// arithmetic happens on the stack, in registers, with no allocation and no runtime dispatch.
/// </para>
/// <para>
/// The <c>struct</c> half of the constraint is inherited from <see cref="TelemetryPacket{T}"/> and
/// still does its job: it is what stops <typeparamref name="T"/> being a reference type and forces
/// the JIT to produce a separately specialised body per payload type.
/// </para>
/// </typeparam>
/// <remarks>
/// Sealed. The operator overloads below take and return <c>NumericPacket&lt;T&gt;</c> exactly, and
/// operators are resolved statically rather than virtually — a subclass could not override them, so
/// allowing one would only create a type whose operators silently discarded its own identity.
/// </remarks>
public sealed class NumericPacket<T> : TelemetryPacket<T>
    where T : struct, INumber<T>
{
    /// <summary>Creates a numeric reading.</summary>
    /// <param name="nodeId">MAC address of the originating node.</param>
    /// <param name="value">The magnitude read.</param>
    /// <param name="timestamp">When the reading was taken.</param>
    /// <param name="category">The kind of device that produced it.</param>
    /// <param name="unit">The unit the reading is expressed in, such as "°C" or "kWh".</param>
    [JsonConstructor]
    public NumericPacket(
        string nodeId,
        T value,
        DateTimeOffset timestamp,
        SensorCategory category,
        string unit)
        : base(nodeId, value, timestamp, category, unit)
    {
    }

    /// <summary>Aggregates two readings, as in <c>Meter3 = Meter1 + Meter2</c>.</summary>
    /// <returns>
    /// A new packet holding the summed magnitude, carrying the later of the two timestamps and the
    /// left operand's category and unit.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    /// <exception cref="InvalidOperationException">The operands carry different units.</exception>
    /// <remarks>
    /// Summing across two different nodes is the intended case — total draw across a group of
    /// meters — so a node mismatch is not an error. The result is attributed to
    /// <see cref="TelemetryPacket{T}.AggregateNodeId"/> in that case rather than to either input.
    /// </remarks>
    public static NumericPacket<T> operator +(NumericPacket<T> left, NumericPacket<T> right)
    {
        EnsureCombinable(left, right, "+");

        return new NumericPacket<T>(
            CombineNodeIds(left, right),
            left.Value + right.Value,
            LaterOf(left, right),
            left.Category,
            left.Unit);
    }

    /// <summary>Takes the delta between two readings, as <c>left - right</c>.</summary>
    /// <returns>
    /// A new packet holding the difference. The result is negative when the right operand is the
    /// larger, which is meaningful — a falling temperature is a real observation, not an error.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    /// <exception cref="InvalidOperationException">The operands carry different units.</exception>
    /// <remarks>
    /// Subtraction is not commutative, so operand order is load-bearing here in a way it is not for
    /// <c>+</c>: <c>now - earlier</c> is the change since the earlier reading.
    /// </remarks>
    public static NumericPacket<T> operator -(NumericPacket<T> left, NumericPacket<T> right)
    {
        EnsureCombinable(left, right, "-");

        return new NumericPacket<T>(
            CombineNodeIds(left, right),
            left.Value - right.Value,
            LaterOf(left, right),
            left.Category,
            left.Unit);
    }

    /// <summary>Tests whether the left reading exceeds the right — a threshold comparison.</summary>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    /// <exception cref="InvalidOperationException">The operands carry different units.</exception>
    /// <remarks>
    /// Compares magnitudes only, deliberately ignoring node and timestamp: the question "is this
    /// reading above that one" is about the numbers.
    /// <para>
    /// Ordering across different units is rejected for the same reason adding across them is —
    /// asking whether 3 °C exceeds 5 kWh is not a question with an answer, so returning
    /// <see langword="false"/> would be inventing one.
    /// </para>
    /// </remarks>
    public static bool operator >(NumericPacket<T> left, NumericPacket<T> right)
    {
        EnsureCombinable(left, right, ">");
        return left.Value > right.Value;
    }

    /// <summary>Tests whether the left reading falls below the right.</summary>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    /// <exception cref="InvalidOperationException">The operands carry different units.</exception>
    /// <remarks>
    /// C# requires <c>&lt;</c> and <c>&gt;</c> to be declared as a pair, which is a sound rule
    /// rather than a formality: a type that could be ordered in one direction but not the other
    /// would be incoherent.
    /// </remarks>
    public static bool operator <(NumericPacket<T> left, NumericPacket<T> right)
    {
        EnsureCombinable(left, right, "<");
        return left.Value < right.Value;
    }

    /// <summary>Tests two packets for value equality across every field.</summary>
    /// <remarks>
    /// Note the asymmetry with <c>&gt;</c> and <c>&lt;</c>, which is intentional. Ordering two
    /// readings in different units throws, because the comparison is meaningless. Equality does not
    /// throw, because "are these the same packet" is always a legitimate question and the answer
    /// for mismatched units is simply no. An equality operator that can throw is a trap: it would
    /// fire from inside collection lookups and null checks that no caller wrote.
    /// <para>
    /// The comparison itself is inherited from
    /// <see cref="TelemetryPacket{T}.Equals(TelemetryPacket{T}?)"/> so that both packet types share
    /// one definition of equality.
    /// </para>
    /// </remarks>
    public static bool operator ==(NumericPacket<T>? left, NumericPacket<T>? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Tests two packets for inequality. The negation of <c>==</c>.</summary>
    public static bool operator !=(NumericPacket<T>? left, NumericPacket<T>? right) => !(left == right);

    /// <inheritdoc />
    /// <remarks>
    /// Declared here only because C# requires a type that overloads <c>==</c> to declare its own
    /// <see cref="object.Equals(object?)"/> override; inheriting one is not enough to satisfy the
    /// compiler. The logic stays on the base class.
    /// </remarks>
    public override bool Equals(object? obj) => base.Equals(obj);

    /// <inheritdoc />
    /// <remarks>Declared alongside <see cref="Equals(object?)"/> for the same reason.</remarks>
    public override int GetHashCode() => base.GetHashCode();
}
