using SmartX.Api.Endpoints;

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

var app = builder.Build();

app.UseCors(ClientCorsPolicy);

// Everything the client calls hangs off /api. Feature areas attach themselves to this group.
var api = app.MapGroup("/api");
api.MapHealthEndpoints();

app.Run();

// Exposed so the test project can spin the API up in-memory via WebApplicationFactory.
// A top-level-statements program has an internal Program class, which the test assembly
// cannot see without this declaration.
public partial class Program;
