using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using SmartX.Api.Security;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Logging in, and asking who you are.
/// </summary>
/// <remarks>
/// <b>One message for every failed login.</b> "Incorrect username or password" is the answer whether the name does not exist or the
/// password is wrong, with the same status and, because an unknown name is still hashed (see <see cref="PasswordVerifier"/>), about
/// the same time. Telling the two apart would let anyone ask the login form which usernames exist.
/// <para>
/// A password is read from the request, checked and dropped. It is not stored, not put in a log line, not echoed back, and not part
/// of any exception message.
/// </para>
/// </remarks>
internal static class AuthEndpoints
{
    private const string BadCredentials = "Incorrect username or password.";

    /// <summary>The public login endpoint. Rate limited; the only part of the API besides health that needs no token.</summary>
    internal static RouteGroupBuilder MapLoginEndpoint(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", Login)
            .WithName("Login")
            .AllowAnonymous()
            .RequireRateLimiting(SecurityRegistration.LoginRateLimit);

        return api;
    }

    /// <summary>Who the caller is, on the group that already requires a token.</summary>
    internal static RouteGroupBuilder MapCurrentUserEndpoint(this RouteGroupBuilder api)
    {
        api.MapGet("/auth/me", Me).WithName("CurrentUser");

        return api;
    }

    private static IResult Login(
        LoginRequest request,
        UserDirectory users,
        PasswordVerifier passwords,
        LoginThrottle throttle,
        TokenService tokens,
        HttpContext http,
        ILogger<LoginRequest> logger)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["credentials"] = ["Enter a username and a password."] },
                title: "The login is incomplete");
        }

        // Checked before the password is, so a locked account is not an oracle for guesses: while it is locked even the right
        // password is refused, and the answer says nothing about whether the name exists.
        if (throttle.LockedFor(request.Username) is { } wait)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));

            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            logger.LogWarning("A login for {Username} was refused: too many failed attempts.", request.Username);

            return Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many failed attempts",
                detail: $"Too many failed attempts. Try again in {seconds} seconds.");
        }

        var account = users.Find(request.Username);

        if (!passwords.Verify(account, request.Password))
        {
            throttle.RecordFailure(request.Username);
            logger.LogWarning("A login for {Username} failed.", request.Username);

            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Login failed", detail: BadCredentials);
        }

        throttle.RecordSuccess(request.Username);
        logger.LogInformation("{Username} logged in as {Role}.", account!.Username, account.Role);

        return Results.Ok(tokens.Issue(account));
    }

    private static IResult Me(ClaimsPrincipal user)
    {
        var role = user.FindFirst(JwtClaims.Role)?.Value ?? "";
        var name = user.FindFirst("name")?.Value ?? user.Identity?.Name ?? "";
        var expiry = user.FindFirst("exp")?.Value;

        DateTimeOffset? expiresAt = long.TryParse(expiry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

        return Results.Ok(new CurrentUser(name, role, expiresAt));
    }
}
