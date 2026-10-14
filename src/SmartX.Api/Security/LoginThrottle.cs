using Microsoft.Extensions.Options;

namespace SmartX.Api.Security;

/// <summary>
/// Locks a username for a while after repeated failures, whether or not the name belongs to anyone.
/// </summary>
/// <remarks>
/// The rate limiter slows one client down; this slows an attack on one account down, from any number of clients. It counts
/// failures per submitted name and not per real user, so a name that does not exist locks exactly as one that does, and the response
/// cannot be used to find out which names are real.
/// <para>
/// In memory and bounded. A flood of made-up names must not grow a dictionary without limit, so past <c>MaxTracked</c> entries
/// the expired ones are dropped, and if that does not make room new names are simply not tracked: the rate limiter is still in front.
/// </para>
/// </remarks>
internal sealed class LoginThrottle(IOptions<AuthOptions> options, TimeProvider time)
{
    private const int MaxTracked = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long until this name may try again, or null if it may now.</summary>
    public TimeSpan? LockedFor(string username)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(username, out var entry) && entry.LockedUntil is { } until)
            {
                var remaining = until - time.GetUtcNow();

                if (remaining > TimeSpan.Zero)
                {
                    return remaining;
                }

                // The lock ran out: a fresh start, not one more strike.
                _entries.Remove(username);
            }

            return null;
        }
    }

    /// <summary>Counts a failed attempt, and locks the name if it was the last one allowed.</summary>
    public void RecordFailure(string username)
    {
        var settings = options.Value;

        lock (_gate)
        {
            if (!_entries.TryGetValue(username, out var entry))
            {
                if (_entries.Count >= MaxTracked)
                {
                    var now = time.GetUtcNow();
                    var expired = _entries.Where(pair => pair.Value.LockedUntil is { } until && until <= now).Select(pair => pair.Key).ToList();

                    foreach (var key in expired)
                    {
                        _entries.Remove(key);
                    }

                    if (_entries.Count >= MaxTracked)
                    {
                        return;
                    }
                }

                entry = new Entry();
                _entries[username] = entry;
            }

            entry.Failures++;

            if (entry.Failures >= settings.MaxFailedAttempts)
            {
                entry.LockedUntil = time.GetUtcNow() + settings.LockoutDuration;
                entry.Failures = 0;
            }
        }
    }

    /// <summary>A correct login clears the count.</summary>
    public void RecordSuccess(string username)
    {
        lock (_gate)
        {
            _entries.Remove(username);
        }
    }

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset? LockedUntil;
    }
}
