using SmartX.Shared;

namespace SmartX.Client.State;

/// <summary>
/// Client-side state that has to outlive any single page.
/// </summary>
/// <remarks>
/// <b>Why a container rather than component fields.</b> Blazor destroys a page's component when the
/// user navigates away, and every field on it goes with it. Anything held here instead survives:
/// select a node on the trust panel, visit the sensors page, come back, and the node is still
/// selected and the filters still set. That is the groundwork for Part 2's asynchronous navigation
/// between views without losing application state.
/// <para>
/// Registered as scoped. In Blazor WebAssembly a scope lasts as long as the browser tab, so in
/// practice there is one instance for the life of the app — but scoped rather than singleton keeps
/// the registration correct if this code is ever hosted server-side, where a singleton would be
/// shared between every connected user.
/// </para>
/// <para>
/// State only changes through methods, never setters, and every change raises <see cref="Changed"/>.
/// A public setter would let a component write a value and forget to notify, leaving every other
/// component showing the old one.
/// </para>
/// </remarks>
public sealed class AppState
{
    private readonly HashSet<TrustBand> _hiddenBands = [];

    /// <summary>Raised after any state change, so subscribed components can re-render.</summary>
    public event Action? Changed;

    /// <summary>The chosen theme, or null until the operating system's preference has been read.</summary>
    public bool? IsDarkMode { get; private set; }

    /// <summary>Whether the navigation drawer is open.</summary>
    public bool DrawerOpen { get; private set; } = true;

    /// <summary>The node the user last selected, by MAC address.</summary>
    public string? SelectedNode { get; private set; }

    /// <summary>Free-text filter over the trust list.</summary>
    public string TrustSearch { get; private set; } = string.Empty;

    /// <summary>Bands the user has hidden from the trust list.</summary>
    public IReadOnlySet<TrustBand> HiddenBands => _hiddenBands;

    /// <summary>When trust data last arrived from the API.</summary>
    public DateTimeOffset? LastRefresh { get; private set; }

    public void SetDarkMode(bool isDarkMode) => Set(IsDarkMode, isDarkMode, value => IsDarkMode = value);

    public void SetDrawerOpen(bool open) => Set(DrawerOpen, open, value => DrawerOpen = value);

    public void SelectNode(string? macAddress) => Set(SelectedNode, macAddress, value => SelectedNode = value);

    public void SetTrustSearch(string? text) =>
        Set(TrustSearch, text?.Trim() ?? string.Empty, value => TrustSearch = value);

    public void MarkRefreshed(DateTimeOffset at) => Set(LastRefresh, at, value => LastRefresh = value);

    /// <summary>Hides a band from the trust list, or shows it again if it was hidden.</summary>
    public void ToggleBand(TrustBand band)
    {
        if (!_hiddenBands.Remove(band))
        {
            _hiddenBands.Add(band);
        }

        Changed?.Invoke();
    }

    /// <summary>Shows every band and clears the search text.</summary>
    public void ClearTrustFilters()
    {
        _hiddenBands.Clear();
        TrustSearch = string.Empty;
        Changed?.Invoke();
    }

    /// <summary>Applies a change and notifies, but only if the value actually changed.</summary>
    /// <remarks>
    /// Generic so the comparison runs through <see cref="EqualityComparer{T}.Default"/> without
    /// boxing a bool or a timestamp. Skipping no-op changes matters because the trust feed marks a
    /// refresh every few seconds, and re-rendering every subscriber when nothing moved is waste.
    /// </remarks>
    private void Set<T>(T current, T next, Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(current, next))
        {
            return;
        }

        apply(next);
        Changed?.Invoke();
    }
}
