namespace SmartX.Shared;

/// <summary>One stored reading, as the range endpoint returns it.</summary>
/// <param name="Sequence">The arrival number. With the timestamp it identifies the reading, so a client can drop a repeat between pages.</param>
/// <param name="Timestamp">When the device took it.</param>
/// <param name="Value">The reading. A state is 1 for engaged and 0 for released.</param>
/// <param name="Unit">Its unit, which is what <c>metric</c> filters on.</param>
public sealed record HistoryReading(long Sequence, DateTimeOffset Timestamp, double Value, string? Unit);

/// <summary>The readings one sensor took in a window, oldest first.</summary>
/// <param name="MacAddress">The sensor.</param>
/// <param name="From">The start of the window, included.</param>
/// <param name="To">The end of the window, excluded.</param>
/// <param name="Metric">The unit filter that was applied, or null for none.</param>
/// <param name="Readings">At most the page size, oldest first.</param>
/// <param name="Truncated">Whether more readings matched than the page holds.</param>
/// <param name="QueryMicroseconds">
/// How long the store took to find the window, in microseconds. The tree's seek and the walk across the window; not the HTTP, the
/// registry lookup or the JSON. Shown on the topology page so the tree's speed is something a person can see.
/// </param>
/// <param name="NextFrom">
/// When truncated, the timestamp of the first reading left out: ask again from here. If several readings share that instant and
/// some came back already, <see cref="HistoryReading.Sequence"/> is how to drop the repeats.
/// </param>
public sealed record HistoryRange(
    string MacAddress,
    DateTimeOffset From,
    DateTimeOffset To,
    string? Metric,
    IReadOnlyList<HistoryReading> Readings,
    bool Truncated,
    double QueryMicroseconds,
    DateTimeOffset? NextFrom);

/// <summary>The size of one of a store's trees.</summary>
/// <param name="Scope">A node's MAC address, or <c>events</c> for the mesh-wide event stream.</param>
/// <param name="Entries">How many entries it holds.</param>
/// <param name="Height">How many levels it has, or null when the structure does not expose its height.</param>
/// <param name="MinimumHeight">
/// ⌈log₂(n + 1)⌉: the height of a perfectly balanced tree of this many entries, which no tree can beat. A height near it is the
/// visible proof that the tree is balancing.
/// </param>
public sealed record HistoryTreeStats(string Scope, int Entries, int? Height, int MinimumHeight);

/// <summary>What the history store holds and how its trees are shaped.</summary>
/// <param name="Store">Which implementation is running: <c>Avl</c> or <c>SortedDictionary</c>.</param>
/// <param name="Entries">How many entries it holds across every tree.</param>
/// <param name="Trees">How many trees it has: one per node, plus the event stream.</param>
/// <param name="Largest">The event stream first, then the biggest node trees.</param>
public sealed record HistoryStoreStats(string Store, int Entries, int Trees, IReadOnlyList<HistoryTreeStats> Largest);
