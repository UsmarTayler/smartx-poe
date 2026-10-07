using System.Reflection;
using System.Text.RegularExpressions;
using SmartX.Api.Alerts;
using SmartX.Api.Commands;
using SmartX.Api.History;
using SmartX.Api.Ingestion;
using SmartX.Api.Realtime;
using SmartX.Api.Search;
using SmartX.Api.Storage;
using SmartX.Api.Trust;
using SmartX.DeviceSimulator;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// The domain does not know SignalR exists.
/// </summary>
/// <remarks>
/// The trust engine, the alert set, the command service, the ingestion worker, the history and the search index raise
/// ordinary events. Only the broadcaster, which turns them into messages, and the hub reference the transport. Three
/// independent checks, because each can be fooled alone: what the assemblies reference, what the domain types expose in
/// their signatures, and what the source files import.
/// </remarks>
public class DomainIsolationTests
{
    private const string SignalRNamespace = "Microsoft.AspNetCore.SignalR";

    private static readonly string[] DomainNamespaces =
    [
        "SmartX.Api.Trust",
        "SmartX.Api.Alerts",
        "SmartX.Api.Commands",
        "SmartX.Api.Ingestion",
        "SmartX.Api.History",
        "SmartX.Api.Search",
        "SmartX.Api.Storage",
    ];

    [Fact]
    public void The_shared_contracts_and_the_simulator_reference_no_signalr_assembly()
    {
        // Shared is the contract both sides compile against, and the simulator is a device. Neither has any
        // business with the push transport.
        foreach (var assembly in new[] { typeof(TrustBand).Assembly, typeof(DeviceFleet).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty);

            Assert.DoesNotContain(references, name => name.Contains("SignalR", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void No_domain_type_exposes_a_signalr_type_in_a_field_property_method_event_or_constructor()
    {
        var domainTypes = typeof(ITrustEngine).Assembly.GetTypes()
            .Where(type => type.Namespace is { } ns && DomainNamespaces.Contains(ns))
            .ToList();

        // The check would pass vacuously if it found nothing to check.
        Assert.True(domainTypes.Count > 30, $"Only {domainTypes.Count} domain types were found.");

        var offenders = domainTypes.Where(type => ExposedTypes(type).Any(MentionsSignalR)).Select(type => type.FullName!).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_broadcaster_and_the_hub_are_the_only_api_types_that_reference_signalr()
    {
        var referencing = typeof(ITrustEngine).Assembly.GetTypes()
            .Where(type => type.IsClass || type.IsInterface)
            .Where(type => type.Namespace is not null && !type.Namespace.StartsWith("Microsoft", StringComparison.Ordinal))
            .Where(type => !type.Name.Contains('<', StringComparison.Ordinal) && !type.Name.Contains('+', StringComparison.Ordinal))
            .Where(type => ExposedTypes(type).Any(MentionsSignalR))
            .Select(type => type.Name)
            .Order()
            .ToList();

        // The broadcaster holds the hub context; the hub is a hub. Nothing in the domain does.
        Assert.All(referencing, name => Assert.Contains(name, new[] { nameof(RealtimeBroadcaster), nameof(TelemetryHub), nameof(ITelemetryClient) }));
        Assert.Contains(nameof(RealtimeBroadcaster), referencing);
        Assert.Contains(nameof(TelemetryHub), referencing);
    }

    [Fact]
    public void No_domain_source_file_imports_signalr()
    {
        var root = RepositoryRoot();
        var folders = DomainNamespaces
            .Select(ns => Path.Combine(root, "src", "SmartX.Api", ns["SmartX.Api.".Length..]))
            .Append(Path.Combine(root, "src", "SmartX.Shared"))
            .Append(Path.Combine(root, "src", "SmartX.DeviceSimulator"))
            .ToList();

        var import = new Regex(@"^\s*(global\s+)?using\s+(static\s+)?Microsoft\.AspNetCore\.SignalR", RegexOptions.Multiline);
        var files = folders.Where(Directory.Exists).SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.True(files.Count > 50, $"Only {files.Count} source files were scanned.");

        var offenders = files.Where(file => import.IsMatch(File.ReadAllText(file))).Select(Path.GetFileName).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_shared_and_simulator_projects_do_not_depend_on_signalr_packages()
    {
        var root = RepositoryRoot();

        foreach (var project in new[] { "SmartX.Shared/SmartX.Shared.csproj", "SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj" })
        {
            var text = File.ReadAllText(Path.Combine(root, "src", project));

            Assert.DoesNotContain("SignalR", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_domain_services_can_be_built_and_used_with_no_hub_at_all()
    {
        // The point of the separation, as a fact: these are all constructed without a transport, and raise their
        // events to nobody.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var (trust, _) = TrustTestHost.Create();
        var mesh = MeshTestHost.Create(clock);

        var changes = new List<AlertChange>();
        mesh.Alerts.Changed += changes.Add;

        mesh.Alerts.Raise(new AlertKey("AA:BB:CC:00:00:01", AlertKind.Fault));
        trust.Track("AA:BB:CC:00:00:01");

        var processed = new ProcessedReadings();
        processed.Publish(default);

        Assert.Equal(AlertChangeKind.Raised, Assert.Single(changes).Kind);
    }

    [Fact]
    public void A_subscriber_that_throws_cannot_break_the_domain_event_it_was_listening_to()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var mesh = MeshTestHost.Create(clock);
        var heard = 0;

        mesh.Alerts.Changed += _ => throw new InvalidOperationException("a faulty subscriber");
        mesh.Alerts.Changed += _ => heard++;

        // The alert is raised, and the second subscriber still hears it.
        Assert.Equal(RaiseOutcome.Raised, mesh.Alerts.Raise(new AlertKey("AA:BB:CC:00:00:01", AlertKind.Fault)));
        Assert.Equal(1, heard);
        Assert.Single(mesh.Alerts.Active());

        var readings = new ProcessedReadings();
        readings.Processed += _ => throw new InvalidOperationException("a faulty subscriber");
        readings.Processed += _ => heard++;

        readings.Publish(default);

        Assert.Equal(2, heard);
    }

    /// <summary>Every type a type's shape depends on: its base, interfaces, members and their signatures.</summary>
    private static IEnumerable<Type> ExposedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (var contract in type.GetInterfaces())
        {
            yield return contract;
        }

        foreach (var field in type.GetFields(all))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(all))
        {
            yield return property.PropertyType;
        }

        foreach (var @event in type.GetEvents(all))
        {
            yield return @event.EventHandlerType!;
        }

        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }

            if (method is MethodInfo info)
            {
                yield return info.ReturnType;
            }
        }
    }

    private static bool MentionsSignalR(Type type)
    {
        if (type.IsGenericType && type.GetGenericArguments().Any(MentionsSignalR))
        {
            return true;
        }

        if (type.HasElementType && type.GetElementType() is { } element && MentionsSignalR(element))
        {
            return true;
        }

        return type.Namespace?.StartsWith(SignalRNamespace, StringComparison.Ordinal) == true
            || type.Assembly.GetName().Name?.Contains("SignalR", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmartX.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (the folder with SmartX.sln) was not found above the test assembly.");
    }
}
