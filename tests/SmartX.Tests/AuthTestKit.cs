using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using SmartX.Api.Security;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Tokens and device keys for tests, minted the way the API mints them and signed with the development key every test host runs on.
/// </summary>
/// <remarks>
/// A test host is the Development environment, so it starts with the published development signing key and fleet secret; this uses
/// the same two, and so a token made here is one the host under test accepts. Nothing in a test is weakened by this: the API still
/// checks every signature, issuer, audience and expiry. The tests that are about authentication make tokens that are wrong on purpose.
/// </remarks>
internal static class TestAuth
{
    public const string OperatorName = "test-operator";
    public const string ViewerName = "test-viewer";

    public static string FleetSecret => AuthDefaults.DevelopmentFleetSecret;

    /// <summary>A token for a user with this role, from a token service set up like the host's.</summary>
    public static string Token(string role = UserRoles.Operator, string? username = null, TimeSpan? lifetime = null, TimeProvider? time = null)
    {
        var options = new AuthOptions
        {
            SigningKey = AuthDefaults.DevelopmentSigningKey,
            FleetSecret = AuthDefaults.DevelopmentFleetSecret,
            TokenLifetime = lifetime ?? TimeSpan.FromHours(1),
        };
        var tokens = new TokenService(Options.Create(options), time ?? TimeProvider.System);

        return tokens.Issue(new UserAccount(username ?? (role == UserRoles.Viewer ? ViewerName : OperatorName), "", role)).Token;
    }

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    public static AuthenticationHeaderValue OperatorHeader() => Bearer(Token(UserRoles.Operator));

    public static AuthenticationHeaderValue ViewerHeader() => Bearer(Token(UserRoles.Viewer));

    /// <summary>A client with no credentials at all, from a factory whose default clients carry an Operator's.</summary>
    public static HttpClient Anonymous(this WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;

        return client;
    }

    /// <summary>A client carrying a Viewer's token.</summary>
    public static HttpClient AsViewer(this WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = ViewerHeader();

        return client;
    }

    /// <summary>A client that authenticates as one device, with the key derived for its MAC. No token.</summary>
    public static HttpClient AsDevice(this WebApplicationFactory<Program> api, string mac, string? key = null)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add(DeviceAuthHeaders.Mac, mac);
        client.DefaultRequestHeaders.Add(DeviceAuthHeaders.Key, key ?? DeviceKeys.Derive(FleetSecret, mac));

        return client;
    }
}

/// <summary>
/// The API's test host, with clients that are an Operator unless a test says otherwise.
/// </summary>
/// <remarks>
/// Most tests are about what the API does with what it is sent, and need to get past authentication to find out. A fixture that
/// logs in as an Operator for them keeps every assertion exactly as it was, instead of editing a hundred tests to carry a header.
/// The tests that are about authentication ask for <see cref="TestAuth.Anonymous"/>, <see cref="TestAuth.AsViewer"/> or
/// <see cref="TestAuth.AsDevice"/> instead.
/// </remarks>
internal class AuthenticatedFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        client.DefaultRequestHeaders.Authorization = TestAuth.OperatorHeader();
    }
}

/// <summary>
/// Calls to the device-only endpoints (collect commands, acknowledge them), made the way a device makes them: with its MAC and its key.
/// </summary>
/// <remarks>
/// An Operator's token is not accepted there, on purpose, so a test that plays a device has to be one. The key is derived for the MAC
/// the call is for, which is also what makes "a key for one device used for another" something a test can arrange.
/// </remarks>
internal static class DeviceCalls
{
    public static HttpRequestMessage Signed(HttpRequestMessage request, string mac, string? key = null)
    {
        request.Headers.Add(DeviceAuthHeaders.Mac, mac);
        request.Headers.Add(DeviceAuthHeaders.Key, key ?? DeviceKeys.Derive(TestAuth.FleetSecret, mac));

        return request;
    }

    public static Task<HttpResponseMessage> DeviceGetAsync(this HttpClient http, string url, string mac) =>
        http.SendAsync(Signed(new HttpRequestMessage(HttpMethod.Get, url), mac));

    public static async Task<T?> DeviceGetFromJsonAsync<T>(this HttpClient http, string url, string mac)
    {
        using var response = await http.DeviceGetAsync(url, mac);
        response.EnsureSuccessStatusCode();

        return await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(response.Content);
    }

    public static Task<HttpResponseMessage> DevicePostAsync(this HttpClient http, string url, string mac, HttpContent content) =>
        http.SendAsync(Signed(new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, mac));

    public static Task<HttpResponseMessage> DevicePostAsJsonAsync<T>(this HttpClient http, string url, string mac, T body) =>
        http.DevicePostAsync(url, mac, System.Net.Http.Json.JsonContent.Create(body));
}
