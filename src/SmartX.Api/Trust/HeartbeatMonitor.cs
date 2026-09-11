using Microsoft.Extensions.Options;
using SmartX.Api.Storage;

namespace SmartX.Api.Trust;

/// <summary>
/// Periodically looks for registered nodes that have stopped reporting and penalises the silence.
/// </summary>
/// <remarks>
/// This is what makes a silent disconnect visible. Every other trust signal is driven by a packet
/// arriving, so a node that simply stops sending generates no signal at all: its score would freeze
/// at whatever it last was, and a healthy node that lost power would keep reading as healthy
/// forever. On a conventional chart the same failure looks like a line that ends — easy to miss,
/// and indistinguishable from a quiet period. Only something that runs on its own clock, and knows
/// how often each node <i>should</i> report, can turn absence into evidence.
/// <para>
/// Each sweep does two things. It applies a missed-heartbeat penalty for every whole interval a
/// sensor has been silent — using the interval from that sensor's own registration — and then
/// re-evaluates every node against the current time. Without that, evidence would only be re-weighed
/// when a packet arrived, so a node whose last good packet was an hour ago would keep showing the
/// score it earned then. Re-evaluating lets stale good evidence carry a nominal node back to
/// degrading, and keeps every reason's silence duration current.
/// </para>
/// </remarks>
internal sealed class HeartbeatMonitor(
    ISensorRegistry registry,
    ITrustEngine trust,
    IOptions<TrustOptions> options,
    TimeProvider time,
    ILogger<HeartbeatMonitor> logger) : BackgroundService
{
    private readonly TrustOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer rather than Task.Delay in a loop: a delay measured after each sweep drifts
        // by however long the sweep took, whereas the timer ticks on a fixed schedule. It is also
        // driven by the injected TimeProvider, so the schedule is controllable in tests.
        using var timer = new PeriodicTimer(_options.HeartbeatCheckInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A single failed sweep must not end the monitor. If it did, one transient fault
                    // would silently switch off disconnect detection for the rest of the process's
                    // life — which is exactly the kind of invisible failure this class exists to
                    // expose.
                    logger.LogError(exception, "Heartbeat sweep failed. The next sweep will retry.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Runs one sweep over every registered sensor.</summary>
    /// <returns>How many missed-heartbeat penalties were applied.</returns>
    /// <remarks>
    /// Separate from <see cref="ExecuteAsync"/> so the sweep can be exercised directly against a
    /// controlled clock, without waiting for a real timer to fire.
    /// </remarks>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var sensors = await registry.ListAsync(cancellationToken);
        var applied = 0;

        foreach (var sensor in sensors)
        {
            var interval = sensor.HeartbeatInterval ?? _options.DefaultHeartbeatInterval;
            applied += trust.CheckHeartbeat(sensor.MacAddress, interval);
        }

        trust.Reevaluate();

        return applied;
    }
}
