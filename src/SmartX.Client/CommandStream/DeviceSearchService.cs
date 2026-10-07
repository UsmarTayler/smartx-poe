using SmartX.Client.Services;
using SmartX.Client.State;
using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>
/// Device search for the Command Stream page: debounced, and kept for the life of the tab.
/// </summary>
/// <remarks>
/// <b>Debounced.</b> Every keystroke changes the filters at once, so the box always shows what was typed, but the request
/// is sent only after typing has paused for <see cref="CommandStreamOptions.SearchDebounce"/>. A newer request cancels the
/// wait of the one before it, so "pump" typed quickly is one request. This matters beyond load: the server records every
/// search it receives as a record of what the operator did, and only a search the user settled on should be in it.
/// <para>
/// <b>Answers can arrive out of order</b> (a slow search for "pu" can finish after a fast search for "pump"), so each
/// request carries a number and only the newest is allowed to set the result.
/// </para>
/// <para>
/// Scoped, with the filters in <see cref="AppState"/> and the result here, so leaving the page and coming back shows the
/// same filters and the same list at once.
/// </para>
/// </remarks>
public sealed class DeviceSearchService(IOperatorApi api, AppState state, TimeProvider time, CommandStreamOptions options)
{
    private CancellationTokenSource? _pending;
    private int _generation;
    private int _requests;

    /// <summary>Raised when the result, the searching state or the problem changes.</summary>
    public event Action? Changed;

    /// <summary>The devices that matched the filters when they were last searched, or null before the first search.</summary>
    public DeviceSearchResponse? Result { get; private set; }

    /// <summary>Whether a search is in flight.</summary>
    public bool IsSearching { get; private set; }

    /// <summary>Why the last search failed, if it did.</summary>
    public ApiProblem? Problem { get; private set; }

    /// <summary>How many searches have been sent to the API. For tests: rapid typing must be one.</summary>
    public int RequestsSent => Volatile.Read(ref _requests);

    /// <summary>
    /// The MAC addresses that match the current filters, or null when no filter is set and so everything matches.
    /// </summary>
    /// <remarks>Used by the live stream to apply the same filters as the device list.</remarks>
    public IReadOnlySet<string>? AllowedDevices { get; private set; }

    /// <summary>
    /// Sets the filters now and searches after typing pauses.
    /// </summary>
    public void Request(DeviceSearchFilters filters)
    {
        state.SetStreamFilters(filters);

        var pending = new CancellationTokenSource();
        Interlocked.Exchange(ref _pending, pending)?.Cancel();

        _ = SearchAfterDelayAsync(filters, pending.Token);
    }

    /// <summary>Searches straight away with the current filters, for the first load and the refresh button.</summary>
    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _pending, null)?.Cancel();

        await SearchAsync(state.StreamFilters, cancellationToken);
    }

    private async Task SearchAfterDelayAsync(DeviceSearchFilters filters, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(options.SearchDebounce, time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Typing carried on, and a newer request took over.
            return;
        }

        await SearchAsync(filters, cancellationToken);
    }

    private async Task SearchAsync(DeviceSearchFilters filters, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);

        IsSearching = true;
        Changed?.Invoke();

        Interlocked.Increment(ref _requests);
        var result = await api.SearchDevicesAsync(filters, cancellationToken);

        // A newer search has been sent since this one was. Its answer is the one that counts.
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        IsSearching = false;

        if (result.IsSuccess)
        {
            Result = result.Value;
            Problem = null;
            AllowedDevices = filters.IsEmpty ? null : result.Value.Devices.Select(device => device.MacAddress).ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            // The last result stays on screen, marked as stale by the problem.
            Problem = result.Problem;
        }

        Changed?.Invoke();
    }
}
