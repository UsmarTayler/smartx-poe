namespace SmartX.Api.History;

/// <summary>
/// The time-ordered record of everything that has happened: readings, commands, alerts, trust transitions and
/// searches.
/// </summary>
/// <remarks>
/// <b>This interface is a contract with the next part of the project.</b> The sorted-dictionary store below is
/// the implementation that exists now, and it has a known limit: it cannot jump to the middle of a node's
/// history, so a range query walks from the start. Part 3 replaces it with a balanced tree where the same query
/// is O(log n + k). Everything that depends on history depends on this interface, and the contract tests in
/// the test project run against it, so the replacement is proven safe by adding one subclass to the same
/// suite rather than by hoping.
/// <para>
/// <b>Not the Part 1 ring buffer.</b> The ring buffer behind trust scoring keeps a fixed count of the most
/// recent <i>outcomes</i> and forgets the rest, because trust only needs recent behaviour. This store keeps
/// everything inside a time window, in time order, because timelines and analysis need to ask "what happened
/// between 14:00 and 14:10", which a count-bounded buffer cannot answer.
/// </para>
/// <para>
/// <b>Boundaries.</b> Every range is half-open: <c>from</c> is included and <c>to</c> is excluded, so
/// <c>[from, to)</c>. Adjacent ranges therefore tile without overlap — an entry exactly on the boundary
/// belongs to the later range only — which is what lets a timeline be fetched in consecutive windows without a
/// reading appearing twice or not at all.
/// </para>
/// <para>
/// <b>Order.</b> Results are in time order, and entries with the same timestamp are in arrival order.
/// </para>
/// </remarks>
internal interface IHistoryStore
{
    /// <summary>How many entries are held across every node.</summary>
    int Count { get; }

    /// <summary>Adds an entry to its node's history and, if it is not a reading, to the mesh-wide event stream.</summary>
    /// <returns>
    /// <see langword="false"/> when an entry with exactly the same timestamp and sequence is already held. The
    /// existing entry is kept and the new one is not stored: a repeated sequence number is a caller fault, and
    /// silently replacing a record would be worse than refusing one.
    /// </returns>
    bool Append(HistoryEntry entry);

    /// <summary>One node's entries with <c>from &lt;= Timestamp &lt; to</c>, oldest first.</summary>
    /// <returns>An empty list when the node is unknown, the range is empty, or <c>from</c> is not before <c>to</c>.</returns>
    IReadOnlyList<HistoryEntry> GetRange(string macAddress, DateTimeOffset from, DateTimeOffset to);

    /// <summary>The most recent <paramref name="count"/> entries for one node, oldest first.</summary>
    /// <returns>Fewer when the node has fewer, and an empty list when it is unknown or the count is not positive.</returns>
    IReadOnlyList<HistoryEntry> GetLatest(string macAddress, int count);

    /// <summary>
    /// Every non-reading entry across the mesh with <c>from &lt;= Timestamp &lt; to</c>, oldest first.
    /// </summary>
    /// <remarks>
    /// The mesh-wide stream of commands, alerts, trust transitions and searches. Readings are left out because
    /// they outnumber everything else by orders of magnitude and nobody asks for "every reading from every
    /// node" as one list.
    /// </remarks>
    IReadOnlyList<HistoryEntry> GetEvents(DateTimeOffset from, DateTimeOffset to);

    /// <summary>Removes every entry with <c>Timestamp &lt; cutoff</c>, and nothing else.</summary>
    /// <returns>How many entries were removed.</returns>
    int EvictOlderThan(DateTimeOffset cutoff);
}
