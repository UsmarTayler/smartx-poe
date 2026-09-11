using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using SmartX.Client;
using SmartX.Client.Services;
using SmartX.Client.State;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

// The API address is configuration rather than a constant. The client is a static site deployed
// independently of the backend, so it has to be repointed at a different API without a rebuild.
// Failing loudly here beats every request quietly 404-ing against the client's own origin.
var apiBaseAddress = builder.Configuration["ApiBaseAddress"]
    ?? throw new InvalidOperationException("ApiBaseAddress is missing from wwwroot/appsettings.json.");

builder.Services.AddHttpClient<SmartXApiClient>(client => client.BaseAddress = new Uri(apiBaseAddress));

// Scoped, which in WebAssembly means one instance per browser tab. State that must survive
// navigation lives in these rather than in page components, which are rebuilt on every visit.
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<ApiConnection>();
builder.Services.AddScoped<SensorService>();
builder.Services.AddScoped<TelemetryService>();

// Injected rather than read from DateTimeOffset.UtcNow, matching the API, so anything that stamps
// or ages client-side state can be driven by a controlled clock in a test.
builder.Services.AddSingleton(TimeProvider.System);

await builder.Build().RunAsync();
