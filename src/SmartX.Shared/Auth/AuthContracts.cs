namespace SmartX.Shared;

/// <summary>The roles an operator can hold. Role names are the values carried in the token, so they are written once.</summary>
public static class UserRoles
{
    /// <summary>Can read everything and change nothing.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Can read everything and every action that changes something.</summary>
    public const string Operator = "Operator";

    /// <summary>
    /// A device, authenticated by its own key. Not a login role: nobody can ask for it, and it is only ever put on a principal by the
    /// device-key scheme.
    /// </summary>
    public const string Device = "Device";
}

/// <summary>The body of a POST to /api/auth/login.</summary>
/// <param name="Username">Who is logging in.</param>
/// <param name="Password">Their password. Sent once, over the wire, never stored by the client and never logged.</param>
public sealed record LoginRequest(string Username, string Password);

/// <summary>What a successful login returns.</summary>
/// <param name="Token">A signed JWT. Sent as <c>Authorization: Bearer</c> on every later request.</param>
/// <param name="ExpiresAt">When the token stops being accepted. The client asks again, or goes back to login, before then.</param>
/// <param name="Username">The user's name, as shown in the app bar.</param>
/// <param name="Role">Viewer or Operator.</param>
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, string Username, string Role);

/// <summary>The caller, as the API sees them. The answer to GET /api/auth/me.</summary>
/// <param name="Username">The user's name.</param>
/// <param name="Role">Viewer or Operator.</param>
/// <param name="ExpiresAt">When the token they are using stops being accepted.</param>
public sealed record CurrentUser(string Username, string Role, DateTimeOffset? ExpiresAt);
