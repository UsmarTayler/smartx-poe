using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Alerts;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// The alerts that are on, acknowledging them, and the totals that show duplicate prevention working.
/// </summary>
internal static class AlertEndpoints
{
    internal static RouteGroupBuilder MapAlertEndpoints(this RouteGroupBuilder api)
    {
        var alerts = api.MapGroup("/alerts");

        alerts.MapGet("", List).WithName("ListAlerts");
        alerts.MapPost("/acknowledge", Acknowledge).WithName("AcknowledgeAlert");
        alerts.MapGet("/stats", Stats).WithName("GetAlertStats");

        return api;
    }

    /// <summary>Every active alert, oldest first, each with how many times it has repeated.</summary>
    private static IResult List(AlertTracker alerts) => Results.Ok(alerts.Active());

    /// <summary>Marks an alert as seen. The condition is not touched.</summary>
    /// <remarks>
    /// 200 whether the alert was newly acknowledged or already was: acknowledging twice is what a double-click
    /// does, and it is not a conflict. 404 when there is no such active alert, which includes one that cleared
    /// between the operator seeing it and clicking.
    /// </remarks>
    private static IResult Acknowledge([FromBody] AlertKey key, AlertTracker alerts)
    {
        if (key.Mac is null || !MacAddressValidator.TryNormalise(key.Mac, out var mac))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [nameof(AlertKey.Mac)] = [$"'{key.Mac}' is not a valid MAC address."] },
                title: "The alert key is not valid");
        }

        var canonical = key with { Mac = mac };

        if (alerts.Acknowledge(canonical) == AcknowledgeOutcome.NotActive)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "No such active alert",
                detail: $"{canonical} is not active, so there is nothing to acknowledge.");
        }

        return Results.Ok(alerts.Active().First(alert => alert.Key == canonical));
    }

    /// <summary>Raised, suppressed and cleared totals since start-up.</summary>
    private static IResult Stats(AlertTracker alerts) => Results.Ok(alerts.Stats());
}
