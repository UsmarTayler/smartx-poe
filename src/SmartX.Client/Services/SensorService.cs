using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// The registered sensors and the zone hierarchy, as the client knows them.
/// </summary>
/// <remarks>
/// Scoped, so the list survives navigation: leave the sensors page and come back, and it renders
/// immediately from what is already held instead of blanking and reloading. A successful
/// registration is added to the list here, which is how the new sensor appears without a reload —
/// every page reading <see cref="Sensors"/> sees it on the next render.
/// </remarks>
public sealed class SensorService(SmartXApiClient api, ApiConnection connection)
{
    private List<SensorRegistration> _sensors = [];

    /// <summary>Raised when the sensors, the zones or the load state change.</summary>
    public event Action? Changed;

    /// <summary>Registered sensors, ordered by MAC as the API returns them.</summary>
    public IReadOnlyList<SensorRegistration> Sensors => _sensors;

    /// <summary>Every zone, flattened parent-before-child for the picker.</summary>
    public IReadOnlyList<ZoneOption> Zones { get; private set; } = [];

    /// <summary>Whether sensors and zones have loaded at least once.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Why the last load failed, if it did.</summary>
    public ApiProblem? LoadProblem { get; private set; }

    /// <summary>The registration form's draft, held here so it survives navigation.</summary>
    public SensorRegistrationModel Draft { get; } = new();

    /// <summary>Loads zones and sensors together.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadProblem = null;
        Changed?.Invoke();

        // Both requests are started before either is awaited, so they run concurrently rather than
        // one after the other.
        var zonesCall = api.GetZoneTreeAsync(cancellationToken);
        var sensorsCall = api.GetSensorsAsync(cancellationToken);

        var zones = await zonesCall;
        var sensors = await sensorsCall;

        connection.Report(Reached(zones) && Reached(sensors));

        if (!zones.IsSuccess)
        {
            LoadProblem = zones.Problem;
        }
        else if (!sensors.IsSuccess)
        {
            LoadProblem = sensors.Problem;
        }
        else
        {
            Zones = ZoneOptions.Flatten(zones.Value);
            _sensors = [.. sensors.Value];
            IsLoaded = true;
        }

        Changed?.Invoke();
    }

    /// <summary>Registers a sensor and, on success, adds it to the list.</summary>
    /// <remarks>
    /// A 409 comes back from the API as a plain problem with no field attached, because to the API a
    /// duplicate is a conflict with existing state rather than a malformed field. To the person
    /// filling the form it is unambiguously about one field — the MAC they typed — so it is attached
    /// there, and the form shows it under the MAC box like any other field error.
    /// </remarks>
    public async Task<ApiResult<SensorRegistration>> RegisterAsync(
        SensorRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await api.RegisterSensorAsync(request, cancellationToken);

        connection.Report(Reached(result));

        if (result.IsSuccess)
        {
            // Inserted at its MAC-ordered position, so the new row lands where a reload would put it.
            var index = _sensors.FindIndex(sensor =>
                string.CompareOrdinal(sensor.MacAddress, result.Value.MacAddress) > 0);

            _sensors.Insert(index < 0 ? _sensors.Count : index, result.Value);
            Changed?.Invoke();

            return result;
        }

        if (result.Problem.Status == 409)
        {
            return ApiResult<SensorRegistration>.Failure(new ApiProblem(
                409,
                result.Problem.Title,
                result.Problem.Detail,
                new Dictionary<string, string[]>
                {
                    [nameof(SensorRegistrationRequest.MacAddress)] = [result.Problem.Message]
                }));
        }

        return result;
    }

    /// <summary>The full path of a zone, for display, or its id if it is not known.</summary>
    public string ZonePath(string zoneId) =>
        Zones.FirstOrDefault(zone => string.Equals(zone.Id, zoneId, StringComparison.OrdinalIgnoreCase))?.Path
        ?? zoneId;

    private static bool Reached<T>(ApiResult<T> result) => result.Problem is not { IsUnreachable: true };
}
