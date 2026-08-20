using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Registration and lookup of sensors.
/// </summary>
/// <remarks>
/// Handlers are named static methods rather than inline lambdas. A lambda is fine for the one-line
/// health check; a handler that validates a request, walks a zone hierarchy and distinguishes four
/// outcomes is easier to read with a name and a signature at the top of it.
/// </remarks>
internal static class SensorEndpoints
{
    internal static RouteGroupBuilder MapSensorEndpoints(this RouteGroupBuilder api)
    {
        var sensors = api.MapGroup("/sensors");

        sensors.MapGet("", ListAsync).WithName("ListSensors");
        sensors.MapGet("/{mac}", GetAsync).WithName("GetSensor");
        sensors.MapPost("", RegisterAsync).WithName("RegisterSensor");

        return api;
    }

    /// <summary>Every registered sensor.</summary>
    private static async Task<IResult> ListAsync(
        ISensorRegistry registry,
        CancellationToken cancellationToken)
    {
        var sensors = await registry.ListAsync(cancellationToken);

        return Results.Ok(sensors);
    }

    /// <summary>One sensor by MAC, in either accepted format.</summary>
    private static async Task<IResult> GetAsync(
        string mac,
        ISensorRegistry registry,
        CancellationToken cancellationToken)
    {
        var sensor = await registry.FindAsync(mac, cancellationToken);

        return sensor is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{mac}'.")
            : Results.Ok(sensor);
    }

    /// <summary>
    /// Registers a sensor, after checking it against its zone and every zone above it.
    /// </summary>
    /// <remarks>
    /// Four outcomes, in the order they are cheapest to establish: the request is malformed (400),
    /// the sensor is well-formed but does not belong in the zone it named (400), the MAC is already
    /// taken (409), or it is accepted (201). Validating before touching the registry means a
    /// rejected registration never half-writes.
    /// </remarks>
    private static async Task<IResult> RegisterAsync(
        SensorRegistrationRequest request,
        ISensorRegistry registry,
        IZoneStore zones,
        CancellationToken cancellationToken)
    {
        if (!request.TryCreateRegistration(out var registration, out var errors))
        {
            return Results.ValidationProblem(errors, title: "The registration request is not valid");
        }

        var catalogue = await zones.GetCatalogueAsync(cancellationToken);
        var validation = new ZoneValidator(catalogue).Validate(registration);

        if (!validation.IsValid)
        {
            // Every violation is reported, not just the first, so an operator fixing a deployment
            // learns everything wrong with it in one round trip. They are grouped under ZoneId
            // because that is the field the caller would change to fix any of them.
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    [nameof(SensorRegistrationRequest.ZoneId)] =
                        [.. validation.Violations.Select(violation => violation.Message)]
                },
                title: "The sensor cannot be deployed in that zone");
        }

        if (!await registry.TryAddAsync(registration, cancellationToken))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Sensor already registered",
                detail: $"A sensor with MAC address '{registration.MacAddress}' is already registered.");
        }

        return Results.Created($"/api/sensors/{registration.MacAddress}", registration);
    }
}
