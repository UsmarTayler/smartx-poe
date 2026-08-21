using System.Numerics;
using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// An inclusive range of permitted values, such as -40 to 85 for an outdoor thermometer.
/// </summary>
/// <remarks>
/// A struct, so a zone's constraint table holds its ranges inline rather than as a field of
/// references to individually allocated objects.
/// <para>
/// The bounds are <see cref="double"/> rather than a generic <c>T</c>. A generic range would have
/// to be matched against a packet of the same <c>T</c>, which would mean a zone could not hold
/// constraints for an int meter and a float thermometer in one table without that table becoming
/// heterogeneous. Using <see cref="double"/> for the bounds keeps the table uniform, and
/// <see cref="Contains{T}(T)"/> converts the reading rather than the bound — so the payload is
/// still stored and compared in its own type. The cost is that a payload beyond 2^53 would lose
/// precision on the way into the check; no sensor reports numbers of that size, but it is the
/// honest limit of this choice.
/// </para>
/// <para>
/// <c>default(ValueRange)</c> is the range [0, 0], because every struct has a parameterless
/// constructor that cannot be suppressed. That is a valid, if narrow, range rather than a broken
/// one, so nothing downstream has to guard against it.
/// </para>
/// </remarks>
public readonly record struct ValueRange
{
    /// <summary>Creates an inclusive range.</summary>
    /// <param name="minimum">Lowest permitted value.</param>
    /// <param name="maximum">Highest permitted value, which must not be below the minimum.</param>
    /// <exception cref="ArgumentException">Either bound is NaN.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The minimum exceeds the maximum.</exception>
    [JsonConstructor]
    public ValueRange(double minimum, double maximum)
    {
        // NaN compares false against everything, so a NaN bound would silently reject every
        // reading rather than failing where the mistake was made.
        if (double.IsNaN(minimum) || double.IsNaN(maximum))
        {
            throw new ArgumentException("A value range cannot be bounded by NaN.", nameof(minimum));
        }

        if (minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimum), $"Range minimum {minimum} exceeds maximum {maximum}.");
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Lowest permitted value, inclusive.</summary>
    public double Minimum { get; }

    /// <summary>Highest permitted value, inclusive.</summary>
    public double Maximum { get; }

    /// <summary>Does this range admit the given value?</summary>
    public bool Contains(double value) => value >= Minimum && value <= Maximum;

    /// <summary>Does this range admit the given reading?</summary>
    /// <remarks>
    /// The generic overload is what keeps the no-boxing story intact at the validation boundary.
    /// <see cref="double.CreateChecked{TOther}(TOther)"/> is a static-abstract member of
    /// <see cref="INumberBase{TSelf}"/>, so the conversion is resolved at compile time for the
    /// concrete payload type — no cast through <c>object</c>, no <c>Convert.ToDouble</c>, no boxing.
    /// </remarks>
    public bool Contains<T>(T value) where T : struct, INumber<T> =>
        Contains(double.CreateChecked(value));

    /// <summary>Does this range fully enclose another?</summary>
    /// <remarks>
    /// Used when checking a sensor against a zone: the sensor's <i>entire</i> expected range has to
    /// fit inside what the zone permits. Checking only that the ranges overlap would admit a sensor
    /// that is certain to report an illegal value eventually.
    /// </remarks>
    public bool Encloses(ValueRange other) => other.Minimum >= Minimum && other.Maximum <= Maximum;

    /// <summary>Narrows this range to the part it shares with another.</summary>
    /// <param name="other">The range to intersect with.</param>
    /// <param name="intersection">The shared span, when there is one.</param>
    /// <returns><see langword="false"/> when the two ranges are disjoint.</returns>
    /// <remarks>
    /// Returns a flag rather than an empty range because the constructor refuses to build an
    /// inverted one. Two ancestor zones with disjoint ranges for the same unit is a misconfigured
    /// tree, and the caller needs to be told that rather than handed a range nothing can satisfy.
    /// </remarks>
    public bool TryIntersect(ValueRange other, out ValueRange intersection)
    {
        var low = Math.Max(Minimum, other.Minimum);
        var high = Math.Min(Maximum, other.Maximum);

        if (low > high)
        {
            intersection = default;
            return false;
        }

        intersection = new ValueRange(low, high);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"[{Minimum}, {Maximum}]";
}
