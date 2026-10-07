using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using SmartX.Client;
using SmartX.Client.Realtime;
using SmartX.Client.Services;
using SmartX.Client.State;
using SmartX.Shared;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The WebAssembly host does not apply the "Logging" section of appsettings.json by itself. Without this
// the configured Warning level is ignored, and every HTTP request writes four Information lines to the
// browser console — hundreds a minute on the trust and telemetry pages, burying any real error. Only the
// default level is read, which is all this app sets; binding per-category levels would take an extra
// package for no gain.
if (Enum.TryParse<LogLevel>(builder.Configuration["Logging:LogLevel:Default"], out var minimumLevel))
{
    builder.Logging.SetMinimumLevel(minimumLevel);
}

builder.Services.AddMudServices();

// The API address is configuration rather than a constant. The client is a static site deployed
// independently of the backend, so it has to be repointed at a different API without a rebuild.
// Failing loudly here beats every request quietly 404-ing against the client's own origin.
var apiBaseAddress = builder.Configuration["ApiBaseAddress"]
    ?? throw new InvalidOperationException("ApiBaseAddress is missing from wwwroot/appsettings.json.");

// Resolved against the page's own address, so the setting may be relative. In development it is absolute,
// pointing at the API's own port. In Docker it is "/", because the client's web server forwards /api to the
// API container and the browser only ever talks to one origin. An absolute value ignores the base entirely.
var apiBaseUri = new Uri(new Uri(builder.HostEnvironment.BaseAddress), apiBaseAddress);

builder.Services.AddHttpClient<SmartXApiClient>(client => client.BaseAddress = apiBaseUri);

// Scoped, which in WebAssembly means one instance per browser tab. State that must survive
// navigation lives in these rather than in page components, which are rebuilt on every visit.
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<ApiConnection>();
builder.Services.AddScoped<SensorService>();
builder.Services.AddScoped<TelemetryService>();
builder.Services.AddScoped<AttachmentService>();
// Real-time. One connection to the hub for the whole tab, built once here and owned by RealtimeConnection; every
// feed is built on it, and pages only ever start and stop feeds. Scoped, like everything that must outlive a page
// and exist once per tab. The hub lives on the API's origin, found the same way the REST API is, so in Docker it is
// reached through the same web server and in development it is cross-origin.
var realtimeOptions = builder.Configuration.GetSection(RealtimeClientOptions.SectionName).Get<RealtimeClientOptions>()
    ?? new RealtimeClientOptions();
var hubUri = new Uri(apiBaseUri, RealtimeTopics.HubPath.TrimStart('/'));

builder.Services.AddSingleton(realtimeOptions);
builder.Services.AddScoped<IRealtimeHub>(_ => new SignalRHub(hubUri, realtimeOptions));
builder.Services.AddScoped<RealtimeConnection>();
builder.Services.AddScoped<IRealtimeSnapshots, ApiRealtimeSnapshots>();

// The trust feed that was the whole of Part 1 is now the fallback, and the one the panel gets is the push feed. It
// is built on the polling feed so it can hand over to it when the hub cannot be reached.
builder.Services.AddScoped<PollingTrustFeed>();
builder.Services.AddScoped<ITrustFeed, SignalRTrustFeed>();
builder.Services.AddScoped<IAlertFeed, AlertFeed>();
builder.Services.AddScoped<ICommandFeed, CommandFeed>();
builder.Services.AddScoped<ITelemetryStream, TelemetryStream>();
builder.Services.AddScoped<IPipelineFeed, PipelineFeed>();

// Injected rather than read from DateTimeOffset.UtcNow, matching the API, so anything that stamps
// or ages client-side state can be driven by a controlled clock in a test.
builder.Services.AddSingleton(TimeProvider.System);

await builder.Build().RunAsync();
