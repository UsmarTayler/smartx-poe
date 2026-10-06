using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartX.Api.History;
using SmartX.Shared;
using static SmartX.Tests.TrustTestHost;

namespace SmartX.Tests;

/// <summary>Retention: the window is configurable, applies to the oldest entries only, and runs on a timer.</summary>
public class HistoryRetentionTests
{
    private static HistoryEntry Reading(long sequence, DateTimeOffset at) => new()
    {
        Sequence = sequence,
        Timestamp = at,
        MacAddress = NodeA,
        Kind = HistoryKind.Reading,
        Value = sequence,
    };

    [Fact]
    public void A_pass_removes_what_is_older_than_the_window_and_keeps_the_rest()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new SortedDictionaryHistoryStore();
        var service = new HistoryEvictionService(
            store, Options.Create(new HistoryOptions { RetentionWindow = TimeSpan.FromHours(24) }), clock, NullLogger<HistoryEvictionService>.Instance);

        store.Append(Reading(1, clock.GetUtcNow().AddHours(-30)));
        store.Append(Reading(2, clock.GetUtcNow().AddHours(-25)));
        store.Append(Reading(3, clock.GetUtcNow().AddHours(-23)));
        store.Append(Reading(4, clock.GetUtcNow()));

        Assert.Equal(2, service.Evict());
        Assert.Equal([3L, 4L], store.GetLatest(NodeA, 10).Select(entry => entry.Sequence));
    }

    [Fact]
    public void The_window_comes_from_options_and_moves_with_the_clock()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new SortedDictionaryHistoryStore();
        var service = new HistoryEvictionService(
            store, Options.Create(new HistoryOptions { RetentionWindow = TimeSpan.FromMinutes(10) }), clock, NullLogger<HistoryEvictionService>.Instance);

        store.Append(Reading(1, clock.GetUtcNow()));

        Assert.Equal(0, service.Evict());

        // Nothing was appended, and nothing was removed by hand: the reading simply aged out.
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, service.Evict());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task The_service_evicts_on_its_own_timer_and_not_on_append()
    {
        var store = new SortedDictionaryHistoryStore();
        var service = new HistoryEvictionService(
            store,
            Options.Create(new HistoryOptions { RetentionWindow = TimeSpan.FromHours(1), EvictionInterval = TimeSpan.FromMilliseconds(50) }),
            TimeProvider.System,
            NullLogger<HistoryEvictionService>.Instance);

        // Appended already expired. If eviction ran on append it would be gone before this assertion.
        store.Append(Reading(1, DateTimeOffset.UtcNow.AddHours(-2)));
        Assert.Equal(1, store.Count);

        await service.StartAsync(CancellationToken.None);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (store.Count > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.Equal(0, store.Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
