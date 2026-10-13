using SmartX.Client.Services;
using SmartX.Client.State;
using SmartX.Shared;

namespace SmartX.Client.CommandStream;

/// <summary>What happened when an operator acted on a suggestion.</summary>
/// <param name="Succeeded">Whether it was done.</param>
/// <param name="Message">What to tell the operator: what was done, or why it was not.</param>
public sealed record SuggestionOutcome(bool Succeeded, string Message);

/// <summary>
/// What a click on a suggestion does, in one place, so the Command Stream panel and the strip on the trust page do the same thing.
/// </summary>
/// <remarks>
/// <b>Accepting goes down the normal path.</b> A suggested command is the command an operator would have sent by hand: the same
/// API call, the same validation, the same state to watch (pending, delivered, acknowledged). The one addition is the suggestion's
/// id, so the server can tell that the operator followed advice and the engine can decline to learn from it. Opening a device is
/// the same selection the device list makes.
/// </remarks>
public sealed class SuggestionActions(IOperatorApi api, AppState state)
{
    /// <summary>Does what the suggestion says: sends the command, or opens the device.</summary>
    public async Task<SuggestionOutcome> AcceptAsync(Suggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        var action = suggestion.Action;

        if (action.Kind == SuggestedActionKind.Command && action.Command is { } command)
        {
            var result = await api.IssueCommandAsync(action.MacAddress, command, action.Level, suggestion.Id);

            return result.IsSuccess
                ? new SuggestionOutcome(true, $"Sent: {action.Label}. It is {result.Value.State.ToString().ToLowerInvariant()} until the device confirms it.")
                : new SuggestionOutcome(false, $"{action.Label} was not sent: {result.Problem.Message}");
        }

        state.SelectStreamDevice(action.MacAddress);

        // Not awaited past the call: the page must not wait on the gateway to note a click, and the client never throws for an
        // unreachable API, it answers with a problem.
        var problem = await api.RecordSelectionAsync(action.MacAddress, suggestion.Id);

        return new SuggestionOutcome(true, problem is null ? $"Opened {action.Room}." : $"Opened {action.Room}. The gateway could not be told: {problem.Message}");
    }

    /// <summary>Marks a suggestion "Not useful".</summary>
    public async Task<SuggestionOutcome> NotUsefulAsync(Suggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        var result = await api.DismissSuggestionAsync(suggestion.Id);

        return result.IsSuccess
            ? new SuggestionOutcome(true, $"Marked not useful: {suggestion.What}.")
            : new SuggestionOutcome(false, result.Problem.Status == 404 ? $"{suggestion.What} has already gone." : result.Problem.Message);
    }

    /// <summary>Takes a "Not useful" back.</summary>
    public async Task<SuggestionOutcome> UndoAsync(Suggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        var result = await api.RestoreSuggestionAsync(suggestion.Id);

        return result.IsSuccess
            ? new SuggestionOutcome(true, $"Taken back: {suggestion.What}.")
            : new SuggestionOutcome(false, result.Problem.Status == 404 ? $"{suggestion.What} can no longer be taken back." : result.Problem.Message);
    }
}
