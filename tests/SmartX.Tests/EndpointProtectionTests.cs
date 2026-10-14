using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Api.Security;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Looks at every endpoint the API has mapped, and fails if one is open that should not be.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> Protection is applied to endpoint groups, so a new endpoint is protected if it is mapped on one. But it is
/// still a person adding it, and the one mistake that matters, mapping something straight onto the app or onto <c>/api</c> and
/// forgetting, is invisible: the endpoint works, every test of its behaviour passes, and anyone on the network can call it. This
/// test reads the routing table itself (<see cref="EndpointDataSource"/>), not a list somebody keeps, so there is nothing to keep
/// up to date: a new endpoint is checked the moment it exists, and an unprotected one fails the build, not a review.
/// <para>
/// The exceptions are an explicit, short allow-list, and the test insists they stay exactly that: health and login are open on
/// purpose, and anything else that opens up is a failure with its name in the message.
/// </para>
/// </remarks>
public class EndpointProtectionTests
{
    /// <summary>The only endpoints that may be called with no credentials, by route.</summary>
    private static readonly string[] AllowAnonymous = ["/api/health", "/api/auth/login"];

    private static readonly string[] KnownPolicies =
        [Policies.Console, Policies.Viewer, Policies.Operator, Policies.Device, Policies.OperatorOrDevice];

    private static readonly HashSet<string> Reads = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    private sealed record Mapped(string Method, string Route, string Policy, bool Anonymous, string Name)
    {
        public bool IsRead => Reads.Contains(Method);

        public override string ToString() => $"{Method} {Route}";
    }

    private static List<Mapped> Endpoints(IServiceProvider services)
    {
        var mapped = new List<Mapped>();

        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            // Groups with an empty prefix leave a doubled or trailing slash in the raw pattern; they route the same.
            var route = Regex.Replace("/" + endpoint.RoutePattern.RawText!.TrimStart('/'), "/{2,}", "/");
            route = route.Length > 1 ? route.TrimEnd('/') : route;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            var policy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).FirstOrDefault(policy => policy is not null) ?? "";
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var protectedByAnything = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any();

