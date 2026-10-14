using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using SmartX.Shared;

namespace SmartX.Client.Auth;

/// <summary>Tells Blazor who is signed in, from the session, so <c>[Authorize]</c> and <c>AuthorizeView</c> work.</summary>
public sealed class SmartXAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly AuthSession _session;

    public SmartXAuthenticationStateProvider(AuthSession session)
    {
        _session = session;
        _session.Changed += OnChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // The first question the app asks is "are you signed in", and the answer may be in sessionStorage. Reading it is
        // asynchronous, so the router waits here (showing "Authorizing") and not on a flash of the login page.
        await _session.RestoreAsync();

        return new AuthenticationState(PrincipalFor(_session.User));
    }

    private void OnChanged() => NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(PrincipalFor(_session.User))));

    private static ClaimsPrincipal PrincipalFor(AuthUser? user)
    {
        if (user is null)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role)],
            authenticationType: "SmartXToken",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));
    }

    public void Dispose() => _session.Changed -= OnChanged;
}

/// <summary>
/// Puts the bearer token on every request the typed API client makes, and ends the session if the API says the token is no good.
/// </summary>
/// <remarks>
/// One place does this, so no page, service or component ever handles a token. A <c>401</c> from anywhere means the same thing
/// (the credential was refused), and answering it here sends the user to the login page with a return URL instead of leaving each
/// page to show a different error. The login request itself is the exception on both counts: it carries no token, and its 401 means
/// "wrong password", which the login page shows.
/// </remarks>
public sealed class BearerTokenHandler(AuthSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = session.Token;
        var isLogin = request.RequestUri?.AbsolutePath.EndsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase) == true;

        if (token is not null && !isLogin)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // Only if the token that was refused is still the one in use: a slow response to an old request must not end a session
        // that has since been replaced by a fresh login.
        if (response.StatusCode == HttpStatusCode.Unauthorized && token is not null && !isLogin && session.Token == token)
        {
            await session.EndAsync(SessionEnd.Rejected);
        }

        return response;
    }
}
