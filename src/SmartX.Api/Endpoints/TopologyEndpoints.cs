using System.Security.Claims;
using Microsoft.Extensions.Options;
using SmartX.Api.Security;
using SmartX.Api.Storage;
using SmartX.Api.Topology;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>The mesh: devices reporting their neighbours, and the graph read back.</summary>
internal static class TopologyEndpoints
{
    /// <summary>A device reporting its own neighbours. Mapped on the operator-or-device group, like telemetry: a device acts for its own MAC.</summary>
    internal static RouteGroupBuilder MapNeighbourReportEndpoint(this RouteGroupBuilder api)
    {
        api.MapPost("/devices/{mac}/neighbours", ReportNeighboursAsync).WithName("ReportNeighbours");

        return api;
    }

    /// <summary>Reading the mesh. Mapped on the console group.</summary>
    internal static RouteGroupBuilder MapTopologyReadEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/topology", GetTopology).WithName("GetTopology");
        api.MapGet("/topology/reachability", GetReachability).WithName("GetTopologyReachability");
        api.MapGet("/topology/articulation-points", GetArticulationPoints).WithName("GetTopologyArticulationPoints");
        api.MapGet("/topology/mst", GetMst).WithName("GetTopologyMst");
        api.MapGet("/topology/route/{mac}", GetRouteAsync).WithName("GetTopologyRoute");
        api.MapGet("/topology/route-changes", GetRouteChanges).WithName("GetTopologyRouteChanges");

