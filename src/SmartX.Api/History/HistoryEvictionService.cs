using Microsoft.Extensions.Options;

namespace SmartX.Api.History;

/// <summary>
/// Removes history that has aged out of the retention window, on a timer.
/// </summary>
/// <remarks>
/// A background service and not part of <see cref="IHistoryStore.Append"/>: see
/// <see cref="HistoryOptions.EvictionInterval"/>. Driven by the injected <see cref="TimeProvider"/>, like the
/// heartbeat monitor, so retention can be tested against a clock the test controls.
/// </remarks>
internal sealed class HistoryEvictionService(
    IHistoryStore store,
    IOptions<HistoryOptions> options,
    TimeProvider time,
    ILogger<HistoryEvictionService> logger) : BackgroundService
{
    private readonly HistoryOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.EvictionInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Evict();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A failed pass must not end the service, or history would grow without bound for the
                    // rest of the process's life with nothing to say so.
                    logger.LogError(exception, "History eviction failed. The next pass will retry.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Runs one pass. Internal so a test can drive it without waiting on the timer.</summary>
    /// <returns>How many entries were removed.</returns>
    internal int Evict()
    {
        var removed = store.EvictOlderThan(time.GetUtcNow() - _options.RetentionWindow);

        if (removed > 0)
        {
            logger.LogDebug("History retention removed {Count} entries older than {Window}.", removed, _options.RetentionWindow);
        }

        return removed;
    }
}
