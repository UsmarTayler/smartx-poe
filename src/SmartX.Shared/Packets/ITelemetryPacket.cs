namespace SmartX.Shared;

/// <summary>
/// The part of a telemetry packet that does not depend on its payload type.
/// </summary>
/// <remarks>
/// Exists so that packets of different payload types can be held in one collection. A store cannot
/// declare <c>List&lt;NumericPacket&lt;int&gt;&gt;</c> and also hold a
/// <c>NumericPacket&lt;float&gt;</c>, because those are unrelated constructed types.
/// <para>
/// <b>This does not reintroduce boxing.</b> A packet is a class, so it is already a heap object and
/// holding a reference to it through an interface is just a reference — nothing is copied and
/// nothing is allocated. Boxing would mean wrapping the payload <c>T</c> itself, and this interface
/// deliberately does not expose <c>Value</c>: doing so would force a return type of
/// <see cref="object"/>, which is exactly the cost the generic design exists to avoid. Code that
/// needs the reading works with the concrete <see cref="NumericPacket{T}"/> or
/// <see cref="StatePacket"/>, where the payload keeps its own type.
/// </para>
/// </remarks>
public interface ITelemetryPacket
{
    /// <summary>MAC address of the node that produced this reading.</summary>
    string NodeId { get; }

    /// <summary>When the reading was taken.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>The kind of device that produced it.</summary>
    SensorCategory Category { get; }

    /// <summary>The unit the reading is expressed in.</summary>
    string Unit { get; }
}
