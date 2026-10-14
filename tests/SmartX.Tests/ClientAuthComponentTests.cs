using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Client.Auth;
using SmartX.Client.Components.CommandStream;
using SmartX.Client.Layout;
using SmartX.Client.Pages;
using SmartX.Client.Services;
using SmartX.Shared;
using static SmartX.Tests.ClientRealtimeKit;

namespace SmartX.Tests;

/// <summary>Answers each request with what a test says, and can hold one open to look at the page while it waits.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);

        return respond(request);
    }

    public static HttpResponseMessage Problem(HttpStatusCode status, string detail) =>
        new(status) { Content = JsonContent.Create(new { title = "x", detail, status = (int)status }) };
}

/// <summary>The login page.</summary>
public class LoginPageTests : StreamComponentTests
{
    private const string Typed = "Typed-Password-Probe-91";

    private ScriptedHandler Handler { get; set; } = null!;

    private IRenderedComponent<Login> Open(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, string? returnUrl = null, string? reason = null, bool signedIn = false)
    {
        Handler = new ScriptedHandler(respond);
        TestServices.AddSingleton(new SmartXApiClient(new HttpClient(Handler) { BaseAddress = new Uri("http://stub/") }));

        if (!signedIn)
        {
            SignOut();
        }

        // The page reads these from the address, as it does in the browser, so the test navigates there and does not pass them in.
        var query = new List<string>();

        if (returnUrl is not null)
        {
            query.Add("returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        if (reason is not null)
        {
            query.Add("reason=" + reason);
        }

        Navigation.NavigateTo("/login" + (query.Count == 0 ? "" : "?" + string.Join("&", query)));

        return Render<Login>();
    }

    private static Task<HttpResponseMessage> Accepts(string role = UserRoles.Operator) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new LoginResponse("issued-token", SessionKit.Start.AddHours(1), "operator", role)),
        });

    private static void FillIn(IRenderedComponent<Login> page, string username, string password)
    {
        var fields = page.FindAll("input");

        fields[0].Input(username);
        fields[1].Input(password);
    }

    [Fact]
    public async Task The_page_has_a_heading_two_labelled_fields_and_a_submit_button()
    {
        var page = Open(_ => Accepts());
        await Task.Yield();

        Assert.Equal("Sign in", page.Find("h1").TextContent.Trim());
        Assert.Contains("Username", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Password", page.Markup, StringComparison.Ordinal);
        Assert.Equal("password", page.FindAll("input")[1].GetAttribute("type"));
        Assert.Equal("submit", page.Find("button").GetAttribute("type"));
    }

    [Fact]
    public void Nothing_can_be_submitted_until_both_fields_have_something_in_them()
    {
        var page = Open(_ => Accepts());

        Assert.True(page.Find("button").HasAttribute("disabled"));

        FillIn(page, "operator", "");
        Assert.True(page.Find("button").HasAttribute("disabled"));

        FillIn(page, "operator", "pw");
        Assert.False(page.Find("button").HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_good_login_signs_in_stores_the_session_and_lands_where_the_user_was_going()
    {
        var page = Open(_ => Accepts(), returnUrl: "/command-stream");
        FillIn(page, "operator", Typed);

        await page.Find("form").SubmitAsync();

        Assert.True(Session.IsAuthenticated);
        Assert.Equal("issued-token", Session.Token);
        Assert.Contains("issued-token", Assert.Single(Storage.Values).Value, StringComparison.Ordinal);
        Assert.EndsWith("/command-stream", Navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_return_address_a_good_login_goes_to_the_front_page()
    {
        var page = Open(_ => Accepts());
        FillIn(page, "operator", Typed);

        await page.Find("form").SubmitAsync();

        Assert.Equal("/", new Uri(Navigation.Uri).AbsolutePath);
    }

    [Fact]
    public async Task A_return_address_that_leaves_the_site_is_ignored()
    {
        var page = Open(_ => Accepts(), returnUrl: "https://elsewhere.example/phish");
        FillIn(page, "operator", Typed);

        await page.Find("form").SubmitAsync();

        Assert.Equal("localhost", new Uri(Navigation.Uri).Host);
        Assert.Equal("/", new Uri(Navigation.Uri).AbsolutePath);
    }

    [Fact]
    public async Task A_wrong_password_says_so_in_the_servers_words_stays_signed_out_and_empties_the_password_field()
    {
        var page = Open(_ => Task.FromResult(ScriptedHandler.Problem(HttpStatusCode.Unauthorized, "Incorrect username or password.")));
        FillIn(page, "operator", Typed);

        await page.Find("form").SubmitAsync();

        var alert = page.Find("[role=alert]");

        Assert.Contains("Incorrect username or password.", alert.TextContent, StringComparison.Ordinal);
        Assert.False(Session.IsAuthenticated);
        Assert.Empty(Storage.Values);
        Assert.Equal("", page.FindAll("input")[1].GetAttribute("value") ?? "");
        Assert.DoesNotContain(Typed, page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_locked_account_says_how_long_to_wait()
    {
        var page = Open(_ => Task.FromResult(ScriptedHandler.Problem(HttpStatusCode.TooManyRequests, "Too many failed attempts. Try again in 42 seconds.")));
        FillIn(page, "operator", Typed);

        await page.Find("form").SubmitAsync();

        Assert.Contains("42 seconds", page.Find("[role=alert]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_that_cannot_be_reached_is_said_to_be_unreachable_and_not_a_wrong_password()
    {
        var page = Open(_ => throw new HttpRequestException("refused"));
        FillIn(page, "operator", Typed);

        await page.Find("form").SubmitAsync();

        Assert.Contains("could not be reached", page.Find("[role=alert]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_the_request_is_in_flight_the_button_says_so_and_cannot_be_pressed_again()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var page = Open(_ => gate.Task);
        FillIn(page, "operator", Typed);

        var first = page.Find("form").SubmitAsync();
        var second = page.Find("form").SubmitAsync();

        Assert.Contains("Signing in", page.Find("button").TextContent, StringComparison.Ordinal);
        Assert.True(page.Find("button").HasAttribute("disabled"));
        Assert.All(page.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));

        gate.SetResult((await Accepts()));
        await first;
        await second;

        Assert.Equal(1, Handler.Requests);
        Assert.True(Session.IsAuthenticated);
    }

    [Theory]
    [InlineData("expired", "session expired")]
    [InlineData("rejected", "no longer accepts")]
    public void When_the_user_was_sent_here_the_page_says_why(string reason, string expected)
    {
        var page = Open(_ => Accepts(), reason: reason);

        Assert.Contains(expected, page.Find("[role=status]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Someone_who_is_already_signed_in_is_sent_on_and_not_shown_a_login_form()
    {
        _ = Open(_ => Accepts(), returnUrl: "/sensors", signedIn: true);

        Assert.EndsWith("/sensors", Navigation.Uri, StringComparison.Ordinal);
    }
}

/// <summary>What a Viewer sees: the controls, disabled, with the reason beside them, and the server still the one that decides.</summary>
public class ViewerExperienceTests : StreamComponentTests
{
    private static readonly Guid Verdictable = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static bool IsDisabled(AngleSharp.Dom.IElement button) => button.HasAttribute("disabled");

    private static IEnumerable<AngleSharp.Dom.IElement> Buttons(IRenderedComponent<CommandControls> cut) => cut.FindAll(".smartx-commands button");

    [Fact]
    public void A_viewer_sees_every_command_button_disabled_and_the_reason()
    {
        SignInAs(UserRoles.Viewer);

        var cut = Render<CommandControls>(parameters => parameters.Add(controls => controls.Sensor, StreamKit.Actuator(StreamKit.Valve, "Dock Valve", ActuatorKind.Valve)));

        Assert.NotEmpty(Buttons(cut));
        Assert.All(Buttons(cut), button => Assert.True(IsDisabled(button)));
        Assert.Contains("Operators only", cut.Find("[role=note]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_sees_them_enabled_and_no_note()
    {
        var cut = Render<CommandControls>(parameters => parameters.Add(controls => controls.Sensor, StreamKit.Actuator(StreamKit.Valve, "Dock Valve", ActuatorKind.Valve)));

        Assert.All(Buttons(cut), button => Assert.False(IsDisabled(button)));
        Assert.Empty(cut.FindAll("[role=note]"));
    }

    [Fact]
    public void A_dimmers_level_control_is_disabled_for_a_viewer_too()
    {
        SignInAs(UserRoles.Viewer);

        var cut = Render<CommandControls>(parameters => parameters.Add(controls => controls.Sensor, StreamKit.Actuator(StreamKit.Valve, "Dock Dimmer", ActuatorKind.Dimmer)));

        Assert.All(Buttons(cut), button => Assert.True(IsDisabled(button)));
    }

    [Fact]
    public void Undo_is_disabled_for_a_viewer_with_the_reason_beside_it()
    {
        SignInAs(UserRoles.Viewer);

        var cut = Render<UndoPanel>();

        Assert.True(IsDisabled(cut.Find("button")));
        Assert.Contains("Operators only", cut.Find("[role=note]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_an_action_to_a_zone_is_disabled_for_a_viewer()
    {
        SignInAs(UserRoles.Viewer);

        var cut = Render<ZoneActionPanel>();

        Assert.Contains("Operators only", cut.Find("[role=note]").TextContent, StringComparison.Ordinal);
        Assert.True(IsDisabled(cut.FindAll("button").Single(button => button.TextContent.Contains("Apply to zone", StringComparison.Ordinal))));
    }

    [Fact]
    public void The_simulate_buttons_are_disabled_for_a_viewer()
    {
        SignInAs(UserRoles.Viewer);

        var cut = Render<PipelineStrip>();

        Assert.All(
            cut.FindAll("button").Where(button => button.TextContent.Contains("Simulate", StringComparison.Ordinal) || button.TextContent.Contains("Back to normal", StringComparison.Ordinal)),
            button => Assert.True(IsDisabled(button)));
        Assert.Contains("Operators only", cut.Find("[role=note]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suggested_command_and_not_useful_are_disabled_for_a_viewer_but_opening_a_device_is_not()
    {
        SignInAs(UserRoles.Viewer);
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(1, [
            SuggestionKit.Make("a", "Turn on Pump A"),
            SuggestionKit.Make("b", "Open Pump A", command: false)]);
        await PreloadFeedsAsync();

        var cut = Render<SuggestedActionsPanel>();
        await SettledAsync(cut);

        var turnOn = cut.FindAll("button").Single(button => button.TextContent.Contains("Turn on Pump A", StringComparison.Ordinal) && !button.TextContent.Contains("Not useful", StringComparison.Ordinal));
        var open = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Open Pump A");

        Assert.True(IsDisabled(turnOn));
        Assert.False(IsDisabled(open));
        Assert.All(cut.FindAll("button").Where(button => button.TextContent.Contains("Not useful", StringComparison.Ordinal)), button => Assert.True(IsDisabled(button)));
        Assert.Contains("Operators only", cut.Find("[role=note]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operator_has_every_suggestion_button_enabled()
    {
        Rig.Snapshots.Suggestions = () => SuggestionKit.Update(1, [SuggestionKit.Make("a", "Turn on Pump A")]);
        await PreloadFeedsAsync();

        var cut = Render<SuggestedActionsPanel>();
        await SettledAsync(cut);

        Assert.All(cut.FindAll(".smartx-suggestion button"), button => Assert.False(IsDisabled(button)));
        Assert.Empty(cut.FindAll("[role=note]"));
    }

    [Fact]
    public async Task A_verdict_cannot_be_given_by_a_viewer()
    {
        SignInAs(UserRoles.Viewer);
        Api.OpenInvestigations = () => [SuggestionKit.Investigation(Verdictable)];
        await PreloadFeedsAsync();

        var cut = Render<InvestigationsPanel>();
        await SettledAsync(cut);

        var verdicts = cut.FindAll("button").Where(button => button.TextContent.Trim() is "Confirm" or "Dismiss" or "Relabel").ToList();

        Assert.Equal(3, verdicts.Count);
        Assert.All(verdicts, button => Assert.True(IsDisabled(button)));
        Assert.Contains("Operators only", cut.Find("[role=note]").TextContent, StringComparison.Ordinal);

        // Looking is still allowed: the decay window is a read.
        Assert.False(IsDisabled(cut.FindAll("button").Single(button => button.TextContent.Contains("Show decay window", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task A_viewer_opening_a_device_sends_no_report_to_the_gateway_because_the_gateway_would_refuse_it()
    {
        var calls = 0;
        var handler = new ScriptedHandler(_ =>
        {
            calls++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var api = new SmartXApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://stub/") });
        var connection = new ApiConnection(api);
        var viewer = SessionKit.New();
        viewer.Session.Adopt(SessionKit.Login(UserRoles.Viewer));
        var oper = SessionKit.New();
        oper.Session.Adopt(SessionKit.Login(UserRoles.Operator));

        Assert.Null(await new OperatorApi(api, connection, viewer.Session).RecordSelectionAsync("B8:27:EB:10:00:34"));
        Assert.Equal(0, calls);

        await new OperatorApi(api, connection, oper.Session).RecordSelectionAsync("B8:27:EB:10:00:34");

        Assert.Equal(1, calls);
    }
}

/// <summary>The app bar: who is signed in, and signing out.</summary>
public class LayoutSessionTests : StreamComponentTests
{
    private IRenderedComponent<MainLayout> RenderLayout()
    {
        // The layout asks the API whether it is up. This one says it is.
        var health = new ScriptedHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new HealthResponse("ok")) }));

        TestServices.AddSingleton(new ApiConnection(new SmartXApiClient(new HttpClient(health) { BaseAddress = new Uri("http://stub/") })));
        TestServices.AddSingleton(Rig.Connection);

        return Render<MainLayout>(parameters => parameters.Add(layout => layout.Body, (RenderFragment)(builder => builder.AddContent(0, "page body"))));
    }

    [Fact]
    public void The_app_bar_names_the_user_and_the_role_and_offers_log_out()
    {
        SignInAs(UserRoles.Operator, "ada");

        var cut = RenderLayout();
        var who = cut.Find(".smartx-appbar-user");

        Assert.Contains("ada", who.TextContent, StringComparison.Ordinal);
        Assert.Contains("Operator", who.TextContent, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Log out", StringComparison.Ordinal));
    }

    [Fact]
    public void A_viewer_is_shown_as_a_viewer()
    {
        SignInAs(UserRoles.Viewer, "vic");

        var who = RenderLayout().Find(".smartx-appbar-user");

        Assert.Contains("Viewer", who.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Before_anyone_signs_in_there_is_no_user_no_log_out_no_menu_and_no_navigation()
    {
        SignOut();

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll(".smartx-appbar-user"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Log out", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[aria-label='Toggle navigation']"));
        Assert.Empty(cut.FindAll(".mud-drawer"));
        Assert.Contains("page body", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_out_clears_the_session_and_the_stored_copy_and_reloads_the_login_page()
    {
        SignInAs(UserRoles.Operator);
        await Storage.SetAsync("smartx.session", "{}");
        var cut = RenderLayout();

        await cut.FindAll("button").Single(button => button.TextContent.Contains("Log out", StringComparison.Ordinal)).ClickAsync();

        Assert.False(Session.IsAuthenticated);
        Assert.Empty(Storage.Values);
        Assert.EndsWith("/login", Navigation.Uri, StringComparison.Ordinal);
        Assert.True(((Bunit.TestDoubles.BunitNavigationManager)Navigation).History.First().Options.ForceLoad);
    }

    [Fact]
    public async Task The_real_time_connection_is_not_opened_until_someone_is_signed_in_and_is_when_they_are()
    {
        SignOut();
        var cut = RenderLayout();
        await Task.Yield();

        var startsBefore = Rig.Hub.StartCalls;

        await Session.SignInAsync(SessionKit.Login());
        cut.WaitForState(() => Rig.Hub.StartCalls > startsBefore, TimeSpan.FromSeconds(2));

        Assert.Equal(0, startsBefore);
        Assert.True(Rig.Hub.StartCalls >= 1);
    }
}
