namespace SmartX.Client.CommandStream;

/// <summary>
/// Settings for the Command Stream page. Read from the <c>CommandStream</c> section of the client's
/// <c>appsettings.json</c>; every value has a default, so the section is optional.
/// </summary>
public sealed class CommandStreamOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "CommandStream";

    /// <summary>How long typing must pause before a device search is sent.</summary>
    /// <remarks>
    /// The server records every search it receives as part of the record the predictive engine will learn from, so only
    /// a search the user has settled on should reach it. "pump" typed quickly is one search, not four requests for "p",
    /// "pu", "pum" and "pump", each of which would be recorded as something the operator wanted to know.
    /// </remarks>
    public TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The most readings the live stream keeps, and the most it holds while paused.</summary>
    /// <remarks>
    /// A bound, because a stream that keeps everything it has ever received is a memory leak with a scroll bar. Past it
    /// the oldest rows fall off, which is what a ring buffer does.
    /// </remarks>
    public int StreamCapacity { get; set; } = 500;

    /// <summary>How long a critical reading stays pinned at the top of the stream if nobody acknowledges it.</summary>
    public TimeSpan PinDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The most critical readings pinned at once.</summary>
    public int MaxPinned { get; set; } = 20;

    /// <summary>How long a suggestion or insight that has just arrived is highlighted.</summary>
    /// <remarks>
    /// Long enough to be noticed by someone looking at another part of the page, short enough that it is gone before it
    /// becomes the thing they are looking at. The highlight is an outline and a word, and it does not move anything.
    /// </remarks>
    public TimeSpan ArrivalHighlight { get; set; } = TimeSpan.FromSeconds(8);
}
