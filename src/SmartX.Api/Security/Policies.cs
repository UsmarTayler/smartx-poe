using Microsoft.AspNetCore.Authorization;
using SmartX.Shared;

namespace SmartX.Api.Security;

/// <summary>
/// The authorization policies, by name. Endpoints are protected by applying one of these to a <em>group</em>, so an endpoint added
/// to the group is protected without anyone remembering to.
/// </summary>
/// <remarks>
/// Nothing in an endpoint's code asks for a role. A role string typed in forty places is forty chances to mistype one and leave a
/// write open to a Viewer; here the roles appear once, in <see cref="AddSmartXPolicies"/>, and an endpoint only says which policy
/// it is under.
/// </remarks>
internal static class Policies
{
    /// <summary>
    /// The operator console. A read (GET, HEAD, OPTIONS) needs a Viewer or an Operator; anything else needs an Operator. The default
    /// for everything an operator's browser calls, so a new endpoint is safe whichever way it was written: a new GET is readable by a
    /// Viewer, and a new POST, PUT or DELETE is not.
    /// </summary>
    public const string Console = "Console";

    /// <summary>A Viewer or an Operator, and nothing else. For the real-time hub.</summary>
    public const string Viewer = "Viewer";

    /// <summary>An Operator only.</summary>
    public const string Operator = "Operator";

    /// <summary>A device with a valid key, and no one else. For what only a device should ever do: collect its commands, acknowledge them.</summary>
    public const string Device = "Device";

    /// <summary>
    /// An Operator, or a device with a valid key. For what a device does and an operator may also do by hand: send a reading,
    /// register. Which MAC the caller may act for is checked by <see cref="DeviceScope"/> in the endpoint.
    /// </summary>
    public const string OperatorOrDevice = "OperatorOrDevice";
}

/// <summary>The scheme names.</summary>
internal static class SchemeNames
{
    /// <summary>The operator's JWT bearer token.</summary>
    public const string Bearer = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;

    /// <summary>A device's per-device key.</summary>
    public const string Device = "Device";
}

/// <summary>Reads are for anyone signed in to the console; writes are for Operators.</summary>
internal sealed class ConsoleAccessRequirement : IAuthorizationRequirement;

/// <summary>Decides <see cref="ConsoleAccessRequirement"/> from the request's method and the caller's role.</summary>
internal sealed class ConsoleAccessHandler : AuthorizationHandler<ConsoleAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ConsoleAccessRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            // Not something this policy was written for. Failing closed is the only safe answer.
            return Task.CompletedTask;
        }

        var isRead = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method);
        var allowed = isRead
            ? context.User.IsInRole(UserRoles.Viewer) || context.User.IsInRole(UserRoles.Operator)
            : context.User.IsInRole(UserRoles.Operator);

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Registers the policies.</summary>
internal static class PolicyRegistration
{
    public static IServiceCollection AddSmartXPolicies(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, ConsoleAccessHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Console, policy => policy
                .AddAuthenticationSchemes(SchemeNames.Bearer)
                .RequireAuthenticatedUser()
                .AddRequirements(new ConsoleAccessRequirement()))
            .AddPolicy(Policies.Viewer, policy => policy
                .AddAuthenticationSchemes(SchemeNames.Bearer)
                .RequireRole(UserRoles.Viewer, UserRoles.Operator))
            .AddPolicy(Policies.Operator, policy => policy
                .AddAuthenticationSchemes(SchemeNames.Bearer)
                .RequireRole(UserRoles.Operator))
            .AddPolicy(Policies.Device, policy => policy
                .AddAuthenticationSchemes(SchemeNames.Device)
                .RequireRole(UserRoles.Device))
            .AddPolicy(Policies.OperatorOrDevice, policy => policy
                .AddAuthenticationSchemes(SchemeNames.Bearer, SchemeNames.Device)
                .RequireRole(UserRoles.Operator, UserRoles.Device));

        return services;
    }
}
