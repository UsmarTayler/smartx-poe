using SmartX.Api.Patterns;
using SmartX.Api.Realtime;

namespace SmartX.Api.Endpoints;

/// <summary>
/// The suggested actions and automated insights, and the operator's "Not useful".
/// </summary>
/// <remarks>
/// The same state the <c>suggestions</c> topic pushes, available over HTTP for a client that is not connected and for anyone
/// checking what the engine thinks. Accepting a suggestion has no endpoint of its own: it is the command or the selection the
/// operator was going to make anyway, sent with the suggestion's id (see <c>IssueCommandRequest.SuggestionId</c>), so it goes down
/// the normal path with the normal validation, including a zone command's confirmation.
/// </remarks>
internal static class SuggestionEndpoints
{
    internal static RouteGroupBuilder MapSuggestionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/suggestions", Get).WithName("GetSuggestions");
        api.MapPost("/suggestions/{id}/dismiss", Dismiss).WithName("DismissSuggestion");
        api.MapDelete("/suggestions/{id}/dismiss", Restore).WithName("RestoreSuggestion");
        api.MapGet("/insights", Insights).WithName("GetInsights");

        return api;
    }

    /// <summary>The ranked suggestions, the insights and the recently dismissed, as of a sequence number.</summary>
    private static IResult Get(RealtimeBroadcaster broadcaster) => Results.Ok(broadcaster.GetSuggestionsSnapshot());

    private static IResult Insights(InsightService insights) => Results.Ok(insights.Current);

    /// <summary>
    /// Marks a suggestion "Not useful": it goes away and the weight of its pattern is lowered, within a floor. Answers with the
    /// state afterwards so the page can show it at once.
    /// </summary>
    private static IResult Dismiss(string id, ISuggestionService suggestions, RealtimeBroadcaster broadcaster) =>
        suggestions.Dismiss(id) == SuggestionOutcome.Done
            ? Results.Ok(broadcaster.GetSuggestionsSnapshot())
            : NotHeld(id);

    /// <summary>Takes a "Not useful" back: the suggestion returns, if its trigger is still live, and the weight is given back.</summary>
    private static IResult Restore(string id, ISuggestionService suggestions, RealtimeBroadcaster broadcaster) =>
        suggestions.Restore(id) == SuggestionOutcome.Done
            ? Results.Ok(broadcaster.GetSuggestionsSnapshot())
            : NotHeld(id);

    private static IResult NotHeld(string id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Suggestion not found",
            detail: $"No suggestion is held with id '{id}'. It may have been acted on, or its trigger may have left the window.");
}
