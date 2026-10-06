using Microsoft.Extensions.Options;
using SmartX.Api.Alerts;
using SmartX.Api.Attachments;
using SmartX.Api.Commands;
using SmartX.Api.Endpoints;
using SmartX.Api.History;
using SmartX.Api.Ingestion;
using SmartX.Api.Search;
using SmartX.Api.Storage;
using SmartX.Api.Trust;

var builder = WebApplication.CreateBuilder(args);

// The Blazor client is served from its own origin and talks to this API over HTTP, so every
// request it makes is cross-origin and would be blocked without an explicit policy. The permitted
// origins come from configuration rather than being hard-coded, so the dev ports can move (or a
// deployed origin be added) without touching this file.
const string ClientCorsPolicy = "SmartXClient";

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddPolicy(ClientCorsPolicy, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

// Singletons, because these are the storage. A scoped or transient lifetime would hand each
// request its own empty dictionary, so a sensor registered by one call would not exist for the
// next — the registration would appear to succeed and then vanish. The implementations are
// thread-safe precisely because a singleton is shared across concurrent requests.
builder.Services.AddSingleton<ISensorRegistry, InMemorySensorRegistry>();

// Retention is bound through IOptions like the trust section, and validated at startup so a
// negative value stops the API with a message instead of failing on the first telemetry request.
builder.Services
    .AddOptions<TelemetryOptions>()
    .Bind(builder.Configuration.GetSection(TelemetryOptions.SectionName))
    .Validate(options => options.RetentionPerNode >= 0, "Telemetry:RetentionPerNode cannot be negative.")
    .ValidateOnStart();

builder.Services.AddSingleton<ITelemetryStore, InMemoryTelemetryStore>();

// Which deployment hierarchy this gateway manages. Decided in a factory rather than by reading
// builder.Configuration here, so configuration a test host adds is seen too.
builder.Services
    .AddOptions<ZoneOptions>()
    .Bind(builder.Configuration.GetSection(ZoneOptions.SectionName))
    .Validate(options => Enum.IsDefined(options.Hierarchy), "Zones:Hierarchy must be Brief or Demo.")
    .ValidateOnStart();

builder.Services.AddSingleton<IZoneStore>(services => new InMemoryZoneStore(
    services.GetRequiredService<IOptions<ZoneOptions>>().Value.Hierarchy == ZoneHierarchy.Demo
        ? new SmartX.Shared.ZoneCatalogue(DemoZoneHierarchy.Zones)
        : InMemoryZoneStore.BriefHierarchy));

// Device management. An operator writes the desired state here; each device polls for its own and applies
// it, which is the device-shadow pattern named in SimulationEndpoints.
builder.Services.AddSingleton<IDeviceShadowStore, InMemoryDeviceShadowStore>();

// Ingestion. Readings are validated on the request thread and processed behind the queues by one worker.
builder.Services
    .AddOptions<IngestionOptions>()
    .Bind(builder.Configuration.GetSection(IngestionOptions.SectionName))
    .Validate(options => options.MaxQueueDepth > 0, "Ingestion:MaxQueueDepth must be positive.")
    .Validate(options => options.MaxCriticalQueueDepth > 0, "Ingestion:MaxCriticalQueueDepth must be positive.")
    .Validate(options => options.MaxConsecutiveCritical > 0, "Ingestion:MaxConsecutiveCritical must be positive.")
    .Validate(options => options.RetryAfterSeconds > 0, "Ingestion:RetryAfterSeconds must be positive.")
    .ValidateOnStart();

// Commands. A command is an instruction to a device that may not receive it, so it has a lifecycle, a timeout
// and an undo stack. See CommandService. The log sits behind an interface because it is about to move into a
// sorted history store.
builder.Services
    .AddOptions<CommandOptions>()
    .Bind(builder.Configuration.GetSection(CommandOptions.SectionName))
    .Validate(options => options.PendingTimeout > TimeSpan.Zero, "Commands:PendingTimeout must be positive.")
    .Validate(options => options.DeliveredTimeout > TimeSpan.Zero, "Commands:DeliveredTimeout must be positive.")
    .Validate(options => options.SweepInterval > TimeSpan.Zero, "Commands:SweepInterval must be positive.")
    .Validate(options => options.MaxUndoDepth >= 1, "Commands:MaxUndoDepth must be at least 1.")
    .ValidateOnStart();

builder.Services.AddSingleton<ICommandStore, InMemoryCommandStore>();
builder.Services.AddSingleton<ICommandLog, HistoryCommandLog>();
builder.Services.AddSingleton<IActuatorStateStore, InMemoryActuatorStateStore>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddHostedService<CommandTimeoutMonitor>();

// History. One time-ordered record of readings, commands, alerts, trust transitions and searches, behind an
// interface because the sorted-dictionary store is the implementation for now and a balanced tree replaces it
// later. ArrivalSequence is the one counter every entry takes its number from.
builder.Services
    .AddOptions<HistoryOptions>()
    .Bind(builder.Configuration.GetSection(HistoryOptions.SectionName))
    .Validate(options => options.RetentionWindow > TimeSpan.Zero, "History:RetentionWindow must be positive.")
    .Validate(options => options.EvictionInterval > TimeSpan.Zero, "History:EvictionInterval must be positive.")
    .Validate(options => options.DefaultTimelineWindow > TimeSpan.Zero, "History:DefaultTimelineWindow must be positive.")
    .Validate(options => options.DefaultMaxPoints >= 1, "History:DefaultMaxPoints must be at least 1.")
    .Validate(options => options.MaxPointsLimit >= options.DefaultMaxPoints, "History:MaxPointsLimit must be at least DefaultMaxPoints.")
    .Validate(options => options.DefaultEventLimit >= 1, "History:DefaultEventLimit must be at least 1.")
    .Validate(options => options.MaxEventLimit >= options.DefaultEventLimit, "History:MaxEventLimit must be at least DefaultEventLimit.")
    .ValidateOnStart();

builder.Services.AddSingleton<ArrivalSequence>();
builder.Services.AddSingleton<IHistoryStore, SortedDictionaryHistoryStore>();
builder.Services.AddHostedService<HistoryEvictionService>();

// Alerts and search. One lock covers the active alert set and the device indexes, so an alert appearing and the
// device becoming findable by it are a single step. The bridge feeds both from the trust engine's own events.
builder.Services
    .AddOptions<AlertOptions>()
    .Bind(builder.Configuration.GetSection(AlertOptions.SectionName))
    .Validate(options => options.DisconnectedAfterIntervals >= 1, "Alerts:DisconnectedAfterIntervals must be at least 1.")
    .Validate(options => options.ClearAfterValidReadings >= 1, "Alerts:ClearAfterValidReadings must be at least 1.")
    .ValidateOnStart();

builder.Services.AddSingleton<DeviceIndex>();
builder.Services.AddSingleton<AlertTracker>();
builder.Services.AddSingleton<ActivityRecorder>();
builder.Services.AddHostedService<TrustEventBridge>();

builder.Services.AddSingleton<TelemetryIngestionQueue>();
builder.Services.AddSingleton<CriticalClassifier>();
builder.Services.AddHostedService<TelemetryProcessor>();

// Trust. The clock is injected rather than read from DateTimeOffset.UtcNow so that decay and
// heartbeats — both entirely about the passage of time — can be tested without waiting for it.
builder.Services.AddSingleton(TimeProvider.System);

// ValidateOnStart means a misconfigured trust section stops the API at startup with a message
// naming the bad value, instead of surfacing later as a nonsensical score on the first packet.
builder.Services
    .AddOptions<TrustOptions>()
    .Bind(builder.Configuration.GetSection(TrustOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TrustOptions>, TrustOptionsValidator>();

// A singleton for the same reason as the stores: trust is accumulated state, and a per-request
// engine would start every node back at 0.5 on every call.
builder.Services.AddSingleton<ITrustEngine, TrustEngine>();
builder.Services.AddHostedService<HeartbeatMonitor>();

// Attachments. The encryption key is read from configuration but is deliberately absent from every
// committed settings file: it comes from user secrets in development and the environment or a key vault
// elsewhere. ValidateOnStart refuses to start outside Development without one.
builder.Services
    .AddOptions<AttachmentOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AttachmentOptions>, AttachmentOptionsValidator>();
builder.Services.AddSingleton<AttachmentKeyProvider>();
builder.Services.AddSingleton<AttachmentStorage>();
builder.Services.AddSingleton<IAttachmentStore, FileAttachmentStore>();
builder.Services.AddSingleton<AttachmentUploader>();

var app = builder.Build();

app.UseCors(ClientCorsPolicy);

// Everything the client calls hangs off /api. Feature areas attach themselves to this group.
var api = app.MapGroup("/api");
api.MapHealthEndpoints();
api.MapSensorEndpoints();
api.MapTelemetryEndpoints();
api.MapZoneEndpoints();
api.MapTrustEndpoints();
api.MapAttachmentEndpoints();
api.MapSimulationEndpoints();
api.MapPipelineEndpoints();
api.MapCommandEndpoints();
api.MapHistoryEndpoints();
api.MapAlertEndpoints();
api.MapSearchEndpoints();

app.Run();

// Exposed so the test project can spin the API up in-memory via WebApplicationFactory.
// A top-level-statements program has an internal Program class, which the test assembly
// cannot see without this declaration.
public partial class Program;
