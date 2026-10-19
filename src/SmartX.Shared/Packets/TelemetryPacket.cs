namespace SmartX.Shared;

/// <summary>
/// A single reading from one node, carrying a payload of type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">
/// The payload type, constrained to <see langword="struct"/>, which is why no boxing occurs.
/// <para>
/// <b>Why <c>where T : struct</c> prevents boxing.</b> The naive design is one <c>object Value</c> field, and assigning a
/// <see cref="float"/> to it <i>boxes</i> it: a heap allocation per reading, and an unbox plus a type check to read it back. A
/// generic <c>T</c> avoids that because the JIT compiles a separate version of this class for each value type, so
/// <c>TelemetryPacket&lt;float&gt;</c> has a 4-byte float laid out inline and there is no <c>object</c> to box into.
/// </para>
/// <para>
/// The <c>struct</c> constraint makes that a guarantee and not a habit: without it <c>T</c> could be a reference type, the JIT
/// would share one implementation across them, and the compiler would accept <c>TelemetryPacket&lt;string&gt;</c>.
/// </para>
/// </typeparam>
/// <remarks>
/// Abstract because a reading with no semantics does not exist: a payload is either a magnitude that can be aggregated
/// (<see cref="NumericPacket{T}"/>) or a state that can be combined logically (<see cref="StatePacket"/>). The base holds the
/// metadata every packet shares, the equality contract, and the guards the operators need.
/// <para>
/// Packets are immutable: a reading is a historical fact, and immutability lets the operators return new packets with no risk of
/// aliasing an operand.
/// </para>
/// </remarks>
public abstract class TelemetryPacket<T> : ITelemetryPacket, IEquatable<TelemetryPacket<T>>
    where T : struct
{
    /// <summary>
    /// The node identifier given to a packet produced by combining two packets from different nodes.
    /// </summary>
    /// <remarks>
    /// An all-zero MAC rather than a word like "aggregate", so the field only ever holds something
    /// shaped like a MAC address. Anything parsing <see cref="NodeId"/> keeps working, and the value
    /// is still obviously not a real device.
    /// </remarks>
    public const string AggregateNodeId = "00:00:00:00:00:00";

    /// <summary>Initialises the metadata shared by every packet.</summary>
    /// <param name="nodeId">MAC address of the originating node.</param>
    /// <param name="value">The reading itself.</param>
    /// <param name="timestamp">When the reading was taken.</param>
    /// <param name="category">The kind of device that produced it.</param>
    /// <param name="unit">The unit the reading is expressed in, such as "°C" or "kWh".</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="nodeId"/> or <paramref name="unit"/> is null, empty or whitespace. A reading
    /// with no unit cannot be safely combined with anything, so it is rejected at construction
    /// rather than being allowed to fail later inside an operator.
    /// </exception>
    protected TelemetryPacket(
        string nodeId,
        T value,
        DateTimeOffset timestamp,
        SensorCategory category,
        string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        NodeId = nodeId;
        Value = value;
        Timestamp = timestamp;
        Category = category;
        Unit = unit;
    }

    /// <summary>MAC address of the node that produced this reading.</summary>
    public string NodeId { get; }

    /// <summary>
    /// The reading. Stored in its natural layout as a <typeparamref name="T"/>, never behind an
    /// <see cref="object"/> reference.
    /// </summary>
    public T Value { get; }

    /// <summary>When the reading was taken.</summary>
    /// <remarks>
    /// <see cref="DateTimeOffset"/> rather than <see cref="DateTime"/>: nodes in different places
    /// report in different local times, and only an offset-bearing timestamp orders those readings
    /// unambiguously.
    /// </remarks>
    public DateTimeOffset Timestamp { get; }

    /// <summary>The kind of device that produced this reading.</summary>
    public SensorCategory Category { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Abstract rather than implemented here, because whether a payload has a numeric reading at all
    /// depends on what <typeparamref name="T"/> is, and only the concrete packet knows that.
    /// </remarks>
    public abstract bool TryGetNumericValue(out double value);

    /// <summary>The unit the reading is expressed in, such as "°C" or "kWh".</summary>
    public string Unit { get; }

    /// <summary>
    /// Rejects an operation between two packets that are not measuring the same kind of thing.
    /// </summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <param name="op">The operator symbol, used in the exception message.</param>
    /// <exception cref="ArgumentNullException">Either operand is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The operands carry different units, or come from different sensor categories.
    /// </exception>
    /// <remarks>
    /// Two nodes reporting in kWh may be combined freely — that is the smart-meter aggregation the
    /// brief calls for, and differing node identifiers are expected there. Two nodes reporting in
    /// <i>different units</i> may not: 3 °C combined with 5 kWh has no meaning, and quietly
    /// returning 8 would be worse than failing, because a wrong number propagates silently while an
    /// exception does not.
    /// <para>
    /// The category is checked as well as the unit, because the unit alone is not enough to make
    /// two readings comparable. Relative humidity and a battery charge level are both reported in
    /// "%", and summing them produces a number with no physical meaning whatsoever. The unit says
    /// how a magnitude is scaled; the category says what is being measured. Both have to agree
    /// before an arithmetic or ordering operation means anything.
    /// </para>
    /// </remarks>
    protected static void EnsureCombinable(TelemetryPacket<T> left, TelemetryPacket<T> right, string op)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Category != right.Category)
        {
            throw new InvalidOperationException(
                $"Cannot apply '{op}' to readings from different sensor categories " +
                $"('{left.Category}' and '{right.Category}').");
        }

        if (!string.Equals(left.Unit, right.Unit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot apply '{op}' to readings in different units ('{left.Unit}' and '{right.Unit}').");
        }
    }

    /// <summary>
    /// Works out the node identifier for a packet derived from two others.
    /// </summary>
    /// <returns>
    /// The shared node identifier when both operands came from the same node, otherwise
    /// <see cref="AggregateNodeId"/>.
    /// </returns>
    /// <remarks>
    /// Combining two readings from one node produces a value still attributable to that node — a
    /// delta between successive readings, for instance. Combining across nodes does not, and
    /// keeping the left operand's identifier would make the result look like it came from a single
    /// device when it did not.
    /// </remarks>
    protected static string CombineNodeIds(TelemetryPacket<T> left, TelemetryPacket<T> right) =>
        string.Equals(left.NodeId, right.NodeId, StringComparison.OrdinalIgnoreCase)
            ? left.NodeId
            : AggregateNodeId;

    /// <summary>
    /// Works out the timestamp for a packet derived from two others.
    /// </summary>
    /// <returns>The later of the two timestamps.</returns>
    /// <remarks>
    /// The combined reading could not be formed until both operands existed, so the later timestamp
    /// is the moment the derived value came into being. Taking the earlier one would claim the
    /// result was known before one of its inputs had been measured.
    /// </remarks>
    protected static DateTimeOffset LaterOf(TelemetryPacket<T> left, TelemetryPacket<T> right) =>
        left.Timestamp >= right.Timestamp ? left.Timestamp : right.Timestamp;

    /// <summary>Compares this packet with another for value equality across every field.</summary>
    /// <remarks>
    /// Lives on the base so both subtypes share one definition of equality and cannot drift apart.
    /// <para>
    /// Equality covers the metadata as well as the reading. Two nodes that both report 21.5 °C have
    /// not reported the same packet; they have reported the same number. Conflating those would make
    /// a packet useless as a key or as a set member.
    /// </para>
    /// <para>
    /// <see cref="EqualityComparer{T}.Default"/> compares the payloads without boxing: for a value
    /// type it resolves to a specialised comparer whose <c>Equals</c> takes <typeparamref name="T"/>
    /// by value, so no heap allocation occurs on the comparison path.
    /// </para>
    /// </remarks>
    public bool Equals(TelemetryPacket<T>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // A NumericPacket and a StatePacket are never equal even if every field matches, so the
        // runtime type is part of the comparison.
        if (GetType() != other.GetType())
        {
            return false;
        }

        return string.Equals(NodeId, other.NodeId, StringComparison.OrdinalIgnoreCase)
            && EqualityComparer<T>.Default.Equals(Value, other.Value)
            && Timestamp == other.Timestamp
            && Category == other.Category
            && string.Equals(Unit, other.Unit, StringComparison.Ordinal);
    }

    /// <summary>Compares this packet with an arbitrary reference for value equality.</summary>
    /// <remarks>
    /// The <see cref="object"/> parameter is imposed by <see cref="object.Equals(object?)"/> and
    /// cannot be avoided. It carries no payload: the reference is narrowed immediately and the
    /// comparison is done by the strongly-typed overload above. No <typeparamref name="T"/> is ever
    /// boxed by this path.
    /// </remarks>
    public override bool Equals(object? obj) => Equals(obj as TelemetryPacket<T>);

    /// <summary>Hash code consistent with <see cref="Equals(TelemetryPacket{T}?)"/>.</summary>
    /// <remarks>
    /// Combines exactly the fields equality compares, which is what keeps a packet usable as a
    /// dictionary key. <see cref="HashCode.Combine{T1, T2, T3, T4, T5}"/> is generic, so the payload
    /// is passed by value rather than boxed on its way in.
    /// </remarks>
    public override int GetHashCode() => HashCode.Combine(NodeId, Value, Timestamp, Category, Unit);

    /// <summary>Renders the packet as "value unit @ node", for logs and debugger display.</summary>
    public override string ToString() => $"{Value} {Unit} @ {NodeId}";
}