        return api;
    }

    private static async Task<IResult> ReportNeighboursAsync(
        ClaimsPrincipal user,
        string mac,
        NeighbourReport report,
        ISensorRegistry registry,
        TopologyService topology,
        IOptions<TopologyOptions> options,
        CancellationToken cancellationToken)
    {
        if (DeviceScope.Require(user, mac) is { } forbidden)
        {
            return forbidden;
        }

        if (!MacAddressValidator.TryNormalise(mac, out var canonical))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["mac"] = [$"'{mac}' is not a valid MAC address."] },
                title: "The neighbour report is not valid");
        }

        var errors = Validate(report, canonical, options.Value.MaxNeighbours);

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "The neighbour report is not valid");
        }

        var sensor = await registry.FindAsync(canonical, cancellationToken);

        if (sensor is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{mac}'.");
        }

        // Ids are stored in one canonical form, so a device that writes a neighbour's MAC another way still links to it.
        var neighbours = report.Neighbours.Select(link => link with { Id = CanonicalId(link.Id) }).ToList();

        topology.Submit(canonical, sensor.Location.Room, sensor.Location.ZoneId, report with { Neighbours = neighbours });

        return Results.NoContent();
    }

    private static Dictionary<string, string[]> Validate(NeighbourReport report, string self, int maxNeighbours)
    {
        Dictionary<string, string[]> errors = [];

        if (!double.IsFinite(report.X) || !double.IsFinite(report.Y))
        {
            errors["position"] = ["'x' and 'y' must be finite numbers."];
        }

        if (report.Neighbours is null)
        {
            errors["neighbours"] = ["'neighbours' is required. Send an empty list if nothing is in range."];

            return errors;
        }

        if (report.Neighbours.Count > maxNeighbours)
        {
            errors["neighbours"] = [$"A report may list at most {maxNeighbours} neighbours."];

            return errors;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in report.Neighbours)
        {
            if (link is null || string.IsNullOrWhiteSpace(link.Id) || !IsKnownIdForm(link.Id))
            {
                errors["neighbours"] = ["Each neighbour must be a valid MAC address or a gateway id."];

                break;
            }

            if (CanonicalId(link.Id) == self)
            {
                errors["neighbours"] = ["A device cannot list itself as a neighbour."];

                break;
            }

            if (!seen.Add(CanonicalId(link.Id)))
            {
                errors["neighbours"] = ["A neighbour may be listed only once."];

                break;
            }

            if (link.Rssi is < -100 or > 0)
            {
                errors["neighbours"] = ["'rssi' must be between -100 and 0 dBm."];

                break;
            }
        }

        return errors;
    }

    private static bool IsKnownIdForm(string id) => MeshGateways.IsGateway(id) || MacAddressValidator.TryNormalise(id, out _);

    private static string CanonicalId(string id) =>
        MeshGateways.All.FirstOrDefault(gateway => string.Equals(gateway.Id, id, StringComparison.OrdinalIgnoreCase))?.Id
        ?? MacAddressValidator.Normalise(id);

    /// <summary>Every device and gateway in the mesh, with every link.</summary>
    private static IResult GetTopology(TopologyService topology)
    {
        var snapshot = topology.Current;

        return Results.Ok(new TopologyView(
            snapshot.BuiltAt,
            [.. snapshot.Graph.Vertices.Select(vertex => new TopologyNode(
                vertex.Id, vertex.Name, vertex.ZoneId, vertex.X, vertex.Y, vertex.Band, vertex.Trust, vertex.IsGateway))],
            [.. snapshot.Graph.Edges.Select(edge => new TopologyEdge(edge.A, edge.B, edge.Rssi, edge.Cost))],
            snapshot.Excluded));
    }

    /// <summary>Each device's nearest gateway and hop count, and the devices that cannot reach one.</summary>
    private static IResult GetReachability(TopologyService topology)
    {
        var snapshot = topology.Current;
        var reachability = snapshot.Reachability;

        return Results.Ok(new ReachabilityView(
            snapshot.BuiltAt,
            [.. reachability.Routes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new DeviceRoute(pair.Key, pair.Value.Gateway, pair.Value.Hops))],
            reachability.CutOff));
    }

    /// <summary>The devices and gateways whose failure would split the mesh, and who would be cut off.</summary>
    private static IResult GetArticulationPoints(TopologyService topology)
    {
        var snapshot = topology.Current;

        return Results.Ok(new ArticulationView(
            snapshot.BuiltAt,
            [.. snapshot.ArticulationPoints.Select(point =>
            {
                snapshot.Graph.TryGetVertex(point.Id, out var vertex);

                return new ArticulationPointView(point.Id, vertex.Name, point.IsGateway, point.CutOff);
            })]));
    }

    /// <summary>The backbone: the cheapest links that connect every reachable device to a gateway, one tree per gateway.</summary>
    private static IResult GetMst(TopologyService topology)
    {
        var snapshot = topology.Current;
        var forest = snapshot.Forest;

        return Results.Ok(new MstView(
            snapshot.BuiltAt,
            [.. forest.Edges.Select(edge => new TopologyEdge(edge.A, edge.B, edge.Rssi, edge.Cost))],
            [.. forest.WeightByGateway.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                snapshot.Graph.TryGetVertex(pair.Key, out var gateway);

                return new MstTreeView(pair.Key, gateway.Name, pair.Value, forest.GatewayOf.Count(entry => entry.Value == pair.Key && entry.Key != pair.Key));
            })],
            forest.TotalWeight,
            snapshot.Reachability.CutOff));
    }

    /// <summary>
    /// One device's route to its gateway along the tree, with the cheapest cost it could have had by any path for comparison. An
    /// unregistered device is a 404; a registered one that is not in the graph is a route that is cut off, and says why.
    /// </summary>
    private static async Task<IResult> GetRouteAsync(string mac, ISensorRegistry registry, TopologyService topology, CancellationToken cancellationToken)
    {
        if (!MacAddressValidator.TryNormalise(mac, out var canonical) || await registry.FindAsync(canonical, cancellationToken) is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Sensor not registered",
                detail: $"No sensor is registered with MAC address '{mac}'.");
        }

        var snapshot = topology.Current;

        if (!snapshot.Routes.TryGetValue(canonical, out var route))
        {
            var reason = snapshot.Excluded.FirstOrDefault(excluded => excluded.Id == canonical)?.Reason
                ?? (snapshot.Graph.Contains(canonical) ? "No path to any gateway" : "No neighbour report yet");

            return Results.Ok(new RoutePathView(snapshot.BuiltAt, canonical, true, reason, null, null, null, null, []));
        }

        return Results.Ok(new RoutePathView(
            snapshot.BuiltAt,
            canonical,
            false,
            null,
            route.Gateway,
            route.Hops,
            route.Cost,
            snapshot.ShortestCosts.TryGetValue(canonical, out var shortest) ? shortest : null,
            [.. route.Path.Select(id =>
            {
                snapshot.Graph.TryGetVertex(id, out var vertex);

                return new RouteHop(id, vertex.Name);
            })]));
    }

    /// <summary>Routes that changed when the mesh was rebuilt, newest first. The history is bounded, so this is the recent past only.</summary>
    private static IResult GetRouteChanges(int? limit, IOptions<TopologyOptions> options, TopologyService topology)
    {
        var count = limit ?? 50;

        if (count < 1 || count > options.Value.RouteChangeHistory)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["limit"] = [$"'limit' must be between 1 and {options.Value.RouteChangeHistory}."] },
                title: "The route change request is not valid");
        }

        _ = topology.Current;

        return Results.Ok(topology.RouteChanges(count)
            .Select(change => change.ToView())
            .ToList());
    }
}
