using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>What one click on a suggestion does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SuggestedActionKind>))]
public enum SuggestedActionKind
{
    /// <summary>Open the device in the Command Stream.</summary>
    OpenDevice,

    /// <summary>Issue a command to the device, through the same path as one typed by hand.</summary>
    Command
}

/// <summary>The one-click action a suggestion carries.</summary>
/// <param name="Kind">Open the device, or issue a command to it.</param>
/// <param name="MacAddress">The device.</param>
/// <param name="Room">Where it is, as registered: the name an operator knows it by.</param>
/// <param name="Command">For a command, which one.</param>
/// <param name="Level">For a command that takes one, the level. Null otherwise.</param>
/// <param name="Label">The button's words: "Turn on Pump A", "Open Pump A".</param>
public sealed record SuggestedAction(
    SuggestedActionKind Kind,
    string MacAddress,
    string Room,
    CommandAction? Command,
    double? Level,
    string Label);

/// <summary>Why a pattern deserves to be believed: how often it has held, and how much of that is real.</summary>
/// <param name="Times">How many times the action followed the trigger.</param>
/// <param name="OfTimes">How many times the trigger happened.</param>
/// <param name="SeededTimes">How many of <paramref name="Times"/> came from the seeded history and were not observed.</param>
/// <param name="Confidence">Of the times the trigger happened, the share followed by the action (decayed).</param>
/// <param name="Lift">How many times more likely than usual. Above one, the trigger made the difference.</param>
/// <param name="Summary">
/// The evidence in a sentence: "Operators did this 7 of the last 8 times (5 from seeded history). 3.2× more likely than
/// usual." Built once on the server, so every client says the same thing.
/// </param>
public sealed record SuggestionEvidence(
    int Times,
    int OfTimes,
    int SeededTimes,
    double Confidence,
    double Lift,
    string Summary);

/// <summary>Something an operator is likely to want to do now, and the reasons for thinking so.</summary>
/// <param name="Id">Identifies this suggestion for this occurrence of its trigger: stable across recomputations, new for a new trigger.</param>
/// <param name="What">What to do: "Turn on Pump A".</param>
/// <param name="Why">What happened: "Moisture Sensor B dropped below 20 %". The time is <paramref name="TriggeredAt"/>.</param>
/// <param name="Evidence">What the pattern rests on.</param>
/// <param name="Level">Whether it came from this device's own pattern or from similar devices.</param>
/// <param name="LevelNote">The level in words: "This device's own pattern", "Similar devices: a %RH sensor in Zone 1".</param>
/// <param name="Score">How highly it ranks, from 0 to 1. Higher first. Documented on the suggestion service.</param>
/// <param name="Weight">
/// What "Not useful" has done to the pattern's weight: one when nobody has objected, down to a floor, never zero. The score
/// already includes it.
/// </param>
/// <param name="Action">What one click does.</param>
/// <param name="TriggerKind">What kind of event this answers.</param>
/// <param name="TriggerMac">The device the trigger happened to.</param>
/// <param name="TriggerRoom">Its name.</param>
/// <param name="TriggeredAt">When the trigger happened.</param>
/// <param name="ExpiresAt">When the trigger ages out of the pattern window and this stops being offered.</param>
/// <param name="ComputedAt">When the server last worked this out.</param>
public sealed record Suggestion(
    string Id,
    string What,
    string Why,
    SuggestionEvidence Evidence,
    PatternLevel Level,
    string LevelNote,
    double Score,
    double Weight,
    SuggestedAction Action,
    TriggerKind TriggerKind,
    string TriggerMac,
    string TriggerRoom,
    DateTimeOffset TriggeredAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ComputedAt);

/// <summary>A suggestion an operator marked "Not useful", kept for a while so the mark can be taken back.</summary>
/// <param name="Suggestion">What was dismissed.</param>
/// <param name="DismissedAt">When.</param>
/// <param name="WeightBefore">The pattern's weight before.</param>
/// <param name="WeightAfter">And after.</param>
public sealed record DismissedSuggestion(Suggestion Suggestion, DateTimeOffset DismissedAt, double WeightBefore, double WeightAfter);

/// <summary>What an insight is about.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InsightKind>))]
public enum InsightKind
{
    /// <summary>A node whose trust is falling and is projected to reach Fault soon. Not yet a fault.</summary>
    Anticipated,

    /// <summary>A node in Fault, with an open investigation, or with active out-of-range alerts. Already wrong.</summary>
    Current
}

/// <summary>A device an operator should look at before anyone searches for it.</summary>
/// <param name="Id">Identifies the insight: its kind and the device.</param>
/// <param name="Kind">Anticipated or current.</param>
/// <param name="MacAddress">The device.</param>
/// <param name="Room">Its name.</param>
/// <param name="Headline">The device and what is the matter: "Rack Row 3 temperature sensor: trust falling".</param>
/// <param name="Detail">More: "Projected to reach Fault in about 6 minutes." or "In Fault, 2 alerts, investigation open."</param>
/// <param name="Reasons">The same, as separate points.</param>
/// <param name="Severity">How urgent, from 0 to 1. Higher first.</param>
/// <param name="Band">The node's trust band now.</param>
/// <param name="Score">Its trust score now.</param>
/// <param name="Since">When this began to be so.</param>
/// <param name="ProjectedFaultAt">For an anticipated insight, when the fitted line reaches the Fault threshold.</param>
/// <param name="FitQuality">For an anticipated insight, R² of the fit: how well a straight line describes the samples.</param>
/// <param name="InvestigationId">The open investigation, if there is one, so a client can open it.</param>
public sealed record Insight(
    string Id,
    InsightKind Kind,
    string MacAddress,
    string Room,
    string Headline,
    string Detail,
    IReadOnlyList<string> Reasons,
    double Severity,
    TrustBand Band,
    double Score,
    DateTimeOffset Since,
    DateTimeOffset? ProjectedFaultAt,
    double? FitQuality,
    Guid? InvestigationId);

/// <summary>
/// The suggestions and insights as of a sequence number. Used both as the snapshot and as every push on the topic.
/// </summary>
/// <param name="Sequence">Its place in the suggestions topic's order.</param>
/// <param name="Suggestions">Ranked, best first. The whole list: a lost or reordered message is harmless because the next one replaces it.</param>
/// <param name="Insights">Anticipated first, then current, each most urgent first.</param>
/// <param name="Dismissed">The latest suggestions marked "Not useful", newest first, which can still be taken back.</param>
/// <param name="ComputedAt">When the server worked it out. A client can measure how long the push took by comparing it with a suggestion's trigger time.</param>
public sealed record SuggestionsUpdate(
    long Sequence,
    IReadOnlyList<Suggestion> Suggestions,
    IReadOnlyList<Insight> Insights,
    IReadOnlyList<DismissedSuggestion> Dismissed,
    DateTimeOffset ComputedAt) : IRealtimeMessage;
