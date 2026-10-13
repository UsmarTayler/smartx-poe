using MudBlazor;

namespace SmartX.Client.Theme;

/// <summary>
/// The application's light and dark themes.
/// </summary>
/// <remarks>
/// <b>Two designed palettes, not one palette inverted.</b> Flipping a light theme's colours produces
/// a dark theme with the wrong properties: shadows stop reading on a dark background, the brand blue
/// that passes contrast on white falls below 3:1 on near-black, and pure white text on pure black
/// halates for many readers. Each palette here was chosen for its own background.
/// <list type="bullet">
/// <item><description>
/// <b>Light</b> is a working-hours control-room palette: a near-white page with pure white cards, so
/// panels separate from the page by a faint tint rather than heavy shadow, and a deep navy app bar
/// that anchors the layout. Body text is a blue-black (#1F2933, about 14:1 on white) rather than
/// #000, which is softer over long sessions without costing legibility.
/// </description></item>
/// <item><description>
/// <b>Dark</b> is for low-light monitoring. Elevation is expressed by lightness instead of shadow —
/// cards (#1B2129) are lighter than the page (#12161C), the convention dark interfaces use because a
/// shadow on black is invisible. The primary blue is lightened to #7FB2E5 so it keeps about 7.9:1
/// against the card surface, and text is an off-white (#E6EAF0) rather than #FFF to avoid halation.
/// </description></item>
/// </list>
/// <para>
/// The status colours for trust bands are deliberately <i>not</i> part of either palette: they are
/// fixed across both themes so that a fault looks the same to an operator whichever theme is on.
/// </para>
/// </remarks>
public static class SmartXTheme
{
    /// <summary>The theme passed to MudBlazor's theme provider.</summary>
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2F5D8A",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#52606D",
            Info = "#2F5D8A",
            // Darker than the dark theme's: the brighter green and amber are fine on a dark surface and fail 4.5:1 as text on
            // a light one (outlined chips and alerts draw their text in these colours).
            Success = "#0A7A0A",
            Warning = "#8A5A00",
            Error = "#D03B3B",
            Background = "#F6F7F9",
            BackgroundGray = "#EDEFF2",
            Surface = "#FFFFFF",
            AppbarBackground = "#1F3A56",
            AppbarText = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#1F2933",
            DrawerIcon = "#52606D",
            TextPrimary = "#1F2933",
            TextSecondary = "#52606D",
            LinesDefault = "#E1E5EA",
            TableLines = "#E1E5EA",
            Divider = "#E1E5EA",
            ActionDefault = "#52606D",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#7FB2E5",
            PrimaryContrastText = "#0B1B2B",
            Secondary = "#9AA5B1",
            Info = "#7FB2E5",
            Success = "#0CA30C",
            Warning = "#FAB219",
            Error = "#D03B3B",
            Background = "#12161C",
            BackgroundGray = "#161B22",
            Surface = "#1B2129",
            AppbarBackground = "#161B22",
            AppbarText = "#E6EAF0",
            DrawerBackground = "#161B22",
            DrawerText = "#C9D1D9",
            DrawerIcon = "#9AA5B1",
            TextPrimary = "#E6EAF0",
            TextSecondary = "#9AA5B1",
            LinesDefault = "#2A323C",
            TableLines = "#2A323C",
            Divider = "#2A323C",
            ActionDefault = "#9AA5B1",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",

            // Wide enough that the three pillar names read in full on two lines at most.
            DrawerWidthLeft = "280px",
        },
    };
}
