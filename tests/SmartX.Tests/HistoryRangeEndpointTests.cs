using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Api.History;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// <c>GET /api/history/{mac}/range</c> and <c>GET /api/history/stats</c>: the AVL tree's range query over HTTP.
/// </summary>
/// <remarks>
/// Readings go straight into the history store from the test, which only the test project may do. The endpoints are about
/// what is read back, and a fixed timestamp makes each window exact; none of this reads the clock.
/// </remarks>
public class HistoryRangeEndpointTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Time(DateTimeOffset at) => Uri.EscapeDataString(at.ToString("O"));

    private static string Window(DateTimeOffset from, DateTimeOffset to) => $"from={Time(from)}&to={Time(to)}";

    private static HistoryEntry Reading(string mac, long sequence, DateTimeOffset at, string unit = "°C") => new()
    {
        Sequence = sequence,
        Timestamp = at,
        MacAddress = mac,
        Kind = HistoryKind.Reading,
        Value = sequence,
        Unit = unit,
    };

    /// <summary>A host with one registered thermometer holding one reading a minute for two hours.</summary>
    private static async Task<WebApplicationFactory<Program>> HostWithHistoryAsync(string? store = null)
    {
        var api = ApiTestHost.New().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["History:Store"] = store })));

        var registered = await api.CreateClient().PostAsync("/api/sensors", Thermometer10To30());
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var history = api.Services.GetRequiredService<IHistoryStore>();

        for (var minute = 0; minute < 120; minute++)
        {
            history.Append(Reading(Thermometer, minute + 1, T0.AddMinutes(minute)));
        }

        return api;
    }

    [Fact]
    public async Task A_viewer_gets_the_readings_in_a_window_oldest_first_and_half_open()
    {
        using var api = await HostWithHistoryAsync();
        var http = api.AsViewer();

        // 12:30 up to but not including 12:35: the readings at 12:30, :31, :32, :33 and :34.
        var range = await http.GetFromJsonAsync<HistoryRange>(
            $"/api/history/{Thermometer}/range?{Window(T0.AddMinutes(30), T0.AddMinutes(35))}");

        Assert.NotNull(range);
        Assert.Equal([31L, 32, 33, 34, 35], range.Readings.Select(reading => reading.Sequence));
        Assert.Equal(T0.AddMinutes(30), range.Readings[0].Timestamp);
        Assert.False(range.Truncated);
        Assert.Null(range.NextFrom);
        Assert.Equal("°C", range.Readings[0].Unit);
    }

    [Fact]
    public async Task The_response_says_how_long_the_query_took_in_microseconds()
    {
        using var api = await HostWithHistoryAsync();

        // Read as raw JSON, so it is the name on the wire that is checked and not a property of a type the test shares with the API.
        var json = await api.AsViewer().GetStringAsync($"/api/history/{Thermometer}/range?{Window(T0, T0.AddHours(1))}");
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("queryMicroseconds", out var microseconds), json);
        Assert.True(microseconds.GetDouble() >= 0);
    }

    [Fact]
    public async Task A_window_with_no_readings_is_an_empty_answer_and_not_an_error()
    {
        using var api = await HostWithHistoryAsync();

        var range = await api.AsViewer().GetFromJsonAsync<HistoryRange>(
            $"/api/history/{Thermometer}/range?{Window(T0.AddDays(1), T0.AddDays(2))}");

        Assert.Empty(range!.Readings);
        Assert.False(range.Truncated);
    }

    [Fact]
    public async Task A_request_with_no_credentials_is_refused()
    {
        using var api = await HostWithHistoryAsync();
        var http = api.Anonymous();

        var range = await http.GetAsync($"/api/history/{Thermometer}/range?{Window(T0, T0.AddHours(1))}");
        var stats = await http.GetAsync("/api/history/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, range.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stats.StatusCode);
    }

    [Fact]
    public async Task A_bad_window_or_limit_is_a_400()
    {
        // One host for all seven. Each Theory row used to start its own, about a second each.
        using var api = await HostWithHistoryAsync();
        var http = api.AsViewer();

        string[] queries =
        [
            "from=2026-03-01T13:00:00Z&to=2026-03-01T12:00:00Z",                   // backwards
            "from=2026-03-01T12:00:00Z&to=2026-03-01T12:00:00Z",                   // empty
            "from=2026-03-01T12:00:00Z",                                           // no end
            "to=2026-03-01T12:00:00Z",                                             // no start
            "from=yesterday&to=today",                                             // not instants
            "from=2026-03-01T12:00:00Z&to=2026-03-01T13:00:00Z&limit=0",
            "from=2026-03-01T12:00:00Z&to=2026-03-01T13:00:00Z&limit=999999",
        ];

        foreach (var query in queries)
        {
            var status = (await http.GetAsync($"/api/history/{Thermometer}/range?{query}")).StatusCode;

            Assert.True(status == HttpStatusCode.BadRequest, $"?{query}: expected 400, got {(int)status}.");
        }
    }

    [Fact]
    public async Task A_sensor_that_is_not_registered_is_a_404()
    {
        using var api = await HostWithHistoryAsync();

        var response = await api.AsViewer().GetAsync($"/api/history/AA:BB:CC:00:00:99/range?{Window(T0, T0.AddHours(1))}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_long_window_comes_back_a_page_at_a_time_and_the_pages_join_up()
    {
        using var api = await HostWithHistoryAsync();
        var http = api.AsViewer();
        var from = T0;
        var sequences = new List<long>();

        for (var pages = 0; pages < 10; pages++)
        {
            var range = (await http.GetFromJsonAsync<HistoryRange>(
                $"/api/history/{Thermometer}/range?{Window(from, T0.AddHours(2))}&limit=50"))!;

            sequences.AddRange(range.Readings.Select(reading => reading.Sequence));

            if (!range.Truncated)
            {
                break;
            }

            Assert.Equal(50, range.Readings.Count);
            from = range.NextFrom!.Value;
        }

        // Three pages (50, 50, 20), every reading exactly once, in order.
        Assert.Equal(Enumerable.Range(1, 120).Select(number => (long)number), sequences);
    }

    [Fact]
    public async Task The_metric_keeps_only_readings_in_that_unit()
    {
        using var api = await HostWithHistoryAsync();
        var history = api.Services.GetRequiredService<IHistoryStore>();
        history.Append(Reading(Thermometer, 1000, T0.AddMinutes(10).AddSeconds(30), unit: "%RH"));
        var http = api.AsViewer();

        var all = await http.GetFromJsonAsync<HistoryRange>($"/api/history/{Thermometer}/range?{Window(T0, T0.AddMinutes(20))}");
        var humidity = await http.GetFromJsonAsync<HistoryRange>(
            $"/api/history/{Thermometer}/range?{Window(T0, T0.AddMinutes(20))}&metric={Uri.EscapeDataString("%RH")}");

        Assert.Equal(21, all!.Readings.Count);
        Assert.Equal([1000L], humidity!.Readings.Select(reading => reading.Sequence));
        Assert.Equal("%RH", humidity.Metric);
    }

    [Fact]
    public async Task Events_in_the_same_tree_are_not_returned_as_readings()
    {
        using var api = await HostWithHistoryAsync();
        var history = api.Services.GetRequiredService<IHistoryStore>();
        history.Append(HistoryEntry.FromEvent(5000, T0.AddMinutes(1).AddSeconds(5), Thermometer, HistoryKind.AlertRaised, new HistoryEventDetails("raised")));

        var range = await api.AsViewer().GetFromJsonAsync<HistoryRange>($"/api/history/{Thermometer}/range?{Window(T0, T0.AddMinutes(3))}");

        Assert.Equal([1L, 2, 3], range!.Readings.Select(reading => reading.Sequence));
    }

    [Fact]
    public async Task Stats_show_the_tree_height_next_to_the_best_height_possible()
    {
        using var api = await HostWithHistoryAsync();

        var stats = await api.AsViewer().GetFromJsonAsync<HistoryStoreStats>("/api/history/stats");

        Assert.Equal("Avl", stats!.Store);
        // The 120 readings, plus whatever the registration itself recorded (a trust sample for the new node).
        Assert.True(stats.Entries >= 120, $"The store reports {stats.Entries} entries.");

        var tree = stats.Largest.Single(tree => tree.Scope == Thermometer);

        Assert.True(tree.Entries >= 120);
        Assert.Equal(AvlTree<int, int>.MinimumHeightFor(tree.Entries), tree.MinimumHeight);
        Assert.Equal(7, tree.MinimumHeight);   // ceil(log2(121)) to ceil(log2(122))
        Assert.InRange(tree.Height!.Value, 7, 8);
    }

    [Fact]
    public async Task The_sorted_dictionary_store_can_be_selected_by_configuration_and_answers_the_same_query()
    {
        using var api = await HostWithHistoryAsync(store: "SortedDictionary");
        var http = api.AsViewer();

        var stats = await http.GetFromJsonAsync<HistoryStoreStats>("/api/history/stats");
        var range = await http.GetFromJsonAsync<HistoryRange>(
            $"/api/history/{Thermometer}/range?{Window(T0.AddMinutes(30), T0.AddMinutes(35))}");

        Assert.Equal("SortedDictionary", stats!.Store);
        Assert.Null(stats.Largest.Single(tree => tree.Scope == Thermometer).Height);
        Assert.Equal([31L, 32, 33, 34, 35], range!.Readings.Select(reading => reading.Sequence));
    }

    [Fact]
    public async Task The_default_store_is_the_avl_tree()
    {
        using var api = await HostWithHistoryAsync();

        Assert.IsType<AvlHistoryStore>(api.Services.GetRequiredService<IHistoryStore>());
    }

    [Fact]
    public async Task A_largest_outside_zero_to_one_hundred_is_a_400()
    {
        using var api = await HostWithHistoryAsync();

        var response = await api.AsViewer().GetAsync("/api/history/stats?largest=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
