using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartX.Api.Topology;
using SmartX.Api.Trust;
using SmartX.Shared;
using Xunit.Abstractions;

namespace SmartX.Tests;

/// <summary>
/// Times the mesh algorithms on seeded synthetic meshes of 1,000 and 10,000 devices: the snapshot rebuild, breadth-first search,
/// articulation points, Prim's, Kruskal's and Dijkstra's.
/// </summary>
/// <remarks>
/// A benchmark, so it is excluded from the default run (see <c>default.runsettings</c>) and run on purpose with the command in the
/// README. It prints a table ready to paste. The assertions are the ones that do not depend on a clock: Prim's and Kruskal's agree
/// on the weight of the tree, and the mesh really has about two and a half links per device.
/// <para>
/// <b>The mesh.</b> Each device links to one random device among the thirty before it (so the whole thing is connected), then
/// gets a further 1.75 links on average to devices within thirty places of it either way. Links are local, as radio links are, and
/// the average degree is five, which is 2.5 links per device. A gateway is attached to every 250th device. Nothing here is the demo
/// layout: that one is only 55 devices.
/// </para>
/// <para>
/// <b>Rebuild.</b> One report arrives and the snapshot is read, as it is in production. That builds the graph from the claims and
/// works out the routes along the spanning tree (the service compares them with the last snapshot's, so Prim's runs inside it),
/// so the rebuild row includes Prim's. Reachability, articulation points and shortest costs are not part of it: the snapshot
/// computes those the first time they are asked for.
/// </para>
/// </remarks>
public class GraphScalingBenchmarkTests(ITestOutputHelper output)
{
    private const int Seed = 22;
    private const int GatewayEvery = 250;

    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static string Mac(int index) => $"AA:BB:{index / 65536 % 256:X2}:{index / 256 % 256:X2}:{index % 256:X2}:01";

    /// <summary>One neighbour table per device: every link is reported by the device with the higher index.</summary>
    private static Dictionary<string, List<NeighbourLink>> Mesh(int devices)
    {
        var random = new Random(Seed);
        var tables = Enumerable.Range(0, devices).ToDictionary(Mac, _ => new List<NeighbourLink>(), StringComparer.Ordinal);
        var seen = new HashSet<(int, int)>();

        void Link(int a, int b)
        {
            if (a != b && b >= 0 && b < devices && seen.Add((Math.Min(a, b), Math.Max(a, b))))
            {
                tables[Mac(Math.Max(a, b))].Add(new NeighbourLink(Mac(Math.Min(a, b)), -40 - random.Next(46)));
            }
        }

        for (var i = 0; i < devices; i++)
        {
            if (i > 0)
            {
                Link(i, Math.Max(0, i - 1 - random.Next(30)));
            }

            for (var extra = 1.75; extra > 0; extra--)
            {
                if (random.NextDouble() < Math.Min(extra, 1))
                {
                    Link(i, i + random.Next(-30, 31));
                }
            }

            if (i % GatewayEvery == 0)
            {
                tables[Mac(i)].Add(new NeighbourLink(MeshGateways.All[i / GatewayEvery % MeshGateways.All.Count].Id, -45));
            }
        }

        return tables;
    }

    private static double Milliseconds(Action action, int repeats)
    {
        // Timed, not counted, for the reason given in AvlHistoryBenchmarkTests.Microseconds.
        var warmUp = Stopwatch.StartNew();

        do
        {
            action();
        }
        while (warmUp.ElapsedMilliseconds < 250);

        var times = new List<double>();

        for (var i = 0; i < repeats; i++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        // The median, so one pass that meets a garbage collection does not stand for the rest.
        times.Sort();

        return times[times.Count / 2];
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void The_mesh_algorithms_at_one_thousand_and_ten_thousand_devices()
    {
        // The depth-first search recurses once per level of its tree, which on a mesh this size is thousands of levels. The default
        // 1 MB stack of a test thread is not a fair place to measure it, so the work runs on a thread with room. (Production would
        // use the explicit-stack form past this size: see MeshTraversal.ArticulationPoints.)
        Exception? failure = null;
        var worker = new Thread(
            () =>
            {
                try
                {
                    // Twice, and only the second is reported: the first is the process-wide warm-up.
                    Measure(report: false);
                    Measure(report: true);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            },
            maxStackSize: 512 * 1024 * 1024);

        worker.Start();
        worker.Join();

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void Measure(bool report)
    {
        var rows = new List<string>();
        var shapes = new List<string>();

        foreach (var (devices, repeats) in new[] { (1_000, 25), (10_000, 7) })
        {
            var clock = new FakeTimeProvider(Start);
            var trust = new TrustEngine(Options.Create(new TrustOptions()), clock, NullLogger<TrustEngine>.Instance);
            var topology = new TopologyService(trust, clock, Options.Create(new TopologyOptions { SettleTime = TimeSpan.Zero }));
            var tables = Mesh(devices);

            foreach (var (mac, links) in tables)
            {
                topology.Submit(mac, mac, "zone", new NeighbourReport(0, 0, links));
            }

            var first = tables.First();
            var graph = topology.Current.Graph;

            // One report marks the snapshot out of date, so reading it builds a new one.
            var rebuild = Milliseconds(() =>
            {
                topology.Submit(first.Key, first.Key, "zone", new NeighbourReport(0, 0, first.Value));
                _ = topology.Current;
            }, repeats);

            var bfs = Milliseconds(() => MeshTraversal.NearestGateways(graph), repeats);
            var articulation = Milliseconds(() => MeshTraversal.ArticulationPoints(graph), repeats);
            var prim = Milliseconds(() => MeshSpanning.Prim(graph), repeats);
            var kruskal = Milliseconds(() => MeshSpanning.Kruskal(graph), repeats);
            var dijkstra = Milliseconds(() => MeshSpanning.ShortestCosts(graph), repeats);

            // What can be asserted without a clock: the generator made the mesh it says, and the two spanning algorithms agree.
            var perDevice = 2.0 * graph.Edges.Count / devices;
            Assert.InRange(perDevice, 4.5, 5.5);
            Assert.Equal(MeshSpanning.Prim(graph).TotalWeight, MeshSpanning.Kruskal(graph).TotalWeight, 6);

            rows.Add($"| {devices:N0} | {rebuild:N1} | {bfs:N1} | {articulation:N1} | {prim:N1} | {kruskal:N1} | {dijkstra:N1} |");
            shapes.Add($"| {devices:N0} | {graph.VertexCount:N0} | {graph.Edges.Count:N0} | {perDevice / 2:N2} | {MeshTraversal.FindArticulationVertices(graph).Count:N0} |");
        }

        if (report)
        {
            output.WriteLine("Milliseconds per run (median), Release");
            output.WriteLine("| Devices | Snapshot rebuild | BFS | Articulation points | Prim's | Kruskal's | Dijkstra's |");
            output.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            rows.ForEach(output.WriteLine);

            output.WriteLine(string.Empty);
            output.WriteLine("The meshes");
            output.WriteLine("| Devices | Vertices (with gateways) | Links | Links per device | Articulation points |");
            output.WriteLine("| ---: | ---: | ---: | ---: | ---: |");
            shapes.ForEach(output.WriteLine);
        }
    }
}
