using System.Globalization;
using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// Turns telemetry into the text the UI shows.
/// </summary>
/// <remarks>
/// A packet arrives from the API as <see cref="ITelemetryPacket"/>, which deliberately exposes no
/// value — exposing one would force it to be <c>object</c>. Pattern matching on the concrete packet
/// type is how the reading gets its type back: each arm sees a <c>NumericPacket&lt;float&gt;</c> or a
/// <see cref="StatePacket"/> and reads a real <c>float</c> or <c>bool</c>, with nothing boxed on the
/// way. Invariant culture throughout, so a reading of 21.5 never renders as "21,5" on a machine set
/// to a comma-decimal locale while the API expects a point.
/// </remarks>
public static class ReadingFormatter
{
    public static string Value(ITelemetryPacket packet) => packet switch
    {
        NumericPacket<float> real => Number(real.Value),
        NumericPacket<int> whole => whole.Value.ToString(CultureInfo.InvariantCulture),
        StatePacket state => state.Value ? "on" : "off",
        _ => "—"
    };

    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset at) =>
        at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
