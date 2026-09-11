namespace SmartX.Api.Storage;

/// <summary>
/// Configuration for telemetry retention.
/// </summary>
/// <remarks>
/// Bound from the "Telemetry" section through <c>IOptions</c>, the same way trust is configured.
/// Retention used to be read with a bare configuration lookup in Program.cs, which meant two
/// sections of the same appsettings file were loaded two different ways, and a negative value was
/// never caught until the store's constructor threw on the first request that resolved it.
/// </remarks>
public sealed class TelemetryOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>Readings retained per node before the oldest is overwritten.</summary>
    /// <remarks>
    /// 256 is enough history for a dashboard to draw a trend while keeping the whole store bounded at
    /// a predictable size: nodes × 256 references, fixed at startup, however long the process runs.
    /// Zero is valid and disables retention.
    /// </remarks>
    public int RetentionPerNode { get; set; } = 256;
}
