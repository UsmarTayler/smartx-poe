using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// A sensor known to the gateway: what it is, where it sits, and what it is expected to report.
/// </summary>
/// <remarks>
/// The registration is the contract every later packet from that device is checked against. A
/// reading is accepted only if the sensor was registered, the payload matches the shape the
/// registration declared, and the value falls inside the range the registration promised.
/// <para>
/// The constructor enforces everything that can be checked from the registration alone — MAC
/// format, non-empty unit, payload and category agreeing. It deliberately does <i>not</i> check the
/// zone, because that requires the surrounding tree; <see cref="ZoneValidator"/> owns that and
/// needs an already-constructed registration to check.
/// </para>
/// </remarks>
public sealed record SensorRegistration
{
    /// <summary>Creates a sensor registration.</summary>
    /// <param name="macAddress">The device's MAC, in any accepted format. Stored canonicalised.</param>
    /// <param name="location">Where the sensor sits.</param>
    /// <param name="category">What kind of device it is.</param>
    /// <param name="unit">The unit it reports in, such as "°C" or "kWh".</param>
    /// <param name="expectedRange">
    /// The range it is expected to report within. Not consulted for
    /// <see cref="TelemetryPayloadKind.State"/> sensors, where the payload is a bool and a range
    /// says nothing.
    /// </param>
    /// <param name="payloadKind">The shape of reading it emits.</param>
    /// <exception cref="ArgumentException">
    /// The MAC is malformed or reserved, the unit is blank, or a
    /// <see cref="TelemetryPayloadKind.State"/> payload is paired with a non-actuator category.
    /// </exception>
    [JsonConstructor]
    public SensorRegistration(
        string macAddress,
        DeploymentLocation location,
        SensorCategory category,
        string unit,
        ValueRange expectedRange,
        TelemetryPayloadKind payloadKind)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        // A boolean reading becomes a StatePacket, and StatePacket fixes its own category to
        // Actuator. Allowing a State sensor to declare itself Environmental would produce packets
        // whose category contradicted the registration that accepted them.
        //
        // The reverse is allowed on purpose: an Actuator may declare a numeric payload, because a
        // dimmer or a modulating valve is an actuator that reports a level rather than a position.
        if (payloadKind == TelemetryPayloadKind.State && category != SensorCategory.Actuator)
        {
            throw new ArgumentException(
                $"A {nameof(TelemetryPayloadKind.State)} sensor must be registered as " +
                $"{nameof(SensorCategory.Actuator)}, not {category}.",
                nameof(payloadKind));
        }

        MacAddress = MacAddressValidator.Normalise(macAddress);
        Location = location;
        Category = category;
        Unit = unit;
        ExpectedRange = expectedRange;
        PayloadKind = payloadKind;
    }

    /// <summary>The device's MAC, uppercase and colon-separated. The unique key for a sensor.</summary>
    public string MacAddress { get; }

    /// <summary>Where the sensor sits.</summary>
    public DeploymentLocation Location { get; }

    /// <summary>What kind of device it is.</summary>
    public SensorCategory Category { get; }

    /// <summary>The unit it reports in.</summary>
    public string Unit { get; }

    /// <summary>The range it is expected to report within.</summary>
    public ValueRange ExpectedRange { get; }

    /// <summary>The shape of reading it emits.</summary>
    public TelemetryPayloadKind PayloadKind { get; }
}
