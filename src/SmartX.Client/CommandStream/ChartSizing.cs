using MudBlazor;

namespace SmartX.Client.CommandStream;

/// <summary>Tells the page how wide its chart will be drawn.</summary>
public interface IChartSizing
{
    /// <summary>The chart's width in pixels, estimated from the window.</summary>
    Task<int> WidthAsync();
}

/// <summary>
/// How wide a chart is, and how many points it is worth asking the server for.
/// </summary>
/// <remarks>
/// The timeline request carries <c>maxPoints</c>, and there is no point asking for more points than the chart has
/// pixels to show them in. A phone gets a few dozen buckets, and a wide desktop a couple of hundred, so the response and
/// the drawing are sized to the screen and not to the largest one.
/// <para>
/// The width is worked out from the window width and the layout rules, not measured from the element. Measuring would
/// need script, and the chart is meant to be plain Blazor and SVG. The estimate only has to be right to within a
/// column's width, because the SVG itself scales to whatever it is given.
/// </para>
/// </remarks>
public static class ChartSizes
{
    /// <summary>The chart's width for a window width, following the page's column rules.</summary>
    public static int WidthFor(int windowWidth)
    {
        // The drawer sits beside the content from 960 px up and over it below, and the page has its own gutters.
        var content = windowWidth >= 960 ? windowWidth - 240 - 48 : windowWidth - 32;

        // The page is one column when narrow, two from about 560 px of content, three from about 900 px.
        double column = content >= 900 ? content * 0.36 : content >= 560 ? content * 0.48 : content;

        return (int)Math.Clamp(column - 32, 260, 720);
    }

    /// <summary>How many buckets a chart of that width can usefully show: about one per three pixels.</summary>
    public static int MaxPointsFor(int chartWidth) => Math.Clamp(chartWidth / 3, 40, 240);
}

/// <summary><see cref="IChartSizing"/> from the browser's window size, through MudBlazor's own viewport service.</summary>
public sealed class BrowserChartSizing(IBrowserViewportService viewport) : IChartSizing
{
    public async Task<int> WidthAsync()
    {
        var window = await viewport.GetCurrentBrowserWindowSizeAsync();

        return ChartSizes.WidthFor(window.Width);
    }
}
