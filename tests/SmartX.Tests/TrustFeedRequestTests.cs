using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using SmartX.Client.Services;
using SmartX.Client.State;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers the cost of one trust panel refresh, measured against the real API.
/// </summary>
/// <remarks>
/// The client's feed talks to an in-process API through a handler that counts every request it
/// sends. Counting at the HTTP boundary, rather than asserting on which methods were called, is what
/// makes this a test of network cost: an extra request added anywhere on the refresh path fails it.
/// </remarks>
public class TrustFeedRequestTests
{
    /// <summary>Counts requests on their way out.</summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        private int _count;

        public int Count => _count;

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);

            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(40)]
    public async Task A_refresh_is_two_requests_however_many_nodes_are_tracked(int nodeCount)
    {
        using var api = New();
        var setup = api.CreateClient();

        for (var node = 0; node < nodeCount; node++)
        {
            var mac = $"AA:BB:CC:00:10:{node:X2}";
            var response = await setup.PostAsync("/api/sensors", Thermometer10To30(mac));
            response.EnsureSuccessStatusCode();
        }

        var counter = new CountingHandler();
        var client = new SmartXApiClient(api.CreateDefaultClient(counter));
        var feed = new PollingTrustFeed(
            client,
            new ApiConnection(client),
            new AppState(),
            TimeProvider.System,
            new ConfigurationBuilder().Build());

        await feed.RefreshNowAsync();

        Assert.Equal(2, counter.Count);
        Assert.Equal(["/api/trust", "/api/trust/summary"], counter.Paths.Order());

        // Two requests are only a fix if the sparklines still have their data.
        Assert.Equal(nodeCount, feed.Current.Nodes.Count);
        Assert.All(feed.Current.Nodes, node => Assert.NotEmpty(node.RecentHistory));
    }

    [Fact]
    public async Task The_trust_list_carries_each_nodes_recent_history_oldest_first()
    {
        using var api = New();
        var client = api.CreateClient();
        await client.PostAsync("/api/sensors", Thermometer10To30());
        await client.PostAsync("/api/telemetry", Reading(Thermometer, "40.0"));

        var nodes = await client.GetFromJsonAsync<List<NodeTrustSnapshot>>("/api/trust");

        var history = Assert.Single(nodes!).RecentHistory;
        Assert.Equal([TrustBand.Unproven, TrustBand.Fault], history.Select(point => point.Band));
        Assert.True(history[0].Timestamp <= history[1].Timestamp);
    }
}
