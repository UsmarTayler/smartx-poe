namespace SmartX.Client.Auth;

/// <summary>Where to go after signing in, accepted only if it is a place on this site.</summary>
public static class ReturnAddress
{
    /// <summary>
    /// A return address is accepted only if it is a path on this site. A login link can be written by anyone, so a return address of
    /// <c>https://elsewhere.example</c> or <c>//elsewhere.example</c> would turn the login page into a way to send a user, freshly
    /// signed in and trusting, to somewhere else. The login page itself is refused too, which would loop.
    /// </summary>
    public static string Safe(string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal)
        && !url.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            ? url
            : "/";
}
