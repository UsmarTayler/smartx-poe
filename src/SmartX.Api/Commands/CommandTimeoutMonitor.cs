using Microsoft.Extensions.Options;

namespace SmartX.Api.Commands;

/// <summary>
/// Notices commands that nobody collected or confirmed, and times them out.
/// </summary>
/// <remarks>
/// A command to a device that is off, asleep or out of range simply sits there: nothing arrives to say it
/// failed, because nothing is there to say anything. Only something that runs on its own clock can turn that
/// silence into a visible result, which is the same reason the heartbeat monitor exists for telemetry. Send a
/// command to a silent device and, after the pending timeout, it becomes <c>TimedOut</c> on its own.
/// </remarks>
internal sealed class CommandTimeoutMonitor(
    CommandService commands,
    IOptions<CommandOptions> options,
    TimeProvider time,
    ILogger<CommandTimeoutMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer driven by the injected clock, like the heartbeat monitor, so the schedule can be
        // controlled in a test.
        using var timer = new PeriodicTimer(options.Value.SweepInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (commands.TimeOutStale() is > 0 and var count)
                    {
                        logger.LogInformation("{Count} command(s) timed out.", count);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One failed sweep must not end the monitor, or every later command would wait for ever.
                    logger.LogError(exception, "Command timeout sweep failed. The next sweep will retry.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
