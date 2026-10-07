using SmartX.Client.Services;

namespace SmartX.Client.Realtime;

/// <summary>Where the client's single real-time connection stands.</summary>
public enum RealtimeState
{
    /// <summary>The first connection attempt is under way. Nothing has failed yet.</summary>
    Connecting,

    /// <summary>Connected: the server pushes changes, and nothing is polled.</summary>
    Live,

    /// <summary>The connection dropped and is being re-established. What is on screen is the last known state.</summary>
    Reconnecting,

    /// <summary>
    /// The hub cannot be reached, so the feeds are polling instead. The connection is still being retried in the
    /// background and the feeds go back to push the moment it succeeds.
    /// </summary>
    Polling
}

/// <summary>What the connection indicator in the app bar shows.</summary>
public enum ConnectionDisplay
{
    /// <summary>Working out whether the API is there.</summary>
    Checking,

    /// <summary>Updates are being pushed.</summary>
    Live,

    /// <summary>The push connection dropped and is being re-established.</summary>
    Reconnecting,

    /// <summary>Push is unavailable and the app is polling.</summary>
    Polling,

    /// <summary>The API itself cannot be reached.</summary>
    Unreachable
}

/// <summary>Works out what the indicator shows from the two things that can be known about the connection.</summary>
public static class ConnectionDisplays
{
    /// <summary>The indicator's state.</summary>
    /// <param name="api">Whether the API answered its last request.</param>
    /// <param name="realtime">Where the push connection stands.</param>
    /// <remarks>
    /// Two facts feed one indicator. The push connection says whether updates are live; the API's own reachability
    /// says whether anything works at all. A push connection that is down while the API answers is a degraded mode
    /// worth naming (Polling), and one that is down because the API is gone is just the API being unreachable, which
    /// is the more useful thing to say.
    /// </remarks>
    public static ConnectionDisplay From(ConnectionStatus api, RealtimeState realtime) => realtime switch
    {
        RealtimeState.Live => ConnectionDisplay.Live,
        RealtimeState.Reconnecting => ConnectionDisplay.Reconnecting,
        RealtimeState.Polling => api == ConnectionStatus.Unreachable ? ConnectionDisplay.Unreachable : ConnectionDisplay.Polling,
        _ => api == ConnectionStatus.Unreachable ? ConnectionDisplay.Unreachable : ConnectionDisplay.Checking,
    };
}
