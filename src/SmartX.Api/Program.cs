using Microsoft.Extensions.Options;
using SmartX.Api.Endpoints;
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
builder.Services.AddSingleton<IZoneStore, InMemoryZoneStore>();

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

var app = builder.Build();

app.UseCors(ClientCorsPolicy);

// Everything the client calls hangs off /api. Feature areas attach themselves to this group.
var api = app.MapGroup("/api");
api.MapHealthEndpoints();
api.MapSensorEndpoints();
api.MapTelemetryEndpoints();
api.MapZoneEndpoints();
api.MapTrustEndpoints();

app.Run();

// Exposed so the test project can spin the API up in-memory via WebApplicationFactory.
// A top-level-statements program has an internal Program class, which the test assembly
// cannot see without this declaration.
public partial class Program;
