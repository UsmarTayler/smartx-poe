using System.Collections.Concurrent;

namespace SmartX.Api.Realtime;

/// <summary>
/// Which topics have at least one listener, so the broadcaster does no work for a topic nobody has asked for.
/// </summary>
/// <remarks>
/// SignalR groups already decide who <i>receives</i> a message. They do not tell the sender whether anyone will.
/// Without this, every reading would be copied into a batch and every trust change would be turned into a snapshot
/// even with no browser open, and at thousands of devices that is the whole cost of the push path spent on an
/// empty room. With it, an idle gateway pays for a dictionary lookup per event.
/// <para>
/// Changes (joining, leaving, disconnecting) are rare and take a lock. Reads are the hot path and take none: the
/// per-topic counts are in a concurrent dictionary and the set of watched devices is an immutable set that is
/// replaced, never edited.
/// </para>
/// </remarks>
internal sealed class RealtimeSubscriptions
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HashSet<string>> _byConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.Ordinal);

    private volatile HashSet<string> _watchedDevices = new(StringComparer.Ordinal);

    /// <summary>Records that a connection joined a topic.</summary>
    /// <returns><see langword="false"/> when it was already in it.</returns>
    public bool Join(string connectionId, string topic)
    {
        lock (_gate)
        {
            if (!_byConnection.TryGetValue(connectionId, out var topics))
            {
                topics = new HashSet<string>(StringComparer.Ordinal);
                _byConnection.Add(connectionId, topics);
            }

            if (!topics.Add(topic))
            {
                return false;
            }

            _counts.AddOrUpdate(topic, 1, static (_, count) => count + 1);
            _generations.AddOrUpdate(topic, 1, static (_, generation) => generation + 1);
            RebuildWatchedDevices();

            return true;
        }
    }

    /// <summary>Records that a connection left a topic.</summary>
    /// <returns><see langword="false"/> when it was not in it.</returns>
    public bool Leave(string connectionId, string topic)
    {
        lock (_gate)
        {
            if (!_byConnection.TryGetValue(connectionId, out var topics) || !topics.Remove(topic))
            {
                return false;
            }

            Decrement(topic);
            RebuildWatchedDevices();

            return true;
        }
    }

    /// <summary>Forgets a connection that has gone, and everything it had joined.</summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var topics))
            {
                return;
            }

            foreach (var topic in topics)
            {
                Decrement(topic);
            }

            RebuildWatchedDevices();
        }
    }

    /// <summary>Whether anyone is listening to a topic.</summary>
    public bool Has(string topic) => _counts.TryGetValue(topic, out var count) && count > 0;

    /// <summary>How many connections are listening to a topic.</summary>
    public int Count(string topic) => _counts.TryGetValue(topic, out var count) ? count : 0;

    /// <summary>Whether anyone is watching one device.</summary>
    public bool HasDevice(string macAddress) => _watchedDevices.Contains(macAddress);

    /// <summary>Whether anyone is watching any device at all.</summary>
    public bool HasAnyDevice => _watchedDevices.Count > 0;

    /// <summary>The MAC addresses someone is watching.</summary>
    public IReadOnlyCollection<string> WatchedDevices => _watchedDevices;

    /// <summary>
    /// A number that goes up every time anyone joins a topic.
    /// </summary>
    /// <remarks>
    /// A new listener arrives with a snapshot, taken from the server's state at that moment. Anything the
    /// broadcaster remembers about "what I last told clients" is then wrong for that listener, so the broadcaster
    /// compares this number to the one it saw and starts its memory afresh when it has moved.
    /// </remarks>
    public long GenerationOf(string topic) => _generations.TryGetValue(topic, out var generation) ? generation : 0;

    private void Decrement(string topic)
    {
        if (_counts.AddOrUpdate(topic, 0, static (_, count) => Math.Max(0, count - 1)) == 0)
        {
            _counts.TryRemove(new KeyValuePair<string, int>(topic, 0));
        }
    }

    // Called under the lock, after every change. Device topics are the only ones that need a set: "is anyone
    // watching this device" is asked once per reading, and has to be an O(1) check that takes no lock.
    private void RebuildWatchedDevices()
    {
        HashSet<string> watched = new(StringComparer.Ordinal);

        foreach (var (topic, count) in _counts)
        {
            if (count > 0 && SmartX.Shared.RealtimeTopics.IsDevice(topic))
            {
                watched.Add(topic[SmartX.Shared.RealtimeTopics.DevicePrefix.Length..]);
            }
        }

        _watchedDevices = watched;
    }
}
