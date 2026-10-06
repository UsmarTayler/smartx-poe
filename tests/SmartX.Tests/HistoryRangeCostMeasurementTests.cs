using System.Diagnostics;
using SmartX.Api.History;
using SmartX.Shared;
using Xunit.Abstractions;

namespace SmartX.Tests;

/// <summary>
/// Measures what a range query on the sorted-dictionary store costs, so the limit is a number and not a claim.
/// </summary>
/// <remarks>
/// <c>SortedDictionary</c> has no range-seek, so <see cref="IHistoryStore.GetRange"/> walks from the oldest
/// entry. A query for an old window stops early and is cheap; a query for the newest window walks everything.
/// That asymmetry is the stated reason the next part of the project builds a balanced tree, and this test
/// prints it. It asserts the asymmetry exists, with a wide margin, rather than any absolute speed.
/// </remarks>
public class HistoryRangeCostMeasurementTests(ITestOutputHelper output)
{
    private const string Mac = "AA:BB:CC:00:00:01";
    private const int Entries = 200_000;
    private const int WindowSize = 100;

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static TimeSpan Time(Action action, int repeats)
    {
        action();

        var started = Stopwatch.GetTimestamp();

        for (var i = 0; i < repeats; i++)
        {
            action();
        }

        return Stopwatch.GetElapsedTime(started) / repeats;
    }

    [Fact]
    public void A_range_query_costs_what_it_takes_to_reach_the_start_of_the_range()
    {
        var store = new SortedDictionaryHistoryStore();

        for (var i = 0; i < Entries; i++)
        {
            store.Append(new HistoryEntry
            {
                Sequence = i + 1,
                Timestamp = T0.AddSeconds(i),
                MacAddress = Mac,
                Kind = HistoryKind.Reading,
                Value = i,
            });
        }

        // The same hundred entries asked for twice: once at the very start of the history, once at the very end.
        var oldest = Time(() => Assert.Equal(WindowSize, store.GetRange(Mac, T0, T0.AddSeconds(WindowSize)).Count), repeats: 20);
        var newest = Time(
            () => Assert.Equal(WindowSize, store.GetRange(Mac, T0.AddSeconds(Entries - WindowSize), T0.AddSeconds(Entries)).Count),
            repeats: 20);

        output.WriteLine($"History held                       : {Entries:N0} entries for one node");
        output.WriteLine($"Oldest {WindowSize} entries (stops early)   : {oldest.TotalMicroseconds:N0} us");
        output.WriteLine($"Newest {WindowSize} entries (walks it all)  : {newest.TotalMicroseconds:N0} us");
        output.WriteLine($"The newest window costs              : {newest / oldest:N0}x as much for the same {WindowSize} results");

        // Same answer size, very different cost. O(n) to reach the start of the range.
        Assert.True(newest > oldest * 5, $"Expected the newest window to cost far more, but {newest.TotalMicroseconds:N0} us vs {oldest.TotalMicroseconds:N0} us.");
    }
}
