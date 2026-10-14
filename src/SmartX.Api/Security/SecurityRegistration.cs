using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SmartX.Shared;

namespace SmartX.Api.Security;

/// <summary>Wires authentication, authorization, the login limits and their start-up checks into the application.</summary>
internal static class SecurityRegistration
{
    /// <summary>The name of the rate-limiter policy on the login endpoint.</summary>
    public const string LoginRateLimit = "login";

    public static IServiceCollection AddSmartXSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        // Development, and only Development, may leave the two secrets unset and be given the published ones. Anywhere else, the
        // validator below refuses to start without them: an API that falls back to a known signing key in production is an API
        // anyone can log in to.
        services
            .AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .PostConfigure<IHostEnvironment>((options, environment) =>
            {
                if (environment.IsDevelopment())
                {
                    options.SigningKey = string.IsNullOrWhiteSpace(options.SigningKey) ? AuthDefaults.DevelopmentSigningKey : options.SigningKey;
                    options.FleetSecret = string.IsNullOrWhiteSpace(options.FleetSecret) ? AuthDefaults.DevelopmentFleetSecret : options.FleetSecret;
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();

        services.AddSingleton<UserDirectory>();
        services.AddSingleton<PasswordVerifier>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<TokenService>();
        services.AddHostedService<AuthStartupCheck>();

        services
            .AddAuthentication(SchemeNames.Bearer)
            .AddJwtBearer(SchemeNames.Bearer, _ => { })
            .AddScheme<AuthenticationSchemeOptions, DeviceKeyAuthenticationHandler>(SchemeNames.Device, _ => { });

        services
            .AddOptions<JwtBearerOptions>(SchemeNames.Bearer)
            .Configure<IOptions<AuthOptions>, TimeProvider>((jwt, auth, time) =>
            {
                jwt.TokenValidationParameters = TokenService.ValidationFor(auth.Value, time);
                jwt.SaveToken = false;
                jwt.MapInboundClaims = false;

                // A browser cannot put a header on a WebSocket, so SignalR sends the token as ?access_token=. It is read from the
                // query string for the hub's path and for nothing else: accepting a token from a URL on every endpoint would
                // make every request's URL (and every log, proxy and history that records one) a place a credential can leak.
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];

                        if (!StringValues.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(RealtimeTopics.HubPath))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddSmartXPolicies();

        // The login endpoint is the one place a password is guessed, so it is the one place that is rate limited. Partitioned by
        // client address, with the numbers read per request so a test, or a deployment, can change them without rebuilding.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(LoginRateLimit, context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;

                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.LoginPermitLimit,
                        Window = settings.LoginWindow,
                        QueueLimit = 0,
                    });
            });
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var settings = context.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;

                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(settings.LoginWindow.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many login attempts",
                    detail: "Too many attempts from this address. Wait a moment and try again.")
                    .ExecuteAsync(context.HttpContext);
            };
        });

        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // The proxy is nginx on the compose network, whose address is not known in advance. This is only in effect when
            // Auth:TrustForwardedHeaders is set, which is a statement that the API is reached only through that proxy.
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
        });

        return services;
    }

    /// <summary>
    /// Adds the middleware in the one order that works: forwarded addresses first, so the limiter sees the client and not the
    /// proxy; the limiter before authentication, so a flood of guesses is refused cheaply; then who you are, then what you may do.
    /// </summary>
    public static WebApplication UseSmartXSecurity(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.TrustForwardedHeaders)
        {
            app.UseForwardedHeaders();
        }

        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        return app;
    }
}

/// <summary>Says so, loudly, when the published development secrets or demo users are in use anywhere but Development.</summary>
internal sealed class AuthStartupCheck(
    IOptions<AuthOptions> options,
    UserDirectory users,
    PasswordVerifier passwords,
    IHostEnvironment environment,
    ILogger<AuthStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        var settings = options.Value;

        if (settings.SigningKey == AuthDefaults.DevelopmentSigningKey)
        {
            logger.LogWarning(
                "Tokens are signed with the development key published in the README. That is fine for a local Docker demo, but " +
                "anyone with the repository can forge a token. Set Auth:SigningKey for any other deployment.");
        }

        if (settings.FleetSecret == AuthDefaults.DevelopmentFleetSecret)
        {
            logger.LogWarning(
                "Device keys are derived from the development fleet secret published in the README, so anyone with the repository " +
                "can derive any device's key. Set Auth:FleetSecret for any other deployment.");
        }

        foreach (var account in users.All.Where(passwords.UsesPublishedDemoPassword))
        {
            logger.LogWarning(
                "The user {Username} has the demo password published in the README. Fine for a local demo; remove the demo users " +
                "(Auth:SeedDemoUsers=false) and add your own (Auth:Users) for anything else.",
                account.Username);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
