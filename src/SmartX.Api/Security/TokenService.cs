using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartX.Shared;

namespace SmartX.Api.Security;

/// <summary>Issues the JWT a login returns, and builds the parameters the bearer scheme checks one against.</summary>
/// <remarks>
/// The token is signed with HMAC-SHA256 under a key only the gateway holds, so it can be checked without a lookup: there is no
/// session table. The cost of that is that a token cannot be taken back before it expires, which is why its lifetime is short and
/// configurable. It carries the name and the role, which is everything a request needs, so authorization never touches the user store.
/// </remarks>
internal sealed class TokenService(IOptions<AuthOptions> options, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Signs a token for an account, valid from now for the configured lifetime.</summary>
    public LoginResponse Issue(UserAccount account)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var expires = now + settings.TokenLifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = account.Username,
                [JwtRegisteredClaimNames.Name] = account.Username,
                [JwtClaims.Role] = account.Role,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(KeyFor(settings), SecurityAlgorithms.HmacSha256),
        };

        return new LoginResponse(_handler.CreateToken(descriptor), expires, account.Username, account.Role);
    }

    /// <summary>What the bearer scheme holds every incoming token to.</summary>
    public static TokenValidationParameters ValidationFor(AuthOptions settings, TimeProvider time) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = settings.Issuer,
        ValidateAudience = true,
        ValidAudience = settings.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = KeyFor(settings),

        // Only HMAC-SHA256: a token that says it is signed some other way (or "none") is refused, which is the classic way to
        // forge a JWT against a library that trusts the token's own header.
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

        RequireExpirationTime = true,
        RequireSignedTokens = true,
        // The lifetime is checked against the injected clock and not the machine's, so expiry can be tested by moving a fake
        // clock instead of waiting an hour. The skew is the configured one: the library's own default is five minutes.
        ValidateLifetime = true,
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = time.GetUtcNow().UtcDateTime;

            return expires is not null
                && now <= expires.Value.Add(settings.ClockSkew)
                && (notBefore is null || now >= notBefore.Value.Subtract(settings.ClockSkew));
        },
        NameClaimType = JwtRegisteredClaimNames.Name,
        RoleClaimType = JwtClaims.Role,
    };

    private static SymmetricSecurityKey KeyFor(AuthOptions settings) => new(Encoding.UTF8.GetBytes(settings.SigningKey!));
}

/// <summary>The names of the claims in the token.</summary>
internal static class JwtClaims
{
    /// <summary>The role claim, <c>role</c>, which the bearer scheme is told to read roles from.</summary>
    public const string Role = "role";
}
