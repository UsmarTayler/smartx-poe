using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SmartX.Shared;

/// <summary>
/// Parses and canonicalises the MAC addresses that identify nodes.
/// </summary>
/// <remarks>
/// A MAC arrives as text and is used as a dictionary key, so it has to be reduced to one canonical
/// form before it is stored. Without that, "aa:bb:cc:dd:ee:01" and "AA-BB-CC-DD-EE-01" register as
/// two different sensors that are physically the same device.
/// <para>
/// The pattern is compiled by the source generator rather than at runtime.
/// <see cref="GeneratedRegexAttribute"/> emits the matching code at build time, so there is no
/// regex parsing or IL emission on first use and the cost of the check is a plain method call.
/// </para>
/// </remarks>
public static partial class MacAddressValidator
{
    /// <summary>
    /// Six hex pairs separated consistently by either colons or hyphens.
    /// </summary>
    /// <remarks>
    /// The two separators are spelled as separate alternatives rather than as a character class so
    /// that a mixed form like "AA:BB-CC:DD:EE:FF" is rejected. A character class would accept it,
    /// and an address written that way is far more likely to be a corrupted string than a real
    /// device.
    /// </remarks>
    [GeneratedRegex(
        @"^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$|^(?:[0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MacPattern();

    /// <summary>Is this a well-formed, usable MAC address?</summary>
    public static bool IsValid([NotNullWhen(true)] string? candidate) =>
        TryNormalise(candidate, out _);

    /// <summary>
    /// Reduces a MAC address to uppercase, colon-separated form.
    /// </summary>
    /// <param name="candidate">The address as it arrived.</param>
    /// <param name="normalised">The canonical form, when the input is valid.</param>
    /// <returns><see langword="false"/> when the input is malformed or reserved.</returns>
    public static bool TryNormalise(
        [NotNullWhen(true)] string? candidate,
        [NotNullWhen(true)] out string? normalised)
    {
        normalised = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim();
        if (!MacPattern().IsMatch(trimmed))
        {
            return false;
        }

        var canonical = trimmed.Replace('-', ':').ToUpperInvariant();

        // The all-zero address is reserved: it is what a packet combining readings from two
        // different nodes is attributed to. Letting a real sensor claim it would make an aggregate
        // indistinguishable from a device.
        if (string.Equals(canonical, TelemetryPacket<int>.AggregateNodeId, StringComparison.Ordinal))
        {
            return false;
        }

        normalised = canonical;
        return true;
    }

    /// <summary>Reduces a MAC address to canonical form, or throws.</summary>
    /// <exception cref="ArgumentException">The address is malformed or reserved.</exception>
    public static string Normalise(string? candidate) =>
        TryNormalise(candidate, out var normalised)
            ? normalised
            : throw new ArgumentException(
                $"'{candidate}' is not a usable MAC address. Expected six hex pairs separated by " +
                "colons or hyphens, and not the reserved all-zero address.",
                nameof(candidate));
}
