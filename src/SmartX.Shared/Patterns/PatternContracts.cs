using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>Something that happened in the system that an operator might respond to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerKind>))]
public enum TriggerKind
{
    /// <summary>A reading crossed a watch level derived from the sensor's registered range.</summary>
    ThresholdCrossing,

    /// <summary>An alert was raised, by kind and device.</summary>
    AlertRaised,

    /// <summary>A node's trust moved into Degrading or Fault.</summary>
    TrustBand,

    /// <summary>A reading crossed a critical threshold and took the critical lane.</summary>
    CriticalReading
}

/// <summary>Something an operator did, as the pattern engine counts it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperatorActionKind>))]
public enum OperatorActionKind
{
    /// <summary>Opened a device in the Command Stream. A deliberate click, and the strongest sign of interest.</summary>
    Viewed,

    /// <summary>Ran a search that narrowed to a few devices, without opening any. A weaker signal, counted at a lower weight.</summary>
    Searched,

    /// <summary>Issued a command to a device.</summary>
    Command,

    /// <summary>Gave a verdict on an investigation.</summary>
    Verdict
}

/// <summary>How specifically a pattern was learned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PatternLevel>))]
public enum PatternLevel
{
    /// <summary>About particular devices: "MOIST-B below 20% is followed by opening PUMP-A".</summary>
    Specific,

    /// <summary>About kinds of device in a zone: "a moisture sensor in Zone 1 below 20% is followed by a pump in that zone".</summary>
    General
}

/// <summary>What the pattern engine is told a trigger was.</summary>
/// <param name="Kind">The sort of trigger.</param>
/// <param name="Detail">Which one: "Low" or "High", an alert kind, a band, or a critical reason.</param>
/// <param name="Scope">The device's MAC for a specific pattern, or a device class and zone for a general one.</param>
/// <param name="ScopeLabel">The same, in words an operator can read.</param>
public sealed record PatternTrigger(TriggerKind Kind, string Detail, string Scope, string ScopeLabel);

/// <summary>What the pattern engine is told an operator did.</summary>
/// <param name="Kind">Viewed, searched, command or verdict.</param>
/// <param name="Target">The device's MAC for a specific pattern, or a device class for a general one.</param>
/// <param name="TargetLabel">The same, in words an operator can read.</param>
/// <param name="Command">For a command, the action; for a verdict, which one; otherwise empty.</param>
public sealed record PatternAction(OperatorActionKind Kind, string Target, string TargetLabel, string Command);

/// <summary>One learned association: after this trigger, operators tend to do that.</summary>
/// <param name="Trigger">What happened.</param>
/// <param name="Action">What followed.</param>
/// <param name="Level">Whether it was learned about devices or about kinds of device.</param>
/// <param name="Support">How many times, weighted and decayed, the action followed the trigger.</param>
/// <param name="Confidence">Of the times the trigger happened, the share that were followed by the action.</param>
/// <param name="Lift">Confidence divided by how often the action happens anyway. Above 1 means it follows more than chance.</param>
/// <param name="TriggerCount">How many times the trigger happened, decayed.</param>
/// <param name="Baseline">The chance of the action in any window of the same length, regardless of the trigger.</param>
/// <param name="Qualified">Whether it has enough support and lift to count as a pattern.</param>
public sealed record PatternAssociation(
    PatternTrigger Trigger,
    PatternAction Action,
    PatternLevel Level,
    double Support,
    double Confidence,
    double Lift,
    double TriggerCount,
    double Baseline,
    bool Qualified);

/// <summary>The thresholds a pattern has to clear, so a client can show them and not guess.</summary>
/// <param name="Window">How soon after a trigger an action counts as following it.</param>
/// <param name="MinimumSupport">How many times an action must have followed a trigger.</param>
/// <param name="MinimumLift">How much more often than chance it must follow.</param>
/// <param name="HalfLife">How long until a pairing counts half as much.</param>
public sealed record PatternSettings(TimeSpan Window, double MinimumSupport, double MinimumLift, TimeSpan HalfLife);

/// <summary>Learned associations, with the thresholds that decided which of them count.</summary>
/// <param name="Associations">Best first: highest lift, then confidence, then support.</param>
/// <param name="Settings">The thresholds in force.</param>
public sealed record PatternsResponse(IReadOnlyList<PatternAssociation> Associations, PatternSettings Settings);

/// <summary>What tends to follow a kind of trigger, and which level of pattern said so.</summary>
/// <param name="Trigger">The trigger asked about.</param>
/// <param name="AnsweredBy">The level that answered: the specific pattern when it has enough support, else the general one.</param>
/// <param name="Associations">What tends to follow it, best first. Empty when nothing has been learned.</param>
public sealed record PatternAnswer(PatternTrigger Trigger, PatternLevel? AnsweredBy, IReadOnlyList<PatternAssociation> Associations);

