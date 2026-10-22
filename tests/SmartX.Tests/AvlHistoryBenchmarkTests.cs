using System.Diagnostics;
using SmartX.Api.History;
using SmartX.Shared;
using Xunit.Abstractions;

namespace SmartX.Tests;

/// <summary>
/// Measures the AVL store against the sorted-dictionary store: a range query at three places in the history, and the cost of
/// inserting, at 10,000, 100,000 and 1,000,000 entries.
/// </summary>
/// <remarks>
/// A benchmark, so it is excluded from the default run (see <c>default.runsettings</c>) and run on purpose with the command in
/// the README. It prints a table ready to paste. The only assertions are the ones that do not depend on a clock: both stores
/// return the same hundred readings, and the AVL tree's height stays inside the AVL bound of 1.44 log2(n + 2). The one timing
/// assertion has a wide margin: at a million entries the newest window must be cheaper on the tree than on the dictionary.
/// <para>
/// The query is always a hundred readings, so the only thing that differs between rows is how much history there is, and the only
/// thing that differs between columns is where the window sits. A store that is O(log n + k) should be flat across the columns.
/// One that is O(n + k) should grow from left to right, and by size.
/// </para>
/// </remarks>
public class AvlHistoryBenchmarkTests(ITestOutputHelper output)
{
    private const string Mac = "AA:BB:CC:00:00:01";
    private const int WindowSize = 100;

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static HistoryEntry[] Entries(int count)
    {
        var entries = new HistoryEntry[count];

        for (var i = 0; i < count; i++)
        {
            entries[i] = new HistoryEntry
            {
                Sequence = i + 1,
                Timestamp = T0.AddSeconds(i),
                MacAddress = Mac,
                Kind = HistoryKind.Reading,
                Value = i,
            };
        }

        return entries;
    }

    private static HistoryEntry[] Shuffled(HistoryEntry[] entries)
    {
        var copy = (HistoryEntry[])entries.Clone();
        var random = new Random(18);

        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }

    private static T Fill<T>(T store, HistoryEntry[] entries)
        where T : IHistoryStore
    {
        foreach (var entry in entries)
        {
            store.Append(entry);
        }

        return store;
    }

    /// <summary>
    /// Microseconds per call, averaged over <paramref name="repeats"/> after a warm-up of a quarter of a second.
    /// </summary>
    /// <remarks>
    /// The warm-up is timed and not counted in calls because the runtime compiles hot code in the background a moment after it
    /// becomes hot. Measuring straight away times the unoptimised version, which is why the first window measured looked slower
    /// than the rest in an earlier run.
    /// </remarks>
    private static double Microseconds(Action action, int repeats)
    {
        var warmUp = Stopwatch.StartNew();

        do
        {
            action();
        }
        while (warmUp.ElapsedMilliseconds < 250);

        var started = Stopwatch.GetTimestamp();

        for (var i = 0; i < repeats; i++)
        {
            action();
        }

        return Stopwatch.GetElapsedTime(started).TotalMicroseconds / repeats;
    }

    /// <summary>Nanoseconds per insert: the median of several fills of a fresh store, after a warm-up that is not counted.</summary>
    /// <remarks>
    /// A median, because one fill in a few will include a garbage collection of the million nodes just built, and the mean would
    /// carry it.
    /// </remarks>
    private static double InsertNanoseconds(Func<IHistoryStore> create, HistoryEntry[] order, int fills)
    {
        // Warm up for a quarter of a second of filling, for the same reason as Microseconds.
        var warmUp = Stopwatch.StartNew();

        do
        {
            Fill(create(), order);
        }
        while (warmUp.ElapsedMilliseconds < 250);

        var times = new List<double>();

        for (var fill = 0; fill < fills; fill++)
        {
            GC.Collect();
            var store = create();
            var started = Stopwatch.GetTimestamp();
            Fill(store, order);
            times.Add(Stopwatch.GetElapsedTime(started).TotalNanoseconds / order.Length);
            GC.KeepAlive(store);
        }

        times.Sort();

        return times[times.Count / 2];
    }

