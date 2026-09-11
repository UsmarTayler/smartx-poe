using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.Trust;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>A clock that only moves when a test moves it.</summary>
/// <remarks>
/// Trust depends on time twice over — decay and heartbeats — so tests against the real clock would
/// either take minutes or depend on timing. This makes "an hour later" a single line and makes
/// every score exactly reproducible.
/// </remarks>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Builds a trust engine against a manual clock.</summary>
internal static class TrustTestHost
{
    internal const string NodeA = "AA:BB:CC:00:00:01";
    internal const string NodeB = "AA:BB:CC:00:00:02";
    internal const string NodeC = "AA:BB:CC:00:00:03";

    internal static readonly DateTimeOffset Start = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    internal static (TrustEngine Engine, ManualTimeProvider Clock) Create(Action<TrustOptions>? configure = null)
    {
        var options = new TrustOptions();
        configure?.Invoke(options);

        var clock = new ManualTimeProvider(Start);

        return (new TrustEngine(Options.Create(options), clock, NullLogger<TrustEngine>.Instance), clock);
    }

    internal static NodeTrustSnapshot Trust(ITrustEngine engine, string mac)
    {
        Assert.True(engine.TryGet(mac, out var snapshot), $"{mac} is not tracked.");

        return snapshot!;
    }

    internal static void RecordMany(ITrustEngine engine, string mac, TrustOutcome outcome, int count)
    {
        for (var i = 0; i < count; i++)
        {
            engine.Record(mac, outcome);
        }
    }
}