/// <summary>What an operator concluded about an investigation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
public enum Verdict
{
    /// <summary>A real fault. Recorded; detection is unchanged.</summary>
    Confirm,

    /// <summary>A false alarm. That node becomes slightly less sensitive to the kind of failure that caused it, within a bound.</summary>
    Dismiss,

    /// <summary>A real problem with a different cause than the one the trust engine blamed. Stored as a label.</summary>
    Relabel
}

/// <summary>The cause an operator gives when relabelling a fault.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FaultCause>))]
public enum FaultCause
{
    /// <summary>The device itself has failed.</summary>
    Hardware,

    /// <summary>The device cannot reach the gateway, or the link is bad.</summary>
    Network,

    /// <summary>The device has lost power or has an unstable supply.</summary>
    Power,

    /// <summary>The conditions around the device, not the device, are the problem.</summary>
    Environmental
}

/// <summary>Where an investigation stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InvestigationStatus>))]
public enum InvestigationStatus
{
    /// <summary>The node is still in fault, or has recovered and nobody has closed it yet.</summary>
    Open,

    /// <summary>The fault cleared, or an operator gave a verdict.</summary>
    Resolved
}

/// <summary>A node's sensitivity to one kind of failure, as an operator's verdicts have left it.</summary>
/// <param name="Outcome">The kind of failure.</param>
/// <param name="Scale">What its weight is multiplied by. One is untouched; the floor is the lowest it can go.</param>
/// <param name="Floor">The lowest the scale can ever be, so detection can be dulled but never switched off.</param>
public sealed record SensitivityState(TrustOutcome Outcome, double Scale, double Floor);

/// <summary>A reading inside an investigation's window.</summary>
/// <param name="Timestamp">When it was taken.</param>
/// <param name="Value">The value; a state is 1 or 0.</param>
/// <param name="Unit">Its unit.</param>
public sealed record InvestigationReading(DateTimeOffset Timestamp, double Value, string? Unit);

/// <summary>What changed about a node's sensitivity when a verdict was given.</summary>
/// <param name="Outcome">The kind of failure.</param>
/// <param name="Before">The scale before the verdict.</param>
/// <param name="After">The scale after it.</param>
public sealed record SensitivityChange(TrustOutcome Outcome, double Before, double After);

/// <summary>A node's fall into fault, gathered into one place an operator can act on.</summary>
/// <param name="Id">Identifies the investigation.</param>
/// <param name="MacAddress">The node.</param>
/// <param name="Room">Where it is.</param>
/// <param name="Status">Open or resolved.</param>
/// <param name="OpenedAt">When the node entered fault.</param>
/// <param name="ResolvedAt">When the fault cleared, or the operator closed it.</param>
/// <param name="Score">The node's trust score as it fell into fault.</param>
/// <param name="Reason">The trust engine's reason, in words.</param>
/// <param name="CausedBy">The kind of failure that weighed most, or null when it could not be told.</param>
/// <param name="WindowStart">The last moment the node was nominal: where its decay began.</param>
/// <param name="WindowEnd">The moment it crossed into fault.</param>
/// <param name="ActiveAlerts">The alerts active on the node when it was opened.</param>
/// <param name="TrustHistory">The node's trust samples inside the window, oldest first.</param>
/// <param name="ReadingCount">How many readings the node took inside the window.</param>
/// <param name="Readings">The most recent of them, oldest first, bounded.</param>
/// <param name="Precedent">What operators did the last time this node, or one like it, faulted.</param>
/// <param name="Verdict">The operator's verdict, if given.</param>
/// <param name="Cause">The cause the operator gave when relabelling.</param>
/// <param name="VerdictAt">When the verdict was given.</param>
/// <param name="SensitivityChange">What the verdict changed about detection, if it changed anything.</param>
/// <param name="Sensitivity">The node's sensitivity now.</param>
public sealed record InvestigationDetails(
    Guid Id,
    string MacAddress,
    string Room,
    InvestigationStatus Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    double Score,
    string Reason,
    TrustOutcome? CausedBy,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<AlertKey> ActiveAlerts,
    IReadOnlyList<TrustHistoryPoint> TrustHistory,
    int ReadingCount,
    IReadOnlyList<InvestigationReading> Readings,
    PatternAnswer Precedent,
    Verdict? Verdict,
    FaultCause? Cause,
    DateTimeOffset? VerdictAt,
    SensitivityChange? SensitivityChange,
    IReadOnlyList<SensitivityState> Sensitivity);

/// <summary>An operator's answer to an investigation.</summary>
/// <param name="Verdict">Confirm, Dismiss or Relabel.</param>
/// <param name="Cause">Required for Relabel: what the real cause was.</param>
public sealed record VerdictRequest(Verdict Verdict, FaultCause? Cause = null);

/// <summary>An operator opening a device, as the client reports it.</summary>
/// <param name="MacAddress">The device that was opened.</param>
public sealed record SelectionRequest(string MacAddress);
