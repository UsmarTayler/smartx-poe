using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SmartX.Tests;

/// <summary>
/// Helpers for the endpoint tests.
/// </summary>
/// <remarks>
/// Each test gets its own host rather than sharing one through a class fixture. The stores are
/// registered as singletons, so a shared host would carry sensors registered by one test into the
/// next — and a duplicate-MAC test would then pass or fail depending on the order xUnit happened to
/// run things in. A fresh host per test costs a fraction of a second and removes that entirely.
/// <para>
/// Request bodies are written as literal JSON rather than serialised from a request object. These
/// tests are about what the API does with what arrives on the wire — particularly which packet type
/// a bare JSON number becomes — so the wire format is the thing worth stating explicitly.
/// </para>
/// </remarks>
internal static class ApiTestHost
{
    internal const string Thermometer = "AA:BB:CC:DD:EE:01";
    internal const string Meter = "AA:BB:CC:DD:EE:02";
    internal const string Switch = "AA:BB:CC:DD:EE:03";

    /// <summary>A host with empty stores and the seeded zone hierarchy.</summary>
    internal static WebApplicationFactory<Program> New() => new();


    /// <summary>
    /// Waits until the ingestion worker has finished processing every reading accepted so far.
    /// </summary>
    /// <remarks>
    /// The API validates a reading while the caller waits and stores and scores it just afterwards, so a test
    /// that posts a reading and immediately reads history would race the worker. Waiting on the pipeline's own
    /// stats — depth zero, and processed equal to accepted — is deterministic where a fixed sleep is not: it
    /// returns the moment the work is done, and fails loudly with the stats if it never is.
    /// </remarks>
    internal static async Task DrainAsync(HttpClient client, int expectedProcessed, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        SmartX.Shared.PipelineStats? stats = null;

        while (DateTime.UtcNow < deadline)
        {
            stats = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<SmartX.Shared.PipelineStats>(
                client, "/api/pipeline/stats");

            if (stats!.TotalDepth == 0 && stats.TotalProcessed >= expectedProcessed)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"The worker did not process {expectedProcessed} readings in time. " +
            $"Depth {stats?.TotalDepth}, processed {stats?.TotalProcessed}.");
    }

    /// <summary>Wraps a JSON literal as a request body.</summary>
    internal static StringContent Json(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return content;
    }

    /// <summary>A registration body for a thermometer reporting 10–30 °C in Sub-Zone B.</summary>
    internal static StringContent Thermometer10To30(string mac = Thermometer, string zoneId = "sub-zone-b") =>
        Registration(mac, zoneId, "Environmental", "°C", 10, 30, "Real");

    /// <summary>A registration body with every field spelled out.</summary>
    internal static StringContent Registration(
        string mac,
        string zoneId,
        string category,
        string unit,
        double minimum,
        double maximum,
        string payloadKind,
        string room = "Server Room 2",
        string nodeId = "NODE-1") =>
        Json($$"""
        {
          "macAddress": "{{mac}}",
          "room": "{{room}}",
          "zoneId": "{{zoneId}}",
          "nodeId": "{{nodeId}}",
          "category": "{{category}}",
          "unit": "{{unit}}",
          "minimum": {{minimum}},
          "maximum": {{maximum}},
          "payloadKind": "{{payloadKind}}"
        }
        """);

    /// <summary>A telemetry body carrying a raw JSON value.</summary>
    internal static StringContent Reading(string mac, string rawJsonValue) =>
        Json($$"""
        { "macAddress": "{{mac}}", "value": {{rawJsonValue}} }
        """);
}
