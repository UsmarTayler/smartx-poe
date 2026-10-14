using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using SmartX.Client.Auth;
using SmartX.Client.Components.CommandStream;
using SmartX.Client.Components.Common;
using SmartX.Client.Layout;
using SmartX.Client.Pages;
using SmartX.Client.Services;
using SmartX.Shared;

namespace SmartX.Tests;

internal static class SessionKit
{
    public static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    public static LoginResponse Login(string role = UserRoles.Operator, TimeSpan? lifetime = null, string token = "token-1", string name = "casey") =>
        new(token, Start + (lifetime ?? TimeSpan.FromHours(1)), name, role);

    public static (AuthSession Session, MemorySessionStorage Storage, FakeTimeProvider Clock) New()
    {
        var clock = new FakeTimeProvider(Start);
        var storage = new MemorySessionStorage();

        return (new AuthSession(storage, clock), storage, clock);
    }
}

/// <summary>The signed-in user: kept, restored, and ended on time.</summary>
public class AuthSessionTests
{
    [Fact]
    public async Task Nobody_is_signed_in_to_begin_with_and_nothing_stored_means_still_nobody()
    {
        var (session, _, _) = SessionKit.New();

        await session.RestoreAsync();

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Token);
        Assert.False(session.CanChange);
    }

    [Fact]
    public async Task Signing_in_keeps_the_token_in_memory_and_in_session_storage()
    {
        var (session, storage, _) = SessionKit.New();

        await session.SignInAsync(SessionKit.Login(UserRoles.Operator, token: "the-token"));

        Assert.True(session.IsAuthenticated);
        Assert.Equal("the-token", session.Token);
        Assert.Equal("casey", session.User!.Username);
        Assert.True(session.CanChange);
        Assert.Contains("the-token", Assert.Single(storage.Values).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_refresh_does_not_sign_you_out_because_the_session_is_read_back()
    {
        var (first, storage, clock) = SessionKit.New();
        await first.SignInAsync(SessionKit.Login(UserRoles.Viewer, token: "kept"));

        // A refresh is a new application reading the same tab's storage.
        var afterRefresh = new AuthSession(storage, clock);
        await afterRefresh.RestoreAsync();

        Assert.True(afterRefresh.IsAuthenticated);
        Assert.Equal("kept", afterRefresh.Token);
        Assert.Equal(UserRoles.Viewer, afterRefresh.User!.Role);
        Assert.False(afterRefresh.CanChange);
    }

    [Fact]
    public async Task A_stored_session_that_has_expired_is_not_restored_and_is_cleared()
    {
        var (first, storage, clock) = SessionKit.New();
        await first.SignInAsync(SessionKit.Login(lifetime: TimeSpan.FromMinutes(5)));
        clock.Advance(TimeSpan.FromMinutes(6));

        var later = new AuthSession(storage, clock);
        await later.RestoreAsync();

        Assert.False(later.IsAuthenticated);
        Assert.Empty(storage.Values);
    }

    [Fact]
    public async Task Something_else_stored_under_the_key_is_treated_as_no_session()
    {
        var (session, storage, _) = SessionKit.New();
        await storage.SetAsync("smartx.session", "not json at all");

        await session.RestoreAsync();

        Assert.False(session.IsAuthenticated);
        Assert.Empty(storage.Values);
    }

    [Fact]
    public async Task Signing_out_clears_the_token_and_the_stored_copy_and_says_so()
    {
        var (session, storage, _) = SessionKit.New();
        await session.SignInAsync(SessionKit.Login());
        var changes = 0;
        session.Changed += () => changes++;

        await session.SignOutAsync();

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Token);
        Assert.Empty(storage.Values);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task The_session_ends_by_itself_when_the_token_runs_out_and_says_expired()
    {
        var (session, storage, clock) = SessionKit.New();
        var ended = new List<SessionEnd>();
        session.Ended += ended.Add;
        await session.SignInAsync(SessionKit.Login(lifetime: TimeSpan.FromMinutes(30)));

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(session.IsAuthenticated);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(session.IsAuthenticated);
        Assert.Equal([SessionEnd.Expired], ended);
        Assert.Empty(storage.Values);
    }

    [Fact]
    public async Task A_token_lifetime_longer_than_a_timer_can_wait_still_ends_on_time()
    {
        var (session, _, clock) = SessionKit.New();
        var ended = new List<SessionEnd>();
        session.Ended += ended.Add;

        await session.SignInAsync(SessionKit.Login(lifetime: TimeSpan.FromDays(120)));

        clock.Advance(TimeSpan.FromDays(119));
        Assert.True(session.IsAuthenticated);

        clock.Advance(TimeSpan.FromDays(2));

        Assert.False(session.IsAuthenticated);
        Assert.Equal([SessionEnd.Expired], ended);
    }

    [Fact]
    public async Task Ending_a_session_twice_counts_once_so_a_burst_of_401s_is_one_event()
    {
        var (session, _, _) = SessionKit.New();
        var ended = new List<SessionEnd>();
        session.Ended += ended.Add;
        await session.SignInAsync(SessionKit.Login());

        await Task.WhenAll(session.EndAsync(SessionEnd.Rejected), session.EndAsync(SessionEnd.Rejected), session.EndAsync(SessionEnd.Expired));

        Assert.Equal([SessionEnd.Rejected], ended);
    }

    [Fact]
    public async Task Only_an_operator_may_change_things()
    {
        var (session, _, _) = SessionKit.New();

        await session.SignInAsync(SessionKit.Login(UserRoles.Viewer));
        Assert.False(session.CanChange);

        await session.SignInAsync(SessionKit.Login(UserRoles.Operator));
        Assert.True(session.CanChange);
    }
}

/// <summary>The one place a token is put on a request, and the one place a refused token is noticed.</summary>
public class BearerTokenHandlerTests
{
    private sealed class Recorder(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);

            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return new HttpResponseMessage(status);
        }
    }

    private static HttpClient Client(AuthSession session, Recorder inner) =>
        new(new BearerTokenHandler(session) { InnerHandler = inner }) { BaseAddress = new Uri("http://api/") };

    [Fact]
    public async Task A_signed_in_request_carries_the_bearer_token()
    {
        var (session, _, _) = SessionKit.New();
        await session.SignInAsync(SessionKit.Login(token: "abc.def.ghi"));
        var inner = new Recorder();

        await Client(session, inner).GetAsync("api/trust");

        Assert.Equal("Bearer abc.def.ghi", inner.Seen.Single().Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task A_request_made_while_signed_out_carries_nothing()
    {
        var (session, _, _) = SessionKit.New();
        var inner = new Recorder();

        await Client(session, inner).GetAsync("api/health");

        Assert.Null(inner.Seen.Single().Headers.Authorization);
    }

    [Fact]
    public async Task The_login_request_never_carries_a_token_and_its_401_means_wrong_password_not_a_dead_session()
    {
        var (session, _, _) = SessionKit.New();
        await session.SignInAsync(SessionKit.Login(token: "still-good"));
        var ended = 0;
        session.Ended += _ => ended++;
        var inner = new Recorder(HttpStatusCode.Unauthorized);

        var response = await Client(session, inner).PostAsJsonAsync("api/auth/login", new LoginRequest("a", "b"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(inner.Seen.Single().Headers.Authorization);
        Assert.Equal(0, ended);
        Assert.True(session.IsAuthenticated);
    }

    [Fact]
    public async Task A_401_to_a_request_that_carried_a_token_ends_the_session_as_rejected()
    {
        var (session, storage, _) = SessionKit.New();
        await session.SignInAsync(SessionKit.Login());
        var ended = new List<SessionEnd>();
        session.Ended += ended.Add;

        await Client(session, new Recorder(HttpStatusCode.Unauthorized)).GetAsync("api/alerts");

        Assert.Equal([SessionEnd.Rejected], ended);
        Assert.False(session.IsAuthenticated);
        Assert.Empty(storage.Values);
    }

    [Fact]
    public async Task A_403_does_not_end_the_session_because_the_user_is_known_and_merely_not_allowed()
    {
        var (session, _, _) = SessionKit.New();
        await session.SignInAsync(SessionKit.Login(UserRoles.Viewer));

        var response = await Client(session, new Recorder(HttpStatusCode.Forbidden)).PostAsync("api/commands", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(session.IsAuthenticated);
    }

    [Fact]
    public async Task A_late_401_for_an_old_token_does_not_end_a_session_that_has_since_been_replaced()
    {
        var (session, _, _) = SessionKit.New();
        await session.SignInAsync(SessionKit.Login(token: "old"));
        var inner = new Recorder(HttpStatusCode.Unauthorized) { Gate = new TaskCompletionSource() };
        var pending = Client(session, inner).GetAsync("api/trust");

        await session.SignInAsync(SessionKit.Login(token: "new"));
        inner.Gate.SetResult();
        await pending;

        Assert.True(session.IsAuthenticated);
        Assert.Equal("new", session.Token);
    }
}

/// <summary>Which pages need a user, and where a visitor without one is sent.</summary>
public class RouteProtectionTests : StreamComponentTests
{
    [Theory]
    [InlineData(typeof(TrustPanel))]
    [InlineData(typeof(Sensors))]
    [InlineData(typeof(SensorDetail))]
    [InlineData(typeof(Telemetry))]
    [InlineData(typeof(CommandStream))]
    public void Every_page_of_the_app_requires_a_signed_in_user(Type page)
    {
        Assert.NotEmpty(page.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true));
    }

    [Fact]
    public void The_login_page_and_the_not_found_page_are_open_to_visitors()
    {
        Assert.NotEmpty(typeof(Login).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));
        Assert.Empty(typeof(NotFound).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true));
    }

    [Fact]
    public void Every_routable_page_is_either_protected_or_on_a_short_list_so_a_new_page_cannot_be_forgotten()
    {
        var open = typeof(SmartX.Client.Pages.TrustPanel).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type) && type.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0)
            .Where(type => type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true).Length == 0)
            .Select(type => type.Name)
            .Order()
            .ToList();

        Assert.Equal(["Login", "NotFound"], open);
    }

    /// <summary>The router and authorization of the real app, with the layout and the pages' own services left out.</summary>
    private sealed class RouterHarness : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingAuthenticationState>(0);
            builder.AddAttribute(1, "ChildContent", (RenderFragment)(tree =>
            {
                tree.OpenComponent<Router>(0);
                tree.AddAttribute(1, "AppAssembly", typeof(SmartX.Client.Pages.TrustPanel).Assembly);
                tree.AddAttribute(2, "Found", (RenderFragment<RouteData>)(routeData => found =>
                {
                    found.OpenComponent<AuthorizeRouteView>(0);
                    found.AddAttribute(1, "RouteData", routeData);
                    found.AddAttribute(2, "NotAuthorized", (RenderFragment<AuthenticationState>)(_ => denied =>
                    {
                        denied.OpenComponent<RedirectToLogin>(0);
                        denied.CloseComponent();
                    }));
                    found.CloseComponent();
                }));
                tree.AddAttribute(3, "NotFound", (RenderFragment)(missing => missing.AddContent(0, "not found")));
                tree.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private void UseRealAuthentication()
    {
        // bUnit registers stand-ins for authorization so a test can ignore it. These tests are about the real thing, so they go.
        TestServices.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        TestServices.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        TestServices.RemoveAll<AuthenticationStateProvider>();
        TestServices.AddAuthorizationCore();
        TestServices.AddSingleton<AuthenticationStateProvider>(new SmartXAuthenticationStateProvider(Session));

        // The login page is a real page and the router will reach it, so it gets the services it asks for.
        TestServices.AddSingleton(new SmartXApiClient(new HttpClient(Rig.Http) { BaseAddress = new Uri("http://stub/") }));
    }

    [Fact]
    public void A_visitor_who_is_not_signed_in_is_sent_to_login_and_the_page_they_wanted_is_remembered()
    {
        SignOut();
        UseRealAuthentication();
        Navigation.NavigateTo("/command-stream");

        _ = Render<RouterHarness>();

        var uri = new Uri(Navigation.Uri);

        Assert.Equal("/login", uri.AbsolutePath);
        Assert.Equal("?returnUrl=%2Fcommand-stream", uri.Query);
    }

    [Fact]
    public void A_deep_address_with_colons_in_it_is_remembered_whole()
    {
        SignOut();
        UseRealAuthentication();
        Navigation.NavigateTo("/sensors/B8:27:EB:10:00:34");

        _ = Render<RouterHarness>();

        Assert.Equal("/sensors/B8:27:EB:10:00:34", Uri.UnescapeDataString(System.Web.HttpUtility.ParseQueryString(new Uri(Navigation.Uri).Query)["returnUrl"]!));
    }

    [Fact]
    public void A_visitor_to_the_front_page_goes_to_plain_login()
    {
        SignOut();
        UseRealAuthentication();
        Navigation.NavigateTo("/");

        _ = Render<RouterHarness>();

        Assert.Equal("/login", new Uri(Navigation.Uri).PathAndQuery);
    }

    [Fact]
    public void The_login_page_is_not_redirected_to_itself()
    {
        SignOut();
        UseRealAuthentication();
        Navigation.NavigateTo("/login");

        var harness = Render<RouterHarness>();

        // The router meets the login page with no user, and AllowAnonymous lets it through: it is not sent anywhere.
        Assert.Equal("/login", new Uri(Navigation.Uri).PathAndQuery);
        Assert.DoesNotContain("not found", harness.Markup, StringComparison.Ordinal);
    }
}

/// <summary>The return address must be a place on this site.</summary>
public class ReturnAddressTests
{
    [Theory]
    [InlineData("/sensors", "/sensors")]
    [InlineData("/sensors/B8:27:EB:10:00:34", "/sensors/B8:27:EB:10:00:34")]
    [InlineData("/command-stream?x=1", "/command-stream?x=1")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("sensors", "/")]
    [InlineData("https://elsewhere.example", "/")]
    [InlineData("//elsewhere.example", "/")]
    [InlineData("/\\elsewhere.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/login", "/")]
    [InlineData("/Login?returnUrl=/sensors", "/")]
    public void Only_a_path_on_this_site_is_accepted(string? requested, string expected)
    {
        Assert.Equal(expected, ReturnAddress.Safe(requested));
    }
}
