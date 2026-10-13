using Microsoft.Extensions.Options;
using SmartX.Api.Patterns;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>A device directory that answers what a test says, so the engine is tested without a registry or a zone tree.</summary>
internal sealed class FakeDirectory : IDeviceDirectory
{
    private readonly Dictionary<string, DeviceProfile> _profiles = new(StringComparer.Ordinal);

    public FakeDirectory Add(
        string mac,
        string room,
        string deviceClass,
        string classLabel,
        string zone,
        string generalZone,
        string generalZoneName,
        bool isState = false,
        double minimum = 0,
        double maximum = 100)
    {
        _profiles[mac] = new DeviceProfile(mac, room, zone, generalZone, generalZoneName, deviceClass, classLabel, isState, minimum, maximum);

        return this;
    }

    public bool TryDescribe(string macAddress, out DeviceProfile profile) => _profiles.TryGetValue(macAddress, out profile!);

    public IReadOnlyList<DeviceProfile> Similar(DeviceProfile device) =>
    [
        .. _profiles.Values
            .Where(other => other.Class == device.Class && other.GeneralZoneId == device.GeneralZoneId)
            .OrderBy(other => other.MacAddress, StringComparer.Ordinal),
    ];
}

/// <summary>An engine that records what it is told, for tests of the things that feed it.</summary>
internal sealed class RecordingEngine : IPatternEngine
{
    public List<TriggerEvent> Triggers { get; } = [];

    public List<OperatorAction> Actions { get; } = [];

    public PatternSettings Settings { get; } = new(TimeSpan.FromMinutes(10), 3, 1.5, TimeSpan.FromDays(7));

    public int TriggersInWindow => Triggers.Count;

    public void RecordTrigger(TriggerEvent trigger)
    {
        Triggers.Add(trigger);
        TriggerRecorded?.Invoke(trigger);
    }

    public void RecordAction(OperatorAction action)
    {
        Actions.Add(action);
        ActionRecorded?.Invoke(action);
    }

    public void Replay(IEnumerable<HistoricEvent> history)
    {
        foreach (var item in history)
        {
            if (item.Trigger is { } trigger)
            {
                Triggers.Add(trigger);
            }
            else if (item.Action is { } action)
            {
                Actions.Add(action);
            }
        }
    }

    public event Action<TriggerEvent>? TriggerRecorded;

    public event Action<OperatorAction>? ActionRecorded;

    public IReadOnlyList<PatternAssociation> Associations(PatternQuery query, DateTimeOffset now) => [];

    public PatternAnswer Predict(TriggerKind kind, string detail, string macAddress, DateTimeOffset now) =>
        new(new PatternTrigger(kind, detail, macAddress, macAddress), null, []);

    public IReadOnlyList<PatternAnswer> ForDevice(string macAddress, DateTimeOffset now) => [];
}

/// <summary>The devices and helpers the pattern tests share. A small mesh with two zones and a brief's worth of habit.</summary>
internal static class PatternKit
{
    public const string MoistB = "B8:27:EB:20:00:0B";    // moisture sensor in Sub-Zone B, which is under Zone 1
    public const string MoistC = "B8:27:EB:20:00:0C";    // moisture sensor in Zone 1 itself
    public const string MoistD = "B8:27:EB:20:00:0D";    // moisture sensor in Zone 4
    public const string MoistE = "B8:27:EB:20:00:0E";    // another moisture sensor in Zone 1, with no history of its own
    public const string PumpA = "B8:27:EB:20:00:1A";     // pump in Zone 1
    public const string PumpB = "B8:27:EB:20:00:1B";     // pump in Zone 1
    public const string PumpX = "B8:27:EB:20:00:1C";     // pump in Zone 4
    public const string Busy = "B8:27:EB:20:00:99";      // a valve whose page is open all day
    public const string Temperature = "B8:27:EB:20:00:2A"; // °C sensor registered for 0 to 50
    public const string ValveState = "B8:27:EB:20:00:3A";  // reports on or off

    public static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public static FakeDirectory Directory() => new FakeDirectory()
        .Add(MoistB, "Moisture B", "Environmental/%RH", "%RH sensor", "sub-zone-b", "zone-1", "Zone 1")
        .Add(MoistC, "Moisture C", "Environmental/%RH", "%RH sensor", "zone-1", "zone-1", "Zone 1")
        .Add(MoistD, "Moisture D", "Environmental/%RH", "%RH sensor", "zone-4", "zone-4", "Zone 4")
        .Add(MoistE, "Moisture E", "Environmental/%RH", "%RH sensor", "zone-1", "zone-1", "Zone 1")
        .Add(PumpA, "Pump A", "Actuator/Pump", "pump", "zone-1", "zone-1", "Zone 1", isState: true, maximum: 1)
        .Add(PumpB, "Pump B", "Actuator/Pump", "pump", "zone-1", "zone-1", "Zone 1", isState: true, maximum: 1)
        .Add(PumpX, "Pump X", "Actuator/Pump", "pump", "zone-4", "zone-4", "Zone 4", isState: true, maximum: 1)
        .Add(Busy, "Busy Valve", "Actuator/Valve", "valve", "zone-1", "zone-1", "Zone 1", isState: true, maximum: 1)
        .Add(Temperature, "Cold Room", "Environmental/°C", "°C sensor", "zone-1", "zone-1", "Zone 1", maximum: 50)
        .Add(ValveState, "Inlet", "Actuator/Valve", "valve", "zone-1", "zone-1", "Zone 1", isState: true, maximum: 1);

    public static PatternEngine Engine(Action<PatternOptions>? configure = null, IDeviceDirectory? directory = null)
    {
        var options = new PatternOptions();
        configure?.Invoke(options);

        return new PatternEngine(Options.Create(options), directory ?? Directory());
    }

    public static TriggerEvent Low(string mac, DateTimeOffset at) => new(TriggerKind.ThresholdCrossing, mac, "Low", at);

    public static OperatorAction View(string mac, DateTimeOffset at) => new(OperatorActionKind.Viewed, mac, string.Empty, at);

    /// <summary>The brief's example once: a moisture sensor drops, and a minute later the operator opens the pump.</summary>
    public static void Respond(PatternEngine engine, string sensor, string device, DateTimeOffset at, TimeSpan? after = null)
    {
        engine.RecordTrigger(Low(sensor, at));
        engine.RecordAction(View(device, at + (after ?? TimeSpan.FromMinutes(1))));
    }

    /// <summary>Applies events in the order they happened, whichever order they were listed in.</summary>
    public static void Replay(PatternEngine engine, IEnumerable<(DateTimeOffset At, Action Do)> events)
    {
        foreach (var (_, action) in events.OrderBy(item => item.At))
        {
            action();
        }
    }

    public static IReadOnlyList<PatternAssociation> All(PatternEngine engine, DateTimeOffset now, PatternQuery? query = null) =>
        engine.Associations((query ?? new PatternQuery()) with { IncludeWeak = true }, now);
}
