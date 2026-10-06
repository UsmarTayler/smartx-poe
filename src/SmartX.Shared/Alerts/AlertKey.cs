using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>What an alert is about.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertKind>))]
public enum AlertKind
{
    /// <summary>The node has stopped reporting.</summary>
    Disconnected,

    /// <summary>The node sent readings outside the range it was registered with.</summary>
    OutOfRange,

    /// <summary>The node sent readings of the wrong type.</summary>
    TypeViolation,

    /// <summary>The node's trust fell into the fault band.</summary>
    Fault,

    /// <summary>A command to the node was never collected or never confirmed.</summary>
    CommandTimedOut
}

/// <summary>
/// Identifies one alert: a kind of trouble on one node.
/// </summary>
/// <param name="Mac">The node's canonical MAC address.</param>
/// <param name="Kind">What kind of trouble.</param>
/// <remarks>
/// <b>Why a <c>readonly record struct</c>.</b> This is the element type of a <see cref="HashSet{T}"/>, and a
/// set is only as good as its idea of "the same". A plain class compares by reference, so two
/// <c>new AlertKey("AA:...", Disconnected)</c> would be different objects and every repeat of an alert would
/// look new — the set would hold a hundred copies of one problem and suppress nothing. A record gives value
/// equality and a matching hash code for free, comparing both members, so the second construction of the same
/// key finds the first.
/// <para>
/// A struct rather than a record class because the key is created on every reading that might raise an alert
/// and thrown away again: a struct lives on the stack and costs no allocation. The synthesised equality is
/// <see cref="IEquatable{T}"/>, which <see cref="HashSet{T}"/> uses directly, so looking a key up does not box it.
/// </para>
/// </remarks>
public readonly record struct AlertKey(string Mac, AlertKind Kind)
{
    /// <summary>A readable form for logs and history: "Disconnected on AA:BB:...".</summary>
    public override string ToString() => $"{Kind} on {Mac}";
}
