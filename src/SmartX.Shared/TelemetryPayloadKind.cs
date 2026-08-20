using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// What shape of reading a registered sensor emits.
/// </summary>
/// <remarks>
/// This is the field that decides which packet type an incoming JSON value becomes. Declaring it at
/// registration rather than inferring it per packet is the whole point: JSON cannot tell an integer
/// from a real number, so <c>1200</c> arriving on the wire is equally valid as an int or a float.
/// Guessing from the payload would mean the same meter silently produced
/// <c>NumericPacket&lt;int&gt;</c> on a whole reading and <c>NumericPacket&lt;float&gt;</c> on a
/// fractional one. The sensor says once what it emits, and every packet from it is routed the same
/// way.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TelemetryPayloadKind>))]
public enum TelemetryPayloadKind
{
    /// <summary>Whole numbers. Routed to <see cref="NumericPacket{T}"/> of <see cref="int"/>.</summary>
    Integer,

    /// <summary>Fractional numbers. Routed to <see cref="NumericPacket{T}"/> of <see cref="float"/>.</summary>
    Real,

    /// <summary>Engaged or not. Routed to <see cref="StatePacket"/>.</summary>
    State
}
