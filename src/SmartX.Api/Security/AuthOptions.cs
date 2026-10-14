using System.Text;
using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Security;

/// <summary>
/// Everything about who may call the API: the token's signing key and lifetime, the fleet secret devices' keys come from,
/// the users, and the limits on logging in. Bound from the "Auth" section.
/// </summary>
/// <remarks>
/// Every value is an option and none is hard-wired: a secret, an expiry, a lockout threshold or a rate limit is changed by
/// setting <c>Auth__Something</c>, which is also how a deployment supplies real secrets. The development defaults live in
/// <see cref="AuthDefaults"/>, are published in the README, protect nothing, and are warned about outside Development.
/// </remarks>
internal sealed class AuthOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Auth";

    /// <summary>The key tokens are signed with (HMAC-SHA256). At least 32 bytes. Unset, Development uses <see cref="AuthDefaults.DevelopmentSigningKey"/>.</summary>
    public string? SigningKey { get; set; }

    /// <summary>The token's issuer claim. A token with another issuer is refused.</summary>
    public string Issuer { get; set; } = "smartx-api";

    /// <summary>The token's audience claim. A token with another audience is refused.</summary>
    public string Audience { get; set; } = "smartx-operators";

    /// <summary>How long a token is good for.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// How far a clock may be out before a token is called expired. Five minutes is the library's default, which makes a token that
    /// "expires in an hour" good for an hour and five minutes; this is short instead.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The secret every device's key is derived from, see <see cref="DeviceKeys"/>. Unset, Development uses
    /// <see cref="AuthDefaults.DevelopmentFleetSecret"/>.
    /// </summary>
    public string? FleetSecret { get; set; }

    /// <summary>
    /// Whether the two demo users (<c>operator</c> and <c>viewer</c>) exist. Unset, they exist in Development and not elsewhere; the
    /// Docker demo sets it.
    /// </summary>
    public bool? SeedDemoUsers { get; set; }

    /// <summary>Users beyond the demo ones. Each carries a password hash, never a password.</summary>
    public List<ConfiguredUser> Users { get; set; } = [];

    /// <summary>How many login attempts one client address may make in <see cref="LoginWindow"/>. The rate limiter's.</summary>
    public int LoginPermitLimit { get; set; } = 10;

    /// <summary>The window <see cref="LoginPermitLimit"/> counts in.</summary>
    public TimeSpan LoginWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How many failed logins in a row lock an account (whether or not the name exists). The lockout's.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long a locked account refuses even the right password.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether to believe <c>X-Forwarded-For</c>. Behind nginx every request comes from nginx's address, so without this the login
    /// rate limit is one bucket for everyone. Off by default because anyone who can reach the API directly can write that header; the
    /// Docker stack turns it on.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }
}

/// <summary>One configured user. A hash, made by <c>PasswordHasher</c>, and never the password.</summary>
internal sealed class ConfiguredUser
{
    /// <summary>The name they log in with. Case does not matter.</summary>
    public string Username { get; set; } = "";

    /// <summary>The ASP.NET Core Identity password hash.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Viewer or Operator.</summary>
    public string Role { get; set; } = UserRoles.Viewer;
}

/// <summary>The published development values. Public on purpose, and recognised so that using them elsewhere is warned about.</summary>
internal static class AuthDefaults
{
    /// <summary>Signs tokens in Development and in the Docker demo. Anyone who can read the repository can forge a token with it.</summary>
    public const string DevelopmentSigningKey = "smartx-development-signing-key-not-a-secret-0001";

    /// <summary>Derives device keys in Development and in the Docker demo. Anyone who can read the repository can derive any device's key.</summary>
    public const string DevelopmentFleetSecret = "smartx-development-fleet-secret-not-a-secret";

    /// <summary>The demo operator's password. Only its hash is in the code.</summary>
    public const string DemoOperatorPassword = "smartx-operator-demo";

    /// <summary>The demo viewer's password. Only its hash is in the code.</summary>
    public const string DemoViewerPassword = "smartx-viewer-demo";
}

/// <summary>Refuses to start with options that would leave the API unprotected or unusable, and says which.</summary>
internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    /// <summary>The shortest signing key HMAC-SHA256 should be given: its own block of key material.</summary>
    internal const int MinimumSigningKeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            problems.Add("Auth:SigningKey is required outside Development. Generate one with 'openssl rand -base64 48'.");
        }
        else if (Encoding.UTF8.GetByteCount(options.SigningKey) < MinimumSigningKeyBytes)
        {
            problems.Add($"Auth:SigningKey must be at least {MinimumSigningKeyBytes} bytes.");
        }

        if (string.IsNullOrWhiteSpace(options.FleetSecret))
        {
            problems.Add("Auth:FleetSecret is required outside Development. Generate one with 'openssl rand -base64 32'.");
        }
        else if (Encoding.UTF8.GetByteCount(options.FleetSecret) < 16)
        {
            problems.Add("Auth:FleetSecret must be at least 16 bytes.");
        }

        if (options.TokenLifetime <= TimeSpan.Zero)
        {
            problems.Add("Auth:TokenLifetime must be positive.");
        }

        if (options.ClockSkew < TimeSpan.Zero)
        {
            problems.Add("Auth:ClockSkew cannot be negative.");
        }

        if (options.LoginPermitLimit < 1)
        {
            problems.Add("Auth:LoginPermitLimit must be at least 1.");
        }

        if (options.LoginWindow <= TimeSpan.Zero)
        {
            problems.Add("Auth:LoginWindow must be positive.");
        }

        if (options.MaxFailedAttempts < 1)
        {
            problems.Add("Auth:MaxFailedAttempts must be at least 1.");
        }

        if (options.LockoutDuration <= TimeSpan.Zero)
        {
            problems.Add("Auth:LockoutDuration must be positive.");
        }

        foreach (var user in options.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Username) || string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                problems.Add("Every Auth:Users entry needs a Username and a PasswordHash (made with ASP.NET Core Identity's PasswordHasher, never a password).");
            }

            if (user.Role is not (UserRoles.Viewer or UserRoles.Operator))
            {
                problems.Add($"Auth:Users role '{user.Role}' is not one of {UserRoles.Viewer}, {UserRoles.Operator}.");
            }
        }

        if (options.SeedDemoUsers != true && options.Users.Count == 0)
        {
            problems.Add("There are no users, so nobody could log in. Set Auth:SeedDemoUsers=true for the demo users, or add Auth:Users.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
