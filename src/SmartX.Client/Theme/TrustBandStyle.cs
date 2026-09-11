using MudBlazor;
using SmartX.Shared;

namespace SmartX.Client.Theme;

/// <summary>
/// How a trust band is shown: a colour, an icon and a word, always all three.
/// </summary>
/// <param name="Colour">The band's status colour, identical in both themes.</param>
/// <param name="Icon">A shape that identifies the band without its colour.</param>
/// <param name="Label">The band's name, in words.</param>
/// <remarks>
/// <b>Colour is never the only encoding — a deliberate accessibility decision.</b> Roughly 8% of men
/// have some form of colour vision deficiency, and for the most common kinds the green and the red
/// here are hard to tell apart. Contrast is a problem too: measured against white, the degrading
/// yellow #fab219 is about 1.8:1 and the nominal green #0ca30c only about 3.4:1; against the dark
/// theme's surface the unproven grey #6b6a66 falls to about 3:1. None of those is safe to rely on as
/// the sole signal, so every status also carries a distinct icon shape — check, question mark,
/// triangle, cross — and its name in text rendered in the theme's body colour, which always passes.
/// The colour then reinforces a meaning that is already there, instead of carrying it alone.
/// <para>
/// The hexes are fixed rather than taken from the theme, so a fault looks the same to an operator
/// whichever theme is active.
/// </para>
/// </remarks>
public readonly record struct TrustBandStyle(string Colour, string Icon, string Label)
{
    public static TrustBandStyle For(TrustBand band) => band switch
    {
        TrustBand.Nominal => new("#0ca30c", Icons.Material.Filled.CheckCircle, "Nominal"),
        TrustBand.Unproven => new("#6b6a66", Icons.Material.Filled.Help, "Unproven"),
        TrustBand.Degrading => new("#fab219", Icons.Material.Filled.Warning, "Degrading"),
        TrustBand.Fault => new("#d03b3b", Icons.Material.Filled.Error, "Fault"),
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Unknown trust band.")
    };

    /// <summary>Every band, most urgent first, in the order the shared formula defines.</summary>
    public static IReadOnlyList<TrustBand> InSeverityOrder { get; } =
        [.. Enum.GetValues<TrustBand>().OrderBy(TrustFormula.Severity)];
}
