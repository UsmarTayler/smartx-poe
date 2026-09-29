using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Ingestion;

/// <summary>Why a reading was treated as critical, and how urgent it is.</summary>
/// <param name="Severity">Lower is more urgent.</param>
/// <param name="Reason">Phrased for a log line or an alert: "power spike", "moisture crash".</param>
internal readonly record struct CriticalAlert(int Severity, string Reason);

/// <summary>
/// Decides whether a valid reading is an emergency.
/// </summary>
/// <remarks>
/// <b>Classification happens at the edge</b>, while the request is still being handled, because the answer
/// decides which queue the reading joins. Deciding later, in the worker, would mean the alert had already
/// waited behind the backlog it was supposed to bypass.
/// <para>
/// The device is never asked. A sensor reports a number; whether that number is an emergency depends on
/// thresholds an operator configures for the deployment, and those live here.
/// </para>
/// </remarks>
internal sealed class CriticalClassifier(IOptions<IngestionOptions> options)
{
    private readonly IngestionOptions _options = options.Value;

    /// <summary>Tests a packet against the configured rules.</summary>
    /// <returns><see langword="false"/> when the reading is ordinary telemetry.</returns>
    public bool IsCritical(ITelemetryPacket packet, out CriticalAlert alert)
    {
        alert = default;

        // A state packet has no number to compare, so no threshold can apply to it.
        if (!packet.TryGetNumericValue(out var value))
        {
            return false;
        }

        foreach (var rule in _options.CriticalRules)
        {
            if (rule.Category != packet.Category || !string.Equals(rule.Unit, packet.Unit, StringComparison.Ordinal))
            {
                continue;
            }

            if (value > rule.Above || value < rule.Below)
            {
                // Comparisons against a null limit are false, so a rule with only an upper limit never fires
                // on a low reading, and vice versa.
                alert = new CriticalAlert(rule.Severity, rule.Reason);

                return true;
            }
        }

        return false;
    }
}
