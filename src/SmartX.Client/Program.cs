using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using SmartX.Client;
using SmartX.Client.Services;

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

await builder.Build().RunAsync();
