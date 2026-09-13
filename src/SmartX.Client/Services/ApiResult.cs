using System.Diagnostics.CodeAnalysis;

namespace SmartX.Client.Services;

/// <summary>
/// What went wrong with an API call, in a shape a component can display.
/// </summary>
/// <remarks>
/// Carries the server's field errors keyed by field name rather than flattened into one sentence.
/// That is what lets the registration form put "zone 1 permits 0–50 °C" under the zone picker and
/// "not a valid MAC" under the MAC box, instead of showing both in a banner the user has to match up
/// with the form themselves.
/// </remarks>
public sealed class ApiProblem
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>();

    public ApiProblem(
        int? status,
        string title,
        string? detail = null,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        Status = status;
        Title = title;
        Detail = detail;

        // Case-insensitive, because the key's casing depends on the server's JSON naming policy and
        // the client should not break if that policy changes.
        FieldErrors = fieldErrors is null
            ? NoFieldErrors
            : new Dictionary<string, string[]>(fieldErrors, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The HTTP status, or null when the API could not be reached at all.</summary>
    public int? Status { get; }

    public string Title { get; }

    public string? Detail { get; }

    /// <summary>Validation messages keyed by the request field they belong to.</summary>
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    /// <summary>True when nothing answered, as opposed to the API answering with an error.</summary>
    public bool IsUnreachable => Status is null;

    /// <summary>The most specific sentence available.</summary>
    public string Message => Detail ?? Title;

    /// <summary>The messages for one field, or none.</summary>
    public IReadOnlyList<string> ErrorsFor(string field) =>
        FieldErrors.TryGetValue(field, out var messages) ? messages : [];

    /// <summary>The problem reported when no response came back.</summary>
    /// <summary>Reads a problem from a raw response body, for requests made outside HttpClient.</summary>
    /// <remarks>
    /// Uploads go through XMLHttpRequest, which hands back a status and a string rather than an
    /// <see cref="HttpResponseMessage"/>. When the body is not a problem document, the status alone still
    /// gives a meaningful message, because the upload endpoint's statuses each mean one specific thing.
    /// </remarks>
    public static ApiProblem FromResponse(int status, string? body)
    {
        if (status <= 0)
        {
            return Unreachable();
        }

        string? title = null;
        string? detail = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(body);
                var root = document.RootElement;

                if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
                    detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not a problem document. Fall back to what the status means.
            }
        }

        return new ApiProblem(status, title ?? status switch
        {
            413 => "The file is too large.",
            415 => "That file type is not permitted.",
            404 => "The sensor is no longer registered.",
            _ => $"The upload failed with status {status}."
        }, detail);
    }

    public static ApiProblem Unreachable() =>
        new(null, "The API could not be reached.", "Check that SmartX.Api is running, then retry.");
}

/// <summary>
/// The outcome of an API call: a value, or a problem.
/// </summary>
/// <typeparam name="T">The value a successful call returns.</typeparam>
/// <remarks>
/// Expected failures — a validation rejection, a 404, an API that is not running — come back as a
/// result rather than an exception. They are ordinary states the UI has to render, and routing them
/// through exceptions would mean every page wrapping every call in try/catch, with the ones that
/// forget blanking the page. A generic struct, so the result adds no allocation of its own.
/// </remarks>
public readonly record struct ApiResult<T>
{
    private ApiResult(T? value, ApiProblem? problem)
    {
        Value = value;
        Problem = problem;
    }

    public T? Value { get; }

    public ApiProblem? Problem { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Problem))]
    public bool IsSuccess => Problem is null;

    public static ApiResult<T> Success(T value) => new(value, null);

    public static ApiResult<T> Failure(ApiProblem problem) => new(default, problem);
}