            foreach (var method in methods)
            {
                mapped.Add(new Mapped(method, route, protectedByAnything ? policy : "", anonymous, endpoint.DisplayName ?? route));
            }
        }

        return mapped;
    }

    [Fact]
    public void Every_mapped_endpoint_requires_authorization_except_the_explicit_allow_list()
    {
        using var api = new AuthenticatedFactory();
        _ = api.CreateClient();

        var open = Endpoints(api.Services)
            .Where(endpoint => endpoint.Policy.Length == 0 && !AllowAnonymous.Contains(endpoint.Route))
            .Select(endpoint => endpoint.ToString())
            .ToList();

        Assert.True(open.Count == 0, "These endpoints are mapped without any authorization: " + string.Join(", ", open));
    }

    [Fact]
    public void The_allow_list_is_exactly_health_and_login_and_nothing_else_is_anonymous()
    {
        using var api = new AuthenticatedFactory();
        _ = api.CreateClient();

        var endpoints = Endpoints(api.Services);

        Assert.Equal(AllowAnonymous.Order(), endpoints.Where(endpoint => endpoint.Anonymous).Select(endpoint => endpoint.Route).Distinct().Order());

        foreach (var route in AllowAnonymous)
        {
            Assert.Contains(endpoints, endpoint => endpoint.Route == route && endpoint.Anonymous);
        }
    }

    [Fact]
    public void Every_protected_endpoint_names_a_policy_that_exists_so_a_typo_cannot_silently_protect_nothing()
    {
        using var api = new AuthenticatedFactory();
        _ = api.CreateClient();

        var unknown = Endpoints(api.Services)
            .Where(endpoint => endpoint.Policy.Length > 0 && !KnownPolicies.Contains(endpoint.Policy))
            .Select(endpoint => $"{endpoint} ({endpoint.Policy})");

        Assert.Empty(unknown);
    }

    [Fact]
    public void The_endpoint_list_is_not_empty_so_this_test_cannot_pass_by_looking_at_nothing()
    {
        using var api = new AuthenticatedFactory();
        _ = api.CreateClient();

        var endpoints = Endpoints(api.Services);

        Assert.True(endpoints.Count > 60, $"Only {endpoints.Count} endpoints were found.");
        Assert.Contains(endpoints, endpoint => endpoint.Route == "/api/commands" && endpoint.Method == "POST" && endpoint.Policy == Policies.Console);
        Assert.Contains(endpoints, endpoint => endpoint.Route == "/api/telemetry" && endpoint.Method == "POST" && endpoint.Policy == Policies.OperatorOrDevice);
        Assert.Contains(endpoints, endpoint => endpoint.Route == "/api/devices/{mac}/desired" && endpoint.Policy == Policies.Device);
        Assert.Contains(endpoints, endpoint => endpoint.Route.StartsWith("/hubs/", StringComparison.Ordinal) && endpoint.Policy == Policies.Viewer);
    }

    // ---- what each group does with each caller -----------------------------------------------------------

    private static string Fill(string route) => Regex.Replace(
        route,
        @"\{\*?([^}:?]+)(:[^}?]+)?\??\}",
        match => match.Groups[2].Value == ":guid" ? Guid.NewGuid().ToString()
            : match.Groups[1].Value.Contains("mac", StringComparison.OrdinalIgnoreCase) ? "AA:BB:CC:DD:EE:01"
            : "x");

    private static HttpRequestMessage Request(Mapped endpoint) =>
        new(new HttpMethod(endpoint.Method), Fill(endpoint.Route))
        {
            Content = endpoint.IsRead ? null : JsonContent.Create(new { }),
        };

    private static async Task<HttpStatusCode> SendAsync(HttpClient http, Mapped endpoint)
    {
        using var response = await http.SendAsync(Request(endpoint));

        return response.StatusCode;
    }

    [Fact]
    public async Task With_no_credentials_every_endpoint_except_the_allow_list_answers_401()
    {
        using var api = new AuthenticatedFactory();
        var http = api.Anonymous();
        var failures = new List<string>();

        foreach (var endpoint in Endpoints(api.Services).Where(endpoint => !AllowAnonymous.Contains(endpoint.Route)))
        {
            var status = await SendAsync(http, endpoint);

            if (status != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{endpoint} answered {(int)status}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public async Task A_viewer_may_read_every_console_endpoint_and_is_forbidden_every_one_that_changes_something()
    {
        using var api = new AuthenticatedFactory();
        var http = api.AsViewer();
        var failures = new List<string>();
        var console = Endpoints(api.Services).Where(endpoint => endpoint.Policy == Policies.Console).ToList();

        Assert.Contains(console, endpoint => !endpoint.IsRead);

        foreach (var endpoint in console)
        {
            var status = await SendAsync(http, endpoint);
            var allowed = endpoint.IsRead ? status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) : status == HttpStatusCode.Forbidden;

            if (!allowed)
            {
                failures.Add($"{endpoint} answered {(int)status} to a Viewer");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public async Task An_operator_is_not_refused_by_authorization_on_any_console_endpoint()
    {
        using var api = new AuthenticatedFactory();
        var http = api.CreateClient();
        var failures = new List<string>();

        foreach (var endpoint in Endpoints(api.Services).Where(endpoint => endpoint.Policy == Policies.Console))
        {
            var status = await SendAsync(http, endpoint);

            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                failures.Add($"{endpoint} answered {(int)status} to an Operator");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public async Task A_viewer_is_forbidden_to_register_a_sensor_and_to_send_a_reading()
    {
        using var api = new AuthenticatedFactory();
        var http = api.AsViewer();

        var register = await http.PostAsync("/api/sensors", ApiTestHost.Thermometer10To30());
        var reading = await http.PostAsync("/api/telemetry", ApiTestHost.Json("""{ "macAddress": "AA:BB:CC:DD:EE:01", "value": 20 }"""));
        var batch = await http.PostAsync("/api/telemetry/batch", ApiTestHost.Json("""{ "macAddress": "AA:BB:CC:DD:EE:01", "readings": [] }"""));

        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reading.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);
    }

    [Fact]
    public async Task An_operator_can_register_a_sensor_send_a_reading_and_read_it_back_so_the_gate_opens_both_ways()
    {
        using var api = new AuthenticatedFactory();
        var http = api.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await http.PostAsync("/api/sensors", ApiTestHost.Thermometer10To30())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync("/api/telemetry", ApiTestHost.Json("""{ "macAddress": "AA:BB:CC:DD:EE:01", "value": 20 }"""))).StatusCode);

        var viewer = api.AsViewer();
        var readBack = await viewer.GetAsync("/api/sensors/AA:BB:CC:DD:EE:01");

        Assert.Equal(HttpStatusCode.OK, readBack.StatusCode);
    }

    // ---- the default is safe: the rule that makes a new endpoint protected without anyone doing anything -----

    private static async Task<bool> ConsoleAllowsAsync(string method, params string[] roles)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;

        var user = new ClaimsPrincipal(new ClaimsIdentity(roles.Select(role => new Claim("role", role)), "test", "name", "role"));
        var requirement = new ConsoleAccessRequirement();
        var authorization = new AuthorizationHandlerContext([requirement], user, context);

        await new ConsoleAccessHandler().HandleAsync(authorization);

        return authorization.HasSucceeded;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task A_read_is_open_to_viewers_and_operators_and_to_no_one_else(string method)
    {
        Assert.True(await ConsoleAllowsAsync(method, UserRoles.Viewer));
        Assert.True(await ConsoleAllowsAsync(method, UserRoles.Operator));
        Assert.False(await ConsoleAllowsAsync(method, UserRoles.Device));
        Assert.False(await ConsoleAllowsAsync(method));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("SOMETHING-NEW")]
    public async Task Anything_that_is_not_a_read_is_open_to_operators_only(string method)
    {
        Assert.True(await ConsoleAllowsAsync(method, UserRoles.Operator));
        Assert.False(await ConsoleAllowsAsync(method, UserRoles.Viewer));
        Assert.False(await ConsoleAllowsAsync(method, UserRoles.Device));
        Assert.False(await ConsoleAllowsAsync(method));
    }
}