    private static void ExpectHundred(IHistoryStore store, int firstSecond)
    {
        var found = store.GetRange(Mac, T0.AddSeconds(firstSecond), T0.AddSeconds(firstSecond + WindowSize));

        Assert.Equal(WindowSize, found.Count);
        Assert.Equal(firstSecond + 1, found[0].Sequence);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void The_avl_store_against_the_sorted_dictionary_store_at_three_sizes()
    {
        // The whole measurement runs twice and only the second is reported. The first pass is the process-wide warm-up: it
        // compiles the code both stores use and settles the allocator, so the first row of the table is not paying for it.
        Measure(report: false);
        Measure(report: true);
    }

    private void Measure(bool report)
    {
        var queryRows = new List<string>();
        var insertRows = new List<string>();
        var heightRows = new List<string>();
        double treeNewestAtMillion = 0, dictionaryNewestAtMillion = 0;

        foreach (var (size, fills) in new[] { (10_000, 15), (100_000, 7), (1_000_000, 5) })
        {
            var entries = Entries(size);
            var avl = Fill(new AvlHistoryStore(), entries);
            var dictionary = Fill(new SortedDictionaryHistoryStore(), entries);

            var positions = new (string Name, int First)[]
            {
                ("oldest", 0),
                ("middle", (size / 2) - (WindowSize / 2)),
                ("newest", size - WindowSize),
            };

            var cells = new List<string>();

            foreach (var (name, first) in positions)
            {
                ExpectHundred(avl, first);
                ExpectHundred(dictionary, first);

                // The dictionary's newest window at a million entries walks a million keys, so it gets fewer repeats.
                var treeTime = Microseconds(() => avl.GetRange(Mac, T0.AddSeconds(first), T0.AddSeconds(first + WindowSize)), repeats: 2000);
                var dictionaryTime = Microseconds(
                    () => dictionary.GetRange(Mac, T0.AddSeconds(first), T0.AddSeconds(first + WindowSize)),
                    repeats: size >= 1_000_000 ? 3 : 20);

                cells.Add($"{treeTime:N1} / {dictionaryTime:N1}");

                if (size == 1_000_000 && name == "newest")
                {
                    treeNewestAtMillion = treeTime;
                    dictionaryNewestAtMillion = dictionaryTime;
                }
            }

            queryRows.Add($"| {size:N0} | {string.Join(" | ", cells)} |");

            var ascending = entries;
            var shuffled = Shuffled(entries);

            insertRows.Add(
                $"| {size:N0} | " +
                $"{InsertNanoseconds(() => new AvlHistoryStore(), ascending, fills):N0} / {InsertNanoseconds(() => new SortedDictionaryHistoryStore(), ascending, fills):N0} | " +
                $"{InsertNanoseconds(() => new AvlHistoryStore(), shuffled, fills):N0} / {InsertNanoseconds(() => new SortedDictionaryHistoryStore(), shuffled, fills):N0} |");

            var tree = avl.GetStats().Largest.Single(stats => stats.Scope == Mac);

            heightRows.Add($"| {size:N0} | {tree.Height} | {tree.MinimumHeight} | {Math.Log2(size):N1} |");

            // The AVL bound: height < 1.4405 log2(n + 2) - 0.3277. Checked with a little room, and not a clock.
            Assert.True(tree.Height <= 1.4405 * Math.Log2(size + 2), $"Height {tree.Height} is outside the AVL bound at {size:N0} entries.");
        }

        if (report)
        {
            Print(queryRows, insertRows, heightRows);
        }

        // The one claim the whole exercise rests on, with a margin of orders of magnitude.
        Assert.True(
            treeNewestAtMillion * 20 < dictionaryNewestAtMillion,
            $"Expected the tree to be far cheaper for the newest window at 1,000,000 entries: {treeNewestAtMillion:N1} us against {dictionaryNewestAtMillion:N1} us.");
    }

    private void Print(List<string> queryRows, List<string> insertRows, List<string> heightRows)
    {
        output.WriteLine($"Range query returning {WindowSize} readings, microseconds per query, AVL / SortedDictionary");
        output.WriteLine("| Entries | Oldest window | Middle window | Newest window |");
        output.WriteLine("| ---: | ---: | ---: | ---: |");
        queryRows.ForEach(output.WriteLine);

        output.WriteLine(string.Empty);
        output.WriteLine("Insert cost, nanoseconds per insert, AVL / SortedDictionary");
        output.WriteLine("| Entries | Arriving in time order | Arriving shuffled |");
        output.WriteLine("| ---: | ---: | ---: |");
        insertRows.ForEach(output.WriteLine);

        output.WriteLine(string.Empty);
        output.WriteLine("AVL tree height against the best possible");
        output.WriteLine("| Entries | Height | Best possible (ceil log2(n+1)) | log2 n |");
        output.WriteLine("| ---: | ---: | ---: | ---: |");
        heightRows.ForEach(output.WriteLine);
    }
}
