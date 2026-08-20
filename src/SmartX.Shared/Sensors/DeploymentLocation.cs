using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// Where a sensor physically sits.
/// </summary>
/// <remarks>
/// Grouped into its own type rather than flattened onto <see cref="SensorRegistration"/> so that
/// the three fields that answer "where is it" travel together. The topology view in a later part
/// needs exactly this triple and nothing else from a registration.
/// </remarks>
public sealed record DeploymentLocation
{
    /// <summary>Creates a deployment location.</summary>
    /// <param name="room">Room or bay name, such as "Server Room 2".</param>
    /// <param name="zoneId">The <see cref="DeploymentZone"/> the sensor is registered into.</param>
    /// <param name="nodeId">
    /// The mesh node the sensor reports through. Distinct from the sensor's own MAC: several
    /// sensors can hang off one gateway node.
    /// </param>
    /// <exception cref="ArgumentException">Any field is null, empty or whitespace.</exception>
    [JsonConstructor]
    public DeploymentLocation(string room, string zoneId, string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        Room = room;
        ZoneId = zoneId;
        NodeId = nodeId;
    }

    /// <summary>Room or bay name.</summary>
    public string Room { get; }

    /// <summary>The zone the sensor is registered into.</summary>
    public string ZoneId { get; }

    /// <summary>The mesh node the sensor reports through.</summary>
    public string NodeId { get; }
}
