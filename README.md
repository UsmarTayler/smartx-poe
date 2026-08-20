# Smart-X

An IoT telemetry ingestion and mesh-management system, built as a university Portfolio of Evidence.

Smart-X is designed to sit between a field network of sensors and the people who need to act on
what those sensors report. The brief covers three pillars:

1. **Sensor Data Ingestion and Telemetry** — accept readings from field devices, validate them, and
   store the resulting telemetry stream.
2. **Real-Time Command Stream and History** — push commands out to devices and keep a replayable
   record of what was sent and when. *(not yet implemented)*
3. **Network Topology and Mesh Routing** — map the mesh, track which nodes can still reach the
   gateway, and route around failures. *(not yet implemented)*

The repository currently contains the foundations only: a compiling solution, a health endpoint,
and a landing page that proves the client reaches the API over HTTP.

## Layout

```
SmartX.sln
src/
  SmartX.Shared/    contracts shared by the API and the client
  SmartX.Api/       ASP.NET Core Minimal API
  SmartX.Client/    Blazor WebAssembly app (MudBlazor)
tests/
  SmartX.Tests/     xUnit
docs/
```

`SmartX.Api` and `SmartX.Client` both reference `SmartX.Shared`. The client does **not** reference
the API — it calls it over HTTP, which is what keeps the two genuinely separate.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

Check what you have:

```bash
dotnet --version
```

## Restore and build

From the repository root:

```bash
dotnet restore
```

```bash
dotnet build
```

## Running

The API and the client are separate applications. Both need to be running, in two terminals.

**Terminal 1 — the API:**

```bash
dotnet run --project src/SmartX.Api
```

**Terminal 2 — the client:**

```bash
dotnet run --project src/SmartX.Client
```

Then open <http://localhost:5131>. The landing page reports whether it reached the API. If it says
the API is unreachable, the API is not running or is on a different port.

## Ports

| Application | HTTP | HTTPS |
| --- | --- | --- |
| `SmartX.Api` | `5266` | `7059` |
| `SmartX.Client` | `5131` | `7258` |

Two settings have to agree for the connection to work, and both are configuration rather than
compiled-in constants:

- `src/SmartX.Client/wwwroot/appsettings.json` → `ApiBaseAddress` points the client at the API.
- `src/SmartX.Api/appsettings.Development.json` → `Cors:AllowedOrigins` lists the client origins
  the API will accept cross-origin requests from.

Change a port and you must change both.

## Tests

```bash
dotnet test
```

## API

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/api/health` | `{ "status": "ok" }` |
