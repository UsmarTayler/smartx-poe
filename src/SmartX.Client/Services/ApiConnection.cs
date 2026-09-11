namespace SmartX.Client.Services;

/// <summary>Whether the client can currently reach the API.</summary>
public enum ConnectionStatus
{
    /// <summary>A check is in flight.</summary>
    Checking,

    /// <summary>The last call to the API succeeded.</summary>
    Connected,

    /// <summary>The last call to the API could not reach it.</summary>
    Unreachable
}

/// <summary>
/// The client's single view of whether the API is reachable.
/// </summary>
/// <remarks>
/// One shared status rather than each page probing for itself, so the app bar, the trust panel and
/// the sensor form can never disagree about whether the backend is up. Any service that makes a call
/// reports the outcome here, which means the indicator reflects real traffic, not just the health
/// check that ran when the app started.
/// </remarks>
public sealed class ApiConnection(SmartXApiClient api)
{
    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The current reachability.</summary>
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Checking;

    /// <summary>Probes the API's health endpoint.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        Update(ConnectionStatus.Checking);

        var health = await api.GetHealthAsync(cancellationToken);

        Update(health is null ? ConnectionStatus.Unreachable : ConnectionStatus.Connected);
    }

    /// <summary>Records the outcome of a call made elsewhere.</summary>
    public void Report(bool reachable) =>
        Update(reachable ? ConnectionStatus.Connected : ConnectionStatus.Unreachable);

    private void Update(ConnectionStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        Changed?.Invoke();
    }
}
