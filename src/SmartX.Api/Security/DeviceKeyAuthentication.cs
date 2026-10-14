using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Security;

/// <summary>
/// Authenticates a device by its per-device key: <c>X-Device-Mac</c> and <c>X-Device-Key</c> on every request.
/// </summary>
/// <remarks>
/// The key is recomputed from the fleet secret and the MAC (<see cref="DeviceKeys"/>) and compared, so the gateway keeps no table of
/// keys. The principal this produces holds the role Device and one claim, the MAC it proved. That MAC is the only thing it may act
/// for: see <see cref="DeviceScope"/>.
/// <para>
/// Headers absent is "no opinion", not a failure, so a request carrying an operator's token passes through this scheme untouched.
/// Headers present and wrong is a failure, and answers 401.
/// </para>
/// </remarks>
internal sealed class DeviceKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthOptions> auth) : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    /// <summary>The claim carrying the MAC the device proved.</summary>
    public const string MacClaim = "device_mac";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var mac = Request.Headers[DeviceAuthHeaders.Mac].ToString();
        var key = Request.Headers[DeviceAuthHeaders.Key].ToString();

        if (mac.Length == 0 && key.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!MacAddressValidator.TryNormalise(mac, out var canonical))
        {
            return Task.FromResult(AuthenticateResult.Fail("The device MAC address is not valid."));
        }

        // One message for every way it can be wrong: a missing key, a wrong one and a MAC nobody has registered must not
        // be told apart, because the difference would tell a guesser how close they were.
        if (!DeviceKeys.Verify(auth.Value.FleetSecret!, canonical, key))
        {
            Logger.LogWarning("A device key was refused for {Mac}.", canonical);

            return Task.FromResult(AuthenticateResult.Fail("The device key is not valid."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, canonical),
                new Claim(JwtClaims.Role, UserRoles.Device),
                new Claim(MacClaim, canonical),
            ],
            Scheme.Name,
            ClaimTypes.Name,
            JwtClaims.Role);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "DeviceKey";

        return base.HandleChallengeAsync(properties);
    }
}

/// <summary>
/// What a caller may act for. An Operator may act for any device; a device may act for itself and for no other.
/// </summary>
/// <remarks>
/// A valid key proves a device is who it says it is, and says nothing about what it may touch. Without this check, any one device's
/// key would let it post readings as every other device. The answer when a key is good but the MAC is someone else's is 403
/// (you are known, and not allowed) and not 401 (you are not known), because the two call for different behaviour: a 401 means the
/// device's credentials are wrong, a 403 means the device is doing something it should not.
/// </remarks>
internal static class DeviceScope
{
    /// <summary>Null if the caller may act for this MAC; otherwise the 403 to return.</summary>
    public static IResult? Require(ClaimsPrincipal user, string macAddress)
    {
        if (MayActFor(user, macAddress))
        {
            return null;
        }

        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Not your device",
            detail: "A device may only act for its own MAC address.");
    }

    /// <summary>Is the caller an Operator, or the device with this MAC?</summary>
    public static bool MayActFor(ClaimsPrincipal user, string macAddress)
    {
        if (user.IsInRole(UserRoles.Operator))
        {
            return true;
        }

        var proved = user.FindFirst(DeviceKeyAuthenticationHandler.MacClaim)?.Value;

        return proved is not null && string.Equals(proved, DeviceKeys.Canonical(macAddress), StringComparison.OrdinalIgnoreCase);
    }
}
