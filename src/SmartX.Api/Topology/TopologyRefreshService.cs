using Microsoft.Extensions.Options;

namespace SmartX.Api.Topology;

/// <summary>
/// Rebuilds the mesh snapshot on a timer, so a route that changes is noticed when it changes and not when someone next looks.
/// </summary>
/// <remarks>
/// The snapshot is otherwise rebuilt lazily, by a read. A device can go stale or fall into Fault with nobody reading, and then the
/// route change would be stamped with the time of the next read, possibly minutes later. Reading <see cref="TopologyService.Current"/>
/// is all it takes: the service rebuilds if the snapshot is out of date and records what changed. A zero interval turns it off.
/// </remarks>
internal sealed class TopologyRefreshService(TopologyService topology, TimeProvider time, IOptions<TopologyOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.RefreshInterval;

        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _ = topology.Current;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
