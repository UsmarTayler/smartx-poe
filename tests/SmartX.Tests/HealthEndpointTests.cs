using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Covers the one endpoint that currently exists.
/// </summary>
/// <remarks>
/// WebApplicationFactory boots the real API in-process, so this exercises the actual routing and
/// JSON serialisation rather than calling the handler directly. That matters here because the
/// thing worth guarding is the contract the client depends on, not the lambda body — a rename of
/// the route or of a property on <see cref="HealthResponse"/> should fail this test.
/// </remarks>
public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_endpoint_reports_ok()
    {
        var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<HealthResponse>("/api/health");

        Assert.Equal(new HealthResponse("ok"), response);
    }
}
