# Smart-X

[![CI](https://github.com/UsmarTayler/smartx-poe/actions/workflows/ci.yml/badge.svg)](https://github.com/UsmarTayler/smartx-poe/actions/workflows/ci.yml)

An IoT telemetry ingestion and mesh-management system, built as a university Portfolio of Evidence.

Smart-X sits between a field network of sensors and the people who operate it. Sensors are registered
into a hierarchy of facilities, zones and sub-zones, and each registration is checked against the rules
of every level above it. Their readings pass through a staged ingestion pipeline that turns raw JSON into
strongly typed packets without boxing. A trust engine scores every node on recent behaviour and flags the
ones going quiet, and a live dashboard ranks the mesh worst first. Operators can attach configuration
files, installation photos and hardware logs to a sensor. These are encrypted at rest.

The brief has three pillars. Part 1 delivers the first:

1. **Sensor Data Ingestion and Telemetry**: built.
2. **Real-Time Command Stream and History**: later part.
3. **Network Topology and Mesh Routing**: later part.

## Contents

- [Prerequisites](#prerequisites)
- [Architecture](#architecture)
- [Quick start](#quick-start)
- [Running with Docker](#running-with-docker)
- [The demo mesh and scenarios](#the-demo-mesh-and-scenarios)
- [Feature walkthrough](#feature-walkthrough)
- [Where the assessed concepts live](#where-the-assessed-concepts-live)
- [Configuration reference](#configuration-reference)
- [API reference](#api-reference)
- [Attachments: design notes and limits](#attachments-design-notes-and-limits)
- [Troubleshooting](#troubleshooting)

## Prerequisites

| Tool | Version | Needed for | Check it with |
| --- | --- | --- | --- |
| .NET SDK | 10.0.x | Building, testing and running locally | `dotnet --version` |
| Git | any recent | Cloning | `git --version` |
| Docker Desktop, or Docker Engine with Compose v2 | Engine 24+ | The Docker path only | `docker --version` and `docker compose version` |

Download the SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>. `dotnet --version` should
print `10.0.` followed by a patch number, for example `10.0.400`. To see every installed SDK:

```bash
dotnet --list-sdks
```

You don't need Docker to run Smart-X locally, and you don't need the .NET SDK to run it with Docker.

## Architecture

```text
SmartX.sln
src/
  SmartX.Shared/     contracts: packets, zones, sensors, trust and attachment models
  SmartX.Api/        ASP.NET Core Minimal API: ingestion, validation, trust, storage
  SmartX.Client/     Blazor WebAssembly app (MudBlazor), served as static files
tests/
  SmartX.Tests/      xUnit: unit tests and in-memory API tests
docs/
  UPDATES.md         ledger of changes made in response to review
docker-compose.yml   the API and the client together
```

| Project | What it does |
| --- | --- |
| `SmartX.Shared` | Holds every type that crosses the wire: `TelemetryPacket<T>` and its operators, the zone hierarchy and its recursive validator, sensor registrations and their validation rules, trust bands, and attachment metadata. It has no dependencies. |
| `SmartX.Api` | Owns all server-side behaviour. It registers sensors and validates them against their zone ancestry, runs readings through the staging buffer into per-node ring buffers, scores each node's trust, watches for missed heartbeats, and streams encrypted attachments to and from disk. It also hosts the optional demo seeder and simulator. |
| `SmartX.Client` | Runs in the browser. It has four pages (trust panel, sensors, telemetry and a sensor profile), and it talks to the API only over HTTP through one typed client, `SmartXApiClient`. |
| `SmartX.Tests` | Unit tests for the shared types, and API tests that boot the real API in memory with `WebApplicationFactory`. |

**Why the client doesn't reference the API.** Both projects reference `SmartX.Shared`, but neither
references the other. The client can only use the contracts and a base URL, never the API's stores,
engine or internal types, so the split is enforced by the compiler and not just by folder layout. The
client can also be deployed on its own. In Docker it is plain static files behind nginx with no .NET
runtime. The one exception is the test project, which references both so it can check the client's
request behaviour against the real API.

## Quick start

Every command runs from the repository root.

### 1. Get the code

```bash
git clone https://github.com/UsmarTayler/smartx-poe.git
```

```bash
cd smartx-poe
```

### 2. Restore, build and test

```bash
dotnet restore
```

```bash
dotnet build
```

```bash
dotnet test
```

The build should finish with `0 Warning(s)` and `0 Error(s)`, and every test should pass. The suite
takes a few seconds.

### 3. Set the development encryption key (once per machine)

```bash
dotnet user-secrets set "Attachments:EncryptionKey" "jk8ISNL3b2INdPgIONnlaaf0nycILpHni4khrvGdBlg=" --project src/SmartX.Api
```

This stores the key in your user profile, outside the repository. See [the encryption
key](#the-encryption-key) for what this key is, and why it's safe to publish here.

### 4. Run the API (terminal 1)

```bash
dotnet run --project src/SmartX.Api
```

It is ready when it prints `Now listening on: http://localhost:5266`. Check it:

```bash
curl http://localhost:5266/api/health
```

That should return `{"status":"ok"}`.

To start with the 40-sensor [demo mesh](#the-demo-mesh-and-scenarios) instead of empty stores:

```bash
dotnet run --project src/SmartX.Api -- --Simulation:Enabled=true
```

### 5. Run the client (terminal 2)

```bash
dotnet run --project src/SmartX.Client
```

Open <http://localhost:5131>. The status in the app bar shows whether the client reached the API.

### Ports

| Application | HTTP | HTTPS profile |
| --- | --- | --- |
| `SmartX.Api` | <http://localhost:5266> | <https://localhost:7059> |
| `SmartX.Client` | <http://localhost:5131> | <https://localhost:7258> |

The HTTP profile is the default. For HTTPS, add `--launch-profile https` to either command and trust the
development certificate once with `dotnet dev-certs https --trust`.

## Running with Docker

This is an alternative to the quick start. It builds both projects inside containers and runs them
together.

### 1. Create your `.env`

macOS, Linux or Git Bash:

```bash
cp .env.example .env
```

PowerShell:

```powershell
Copy-Item .env.example .env
```

`.env` is gitignored. It holds the encryption key and the settings described
[below](#docker-compose-settings).

### 2. Build and start

```bash
docker compose up --build
```

The first build downloads the .NET SDK and nginx images and takes a few minutes. Later builds reuse the
cached layers. Open <http://localhost:8080>.

To start with the demo mesh, either set `SMARTX_SIMULATION_ENABLED=true` in `.env`, or override it for
one run.

macOS, Linux or Git Bash:

```bash
SMARTX_SIMULATION_ENABLED=true docker compose up --build
```

PowerShell:

```powershell
$env:SMARTX_SIMULATION_ENABLED = "true"; docker compose up --build
```

### 3. Stop

```bash
docker compose down
```

Uploaded attachments are kept in the `smartx_attachments` volume, and survive `down`, restarts and
rebuilds. To delete them as well:

```bash
docker compose down --volumes
```

### How the containers fit together

| Service | Image | Listens on | Host port | Runs as |
| --- | --- | --- | --- | --- |
| `client` | nginx serving the published WebAssembly files | 8080 | `8080` | `nginx` (non-root) |
| `api` | ASP.NET Core runtime, Release build | 8080 | `5266` | `app` (non-root) |

The browser only talks to the client container. nginx serves the app, and forwards any request under
`/api/` to the `api` service on the compose network, so the client's `ApiBaseAddress` is just `/`.
Because the browser sees one origin, no CORS is involved, and the browser never needs to resolve `api`,
a name that only exists inside the compose network. nginx streams uploads straight through without
buffering them or applying its own size limit, and the API enforces the real limit. The API's host port
is published so `curl` and the scenario endpoint can reach it directly.

Both images are multi-stage. The SDK stage restores and publishes in Release, and only the output is
copied into the runtime image, so neither image contains source or a compiler.

## The demo mesh and scenarios

The seeder is **off by default**. A plain `dotnet run` starts with empty stores and the brief's four-zone
hierarchy, which is what manual testing and the automated tests expect. With `Simulation:Enabled=true` the
API does four things before it accepts its first request:

1. **Replaces the zone hierarchy** with 16 zones in two facilities, three levels deep (Facility → Zone →
   Sub-Zone). There are constraints at every level. Temperature, for example, narrows from −40 to 85 °C
   at Facility A, to −30 to 15 °C in the Cold Store, to −30 to −10 °C in the Freezer Bay. Category
   restrictions appear at zone and sub-zone level. The brief's own ids (`facility-a`, `zone-1`,
   `sub-zone-b`, `zone-2`) are kept.
2. **Registers 40 sensors** covering all three categories and all three payload kinds. Every one is run
   through the recursive zone validator first, and the API refuses to start if any fails.
3. **Replays 20 minutes of history** for each sensor through the real ingestion rules, ring buffers and
   trust engine, so every sparkline already has a shape on first load.
4. **Starts a simulator** that keeps readings flowing (about seven a second across the mesh) through
   the same validation path the telemetry endpoint uses.

### Scenarios

| Scenario | Behaviour | Trust signal | Seeded on |
| --- | --- | --- | --- |
| `Nominal` | In-range readings, a little inside the registered heartbeat | Successes | 24 sensors |
| `Drift` | Readings walk toward the top of the range, then hover across it | Range violations, gradually | 4 |
| `Spike` | Mostly normal, with occasional values well outside the range | Range violations, intermittently | 4 |
| `TypeViolation` | Mostly normal, with occasional wrong-typed payloads (`"ERR"`, `21.5` to an integer meter, `1` instead of `true`) | Schema failures | 4 |
| `Disconnect` | Stops reporting entirely | Missed heartbeats | 4 |

Each faulty scenario starts between 4 and 10 minutes before startup, so a sparkline shows a healthy line
that bends where the fault began. On first load the panel shows most nodes Nominal, with several
Degrading and a few in Fault.

`Drift` and `Spike` need a numeric range, so a `State` sensor (a switch or a door contact) can only run
`Nominal`, `TypeViolation` or `Disconnect`.

### Changing a scenario live

```bash
curl -X POST http://localhost:5266/api/simulation/B8:27:EB:10:00:01/scenario -H "Content-Type: application/json" -d "{\"scenario\":\"Disconnect\"}"
```

PowerShell:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5266/api/simulation/B8:27:EB:10:00:01/scenario -ContentType application/json -Body '{"scenario":"Disconnect"}'
```

To see every simulated sensor and its current scenario:

```bash
curl http://localhost:5266/api/simulation
```

A sensor registered by hand can be given a scenario too, and the simulator starts sending its readings.
Both simulation routes return `409` when simulation is off.

**Useful sensors for a demo:**

| MAC | Sensor | Starts as |
| --- | --- | --- |
| `B8:27:EB:10:00:01` | Server room thermometer, 18–32 °C, `Real` | `Nominal`, a good one to fail on camera |
| `B8:27:EB:10:00:06` | CRAC unit, `State` | `Nominal` |
| `B8:27:EB:10:00:13` | Switchgear feeder, 0–200 kW | `Disconnect`, a good one to reconnect with `Nominal` |
| `B8:27:EB:10:00:23` | Office CO₂, 400–2500 ppm, `Integer` | `Nominal` |

## Feature walkthrough

These steps assume the API was started with the demo mesh on. Each one also works on an empty API once
you have registered a sensor or two.

### Register a sensor, with validation

1. Open **Sensors** in the navigation.
2. Fill in **Register a sensor**. Invalid values are flagged as you type, using the same rules the API
   runs (`SensorRegistrationRules` in `SmartX.Shared`). Try a MAC of `AA:BB:CC` or a minimum above the
   maximum.
3. Enter a valid sensor and choose **Register sensor**. It appears in **Registered sensors** straight
   away, and on the trust panel as `Unproven`.
4. Register the same MAC again. The API returns `409`, and the form says the MAC is already registered.

### Zone violations from the recursive validator

1. Register an `Environmental` sensor in °C with an expected range of `-10` to `30`, and choose
   **Sub-Zone B – Server Room** as the zone.
2. The form shows both violations under the zone picker: Zone 1 permits 0–50 °C, and Sub-Zone B itself
   permits 15–35 °C. The validator walked up from the sub-zone and collected every broken rule, not just
   the first.
3. Pick **Zone 2 – Energy Centre** instead. That zone only permits `PowerConsumption` sensors, so the
   category is rejected.

### Ingestion and rejection messages

1. Open **Telemetry**. Each sensor gets a card with an input suited to what it was registered to send: a
   number field for `Integer` and `Real`, and a switch for `State`.
2. Send an in-range value, for example `24` to `B8:27:EB:10:00:01`. It appears under **Recent readings**.
3. Send `60` to the same sensor. It is rejected with the reason, `outside the expected range 18–32 °C`.
4. Send `612.5` to `B8:27:EB:10:00:23`, an `Integer` sensor. It is rejected because readings must be
   whole numbers, and counts as a schema failure against that node's trust.

### The trust panel reordering live

1. Open **Trust panel**, the home page. Nodes are listed worst first by band, then by score, each with a
   reason and a sparkline. The tile at the top shows mesh confidence and the count per band.
2. [Switch](#changing-a-scenario-live) `B8:27:EB:10:00:01` to `Disconnect`. Within seconds its reason
   reads `no packets for …` and it starts climbing the list. It turns Degrading after about 1½ minutes and
   Fault after about 3½, because twenty minutes of good history takes a while to outweigh.
3. Switch it back to `Nominal`. It reports again straight away and sinks back down as its score recovers.
4. Use the band chips and the text filter to narrow the list.

### Attachments: upload, progress, thumbnail, download, delete

1. Select any MAC on the sensors table or a telemetry card to open its **Sensor profile**.
2. Drop files onto **Attachments**, or choose **Browse**. Several can go at once, and they upload two at a
   time with real progress bars. Try a large log file to watch the progress move.
3. An image shows a thumbnail. Select it to open a full-size preview, and press Escape to close it.
4. Choose **Download** on any attachment. The file is decrypted as it streams out, and is byte-identical
   to what was uploaded.
5. Choose delete, confirm, and the attachment and its encrypted file are removed.
6. Try something that isn't allowed. A `.exe` is refused before upload. A PNG renamed to `.txt` is sent,
   and the API rejects it with `415` after inspecting its bytes.

Restart the API, or the Docker stack, and the attachments are still there.

## Where the assessed concepts live

| Concept | Where | What to look at |
| --- | --- | --- |
| Generics without boxing | [`TelemetryPacket.cs`](src/SmartX.Shared/Packets/TelemetryPacket.cs) | `TelemetryPacket<T> where T : struct`; the class comment explains how the constraint avoids boxing |
| Generic math | [`NumericPacket.cs`](src/SmartX.Shared/Packets/NumericPacket.cs) | `NumericPacket<T> where T : struct, INumber<T>` |
| Generic conversion at the boundary | [`ValueRange.cs`](src/SmartX.Shared/Sensors/ValueRange.cs) | `Contains<T>(T value)` uses `double.CreateChecked`, not `Convert.ToDouble(object)` |
| Generic results on the client | [`ApiResult.cs`](src/SmartX.Client/Services/ApiResult.cs), [`SmartXApiClient.cs`](src/SmartX.Client/Services/SmartXApiClient.cs) | `ApiResult<T>` as a `readonly record struct`, and `SendReadingAsync<T>` |
| Operator overloading, arithmetic and comparison | [`NumericPacket.cs`](src/SmartX.Shared/Packets/NumericPacket.cs) | `+`, `-`, `>`, `<`, `==`, `!=` |
| Operator overloading, logical | [`StatePacket.cs`](src/SmartX.Shared/Packets/StatePacket.cs) | `\|`, `&`, `==`, `!=` |
| Operator guard | [`TelemetryPacket.cs`](src/SmartX.Shared/Packets/TelemetryPacket.cs) | `EnsureCombinable`, which rejects mismatched units or categories |
| Recursion, walking up | [`ZoneValidator.cs`](src/SmartX.Shared/Zones/ZoneValidator.cs) | `Ascend`, with its base case, cycle guard and depth guard |
| Recursion, walking down | [`ZoneTree.cs`](src/SmartX.Shared/Zones/ZoneTree.cs) | `Descend` (sensors beneath a zone) and `BuildNode` (the nested tree) |
| Jagged arrays | [`TelemetryStagingBuffer.cs`](src/SmartX.Shared/Collections/TelemetryStagingBuffer.cs) | the `T[][] _windows` field, `Stage` and `Drain`; the class comment covers jagged versus rectangular |
| Custom collection | [`TelemetryRingBuffer.cs`](src/SmartX.Shared/Collections/TelemetryRingBuffer.cs) | `TelemetryRingBuffer<T>`: O(1) `Add` that overwrites the oldest, an indexer, and a struct `Enumerator` so `foreach` allocates nothing |
| Lists | [`TelemetryStagingBuffer.cs`](src/SmartX.Shared/Collections/TelemetryStagingBuffer.cs), [`TelemetryRingBuffer.cs`](src/SmartX.Shared/Collections/TelemetryRingBuffer.cs) | `Drain` returns a pre-sized `List<T>`, and `ToList` copies a node's history out as a pre-sized list |
| Dictionary and HashSet | [`TrustEngine.cs`](src/SmartX.Api/Trust/TrustEngine.cs) | `_nodes`, keyed by MAC for O(1) lookup per packet, and `_degraded`, where each failing node appears once |
| The ingestion pipeline, stage by stage | [`TelemetryEndpoints.cs`](src/SmartX.Api/Endpoints/TelemetryEndpoints.cs) | `IngestBatchAsync`, with each stage numbered in comments, and `TryCreatePacket` |
| Polymorphic JSON | [`ITelemetryPacket.cs`](src/SmartX.Shared/Packets/ITelemetryPacket.cs) | `[JsonDerivedType]` over the packet types |
| Trust formula | [`TrustFormula.cs`](src/SmartX.Shared/Trust/TrustFormula.cs) | `Score` (Laplace smoothing), `DecayWeight` (half-life), `Classify` |
| Background services | [`HeartbeatMonitor.cs`](src/SmartX.Api/Trust/HeartbeatMonitor.cs), [`MeshSimulator.cs`](src/SmartX.Api/Simulation/MeshSimulator.cs) | `PeriodicTimer` driven by an injected `TimeProvider` |
| Async all the way down | [`SmartXApiClient.cs`](src/SmartX.Client/Services/SmartXApiClient.cs), [`ISensorRegistry.cs`](src/SmartX.Api/Storage/ISensorRegistry.cs) | no `.Result`, `.Wait()` or `GetAwaiter().GetResult()` anywhere |
| Streamed, encrypted I/O | [`AttachmentUploader.cs`](src/SmartX.Api/Attachments/AttachmentUploader.cs), [`AesGcmEncryptingStream.cs`](src/SmartX.Api/Attachments/AesGcmEncryptingStream.cs) | `MultipartReader`, not `IFormFile`, and chunked AES-256-GCM |

## Configuration reference

The API reads configuration in the standard ASP.NET Core order: `appsettings.json`, then
`appsettings.{Environment}.json`, then user secrets (Development only), then environment variables, then
command-line arguments. Later sources win. An environment variable uses `__` for each `:` in a setting
name, so `Trust:HalfLife` becomes `Trust__HalfLife`. The `Attachments`, `Simulation`, `Telemetry` and
`Trust` sections are validated at startup, and a bad value stops the API with a message naming the
setting.

### API: `Attachments`

| Setting | Default | What it does |
| --- | --- | --- |
| `Attachments:StoragePath` | `App_Data/attachments` (`/data/attachments` in Docker) | Where encrypted files and their index entries are written. A relative path is resolved against the API's content root. |
| `Attachments:MaxFileBytes` | `104857600` (100 MiB) | The largest file accepted. It is enforced byte by byte while the upload streams, and published to the client at `/api/attachments/limits`. |
| `Attachments:EncryptionKey` | *(none)* | Base64-encoded 256-bit AES key. See [the encryption key](#the-encryption-key). |

### API: `Simulation`

| Setting | Default | What it does |
| --- | --- | --- |
| `Simulation:Enabled` | `false` | Seeds the demo mesh on startup and runs the simulator. |
| `Simulation:BackfillDuration` | `00:20:00` | How much history is replayed for each seeded sensor. |
| `Simulation:TickInterval` | `00:00:00.5` | How often the simulator checks which sensors are due to report. It must be shorter than a fifth of the shortest heartbeat, which is five seconds in the demo mesh. |
| `Simulation:RandomSeed` | `1729` | Seeds the generated values, so every run produces the same readings. |

### API: `Telemetry`

| Setting | Default | What it does |
| --- | --- | --- |
| `Telemetry:RetentionPerNode` | `256` | Readings kept per sensor in its ring buffer before the oldest is overwritten. |

### API: `Trust`

| Setting | Default | What it does |
| --- | --- | --- |
| `Trust:SuccessWeight` | `1` | Added to successes for each valid, in-range reading. |
| `Trust:RangeViolationWeight` | `1` | Added to failures for a reading outside its range. |
| `Trust:SchemaFailureWeight` | `2` | Added to failures for a wrong-typed reading. |
| `Trust:MissedHeartbeatWeight` | `3` | Added to failures for each heartbeat interval that passes in silence. |
| `Trust:HalfLife` | `00:10:00` | The age at which an outcome counts half as much as a fresh one. |
| `Trust:OutcomeWindow` | `256` | Recent outcomes retained per node for scoring. |
| `Trust:HistoryLength` | `120` | Trust samples retained per node for its full history. |
| `Trust:HistorySampleInterval` | `00:00:10` | Minimum spacing between history samples. A band change is always sampled. |
| `Trust:SnapshotHistoryPoints` | `30` | Recent samples included with each node in `/api/trust`, for its sparkline. |
| `Trust:NominalThreshold` | `0.75` | The lowest score that counts as Nominal. |
| `Trust:FaultThreshold` | `0.5` | Scores below this are Fault. |
| `Trust:DefaultHeartbeatInterval` | `00:01:00` | The heartbeat assumed for a sensor registered without one. |
| `Trust:HeartbeatCheckInterval` | `00:00:05` | How often the heartbeat monitor looks for silent nodes. |

### API: `Cors`

| Setting | Default | What it does |
| --- | --- | --- |
| `Cors:AllowedOrigins` | `http://localhost:5131` and `https://localhost:7258`, set in `appsettings.Development.json` only | The client origins allowed to call the API from a browser. It isn't needed in Docker, where nginx puts the client and the API on one origin. |

### Client: `src/SmartX.Client/wwwroot/appsettings.json`

| Setting | Default | What it does |
| --- | --- | --- |
| `ApiBaseAddress` | `http://localhost:5266/` | Where the client sends API requests. It can be absolute, or relative to the page (Docker uses `/`). |
| `TrustFeed:PollIntervalSeconds` | `3` | How often the trust panel refreshes. Each refresh is two requests, however many nodes there are. |

In Docker these two come from the client container's `API_BASE_ADDRESS` and
`TRUST_POLL_INTERVAL_SECONDS` environment variables. They are written into `appsettings.json` when the
container starts, so the image never needs rebuilding to point somewhere else.

### Docker Compose settings

These go in `.env`:

| Variable | Default in `.env.example` | What it does |
| --- | --- | --- |
| `SMARTX_ATTACHMENT_KEY` | the development key | Becomes `Attachments__EncryptionKey` for the API. Compose refuses to start without it. |
| `SMARTX_SIMULATION_ENABLED` | `false` | Becomes `Simulation__Enabled`. |
| `SMARTX_CLIENT_PORT` | `8080` | Host port for the client. Open this one in the browser. |
| `SMARTX_API_PORT` | `5266` | Host port for the API, for `curl` and the scenario endpoint. |

### The encryption key

Attachments are encrypted at rest with AES-256-GCM, using a 256-bit key supplied as base64.

**The key is never in source code, or in any settings file the API loads.** It reaches the API in one of
three ways:

| Where the API runs | How the key is supplied |
| --- | --- |
| Locally, `dotnet run` | User secrets, which live in your user profile outside the repository (`dotnet user-secrets set …`, see [step 3](#3-set-the-development-encryption-key-once-per-machine)) |
| Docker Compose | `SMARTX_ATTACHMENT_KEY` in your gitignored `.env` |
| Anywhere else | The environment variable `Attachments__EncryptionKey`, set by the host or a secret store |

**The development key.** The key in this README and in `.env.example` is
`jk8ISNL3b2INdPgIONnlaaf0nycILpHni4khrvGdBlg=`. It is fixed so that attachments uploaded while developing
or recording a demo are still readable after a restart. It is **published, and so it protects nothing**.
Anyone with this repository can decrypt what it encrypts. The API recognises it by fingerprint and logs a
warning whenever it runs outside the Development environment with it.

To use a private key instead, generate one.

macOS, Linux or Git Bash:

```bash
openssl rand -base64 32
```

PowerShell:

```powershell
$key = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($key); [Convert]::ToBase64String($key)
```

**If no key is set:**

- In the Development environment, the API generates a temporary key for that run and logs a warning.
  Everything works until you restart, after which those attachments can't be read.
- In any other environment, the API refuses to start and names the missing setting.

**If the key changes,** attachments encrypted under the old key are hidden from lists, not shown as
downloads that would fail. They are not deleted, so restoring the old key brings them back.

#### What this does and does not demonstrate

This demonstrates the *encryption mechanism*, not production key management.

- **Demonstrated:**
  - streamed authenticated encryption with a random nonce per file
  - tamper detection on every chunk, before any plaintext is released
  - no plaintext written to disk at any point
  - a key supplied through configuration, never code
  - a fingerprint of the key recorded with each file, so a key change is detected rather than failing at
    download
- **Not demonstrated:**
  - A real deployment would fetch the key from a managed key vault (Azure Key Vault, AWS KMS, HashiCorp
    Vault), keep it out of the process environment, and audit every access.
  - Keys would be rotated, re-encrypting or re-wrapping existing files. The key fingerprint shows which
    files a rotation would have to touch, but no rotation is implemented.
  - Envelope encryption, where each file gets its own data key, itself encrypted by a master key, is the
    usual approach, and is not implemented here.

## API reference

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/api/health` | `{ "status": "ok" }` |
| `GET` | `/api/sensors` | Every registered sensor |
| `POST` | `/api/sensors` | Register a sensor: `201`, `400` (invalid, or a zone violation), `409` (MAC taken) |
| `GET` | `/api/sensors/{mac}` | One sensor, or `404` |
| `POST` | `/api/telemetry` | One reading: `202`, `400` (rejected, with the reason), `404` |
| `POST` | `/api/telemetry/batch` | A jagged batch of readings: `202`, with per-reading errors |
| `GET` | `/api/sensors/{mac}/telemetry` | Retained readings, oldest first |
| `GET` | `/api/zones` | The zone hierarchy, nested |
| `GET` | `/api/trust` | Every node worst first, each with recent history |
| `GET` | `/api/trust/summary` | Mesh confidence and per-band counts |
| `GET` | `/api/trust/{mac}` | One node's trust |
| `GET` | `/api/trust/{mac}/history` | One node's full trust series |
| `POST` | `/api/sensors/{mac}/attachments` | Streamed multipart upload of one file: `201`, `400`, `404`, `413`, `415` |
| `GET` | `/api/sensors/{mac}/attachments` | A sensor's attachments, newest first |
| `GET` | `/api/attachments/limits` | Maximum size and permitted extensions |
| `GET` | `/api/attachments/{id}` | Download, decrypted on the way out |
| `GET` | `/api/attachments/{id}/thumbnail` | An image attachment for inline display |
| `DELETE` | `/api/attachments/{id}` | `204`, or `404` |
| `GET` | `/api/simulation` | Every simulated sensor and its scenario, or `409` when simulation is off |
| `POST` | `/api/simulation/{mac}/scenario` | `{ "scenario": "Drift" }`: `200`, `400` (not possible for that sensor), `404`, `409` |

Sensors, readings and trust are held in memory. Restarting the API clears them, apart from what the
seeder registers again when simulation is on. Attachments are the exception, and persist on disk.

## Attachments: design notes and limits

- **Streamed, not buffered.** Uploads are read with `MultipartReader` straight off the request body.
  `IFormFile` binding would buffer the whole file into memory or a temporary file before the handler ran.
  A 1 GiB log round-trips while the API's private memory rises by about 6 MB.
- **An oversized upload closes the connection.** Once the limit is passed, the server stops reading and
  answers `413`. A client still sending may see the connection close instead of the `413`, because
  draining an arbitrarily large body just to reply politely would defeat the limit. The web client checks
  size before uploading, so this only affects clients that skip that check.
- **Content is inspected, not trusted.** The file's first bytes decide whether it is a PNG, JPEG or WebP.
  Text must be valid UTF-8 without control characters all the way through, not just at the start. The
  extension and declared type must agree with the bytes.
- **Thumbnails are the original image.** A resized thumbnail would need an image library and a full decode
  of the picture into memory, which is exactly the cost the streaming design avoids. The client scales the
  image down for display.
- **Text is UTF-8 only.** A UTF-16 log contains NUL bytes and is rejected as binary.
- **Storage is a directory.** Each attachment is a `.smxa` ciphertext file plus a small `.json` index
  entry with the same GUID name. The index holds the original name, size, SHA-256 and key fingerprint, and
  is loaded into memory at startup. Locally the directory is `src/SmartX.Api/App_Data/attachments`, which
  is gitignored. In Docker it is the `smartx_attachments` volume.

## Troubleshooting

### The address is already in use

`dotnet run` fails with `Failed to bind to address http://127.0.0.1:5266: address already in use`, or
Docker reports `port is already allocated`.

Find what is holding the port.

PowerShell:

```powershell
Get-NetTCPConnection -LocalPort 5266 | Select-Object OwningProcess
```

macOS or Linux:

```bash
lsof -i :5266
```

Usually it's an earlier run of the same app, or the Docker stack while you run locally (both use
`5266`). Stop that first. To move a port instead:

- **Locally:** change `applicationUrl` in the project's `Properties/launchSettings.json`, then update the
  other half to match. `ApiBaseAddress` in the client's `wwwroot/appsettings.json` must point at the
  API's port, and `Cors:AllowedOrigins` in the API's `appsettings.Development.json` must list the client's
  origin.
- **Docker:** change `SMARTX_CLIENT_PORT` or `SMARTX_API_PORT` in `.env`.

### The build fails with "file is locked by SmartX.Api"

A running API or client holds its own executable. Stop it (Ctrl+C in its terminal, or stop debugging in
Visual Studio) and build again.

### The client build fails with MSB3030 "Could not copy the file … webcil"

This happens on Windows when the repository is cloned somewhere with a long path. The Blazor build writes
files several folders deep under `src/SmartX.Client/obj`, and once the full path passes Windows'
260-character limit the copy fails. Either clone to a shorter path such as `C:\src\smartx-poe`, or
enable long paths once from an administrator PowerShell and restart the terminal:

```powershell
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem" -Name LongPathsEnabled -Value 1 -PropertyType DWORD -Force
```

### The browser console shows a CORS error

The error reads `blocked by CORS policy: No 'Access-Control-Allow-Origin' header`. The client is being
served from an origin the API doesn't allow.

- Check which address is in the browser's address bar. If you used the HTTPS profile, or changed a port,
  that origin has to be in `Cors:AllowedOrigins` in `src/SmartX.Api/appsettings.Development.json`.
- The CORS list is only in the Development settings. If the API was started with
  `ASPNETCORE_ENVIRONMENT=Production` while the client runs on its own port, pass the origin explicitly:
  `dotnet run --project src/SmartX.Api -- --Cors:AllowedOrigins:0=http://localhost:5131`.
- In Docker there is no cross-origin request. If you see this error there, the client's
  `API_BASE_ADDRESS` has been set to an absolute URL. Leave it as `/`.

### "The API could not be reached"

The client loaded, but nothing answered at `ApiBaseAddress`.

1. Check the API is running. `curl http://localhost:5266/api/health` should return `{"status":"ok"}`.
2. Check the address in `src/SmartX.Client/wwwroot/appsettings.json` matches the port the API printed.
3. In Docker, check both containers are up with `docker compose ps`, and read the API's log with
   `docker compose logs api`.

### The API won't start: missing or invalid encryption key

The message is `Attachments:EncryptionKey is required outside Development` or `must be a base64-encoded
256-bit (32-byte) key`.

- **Locally:** set the key with the user-secrets command in [step
  3](#3-set-the-development-encryption-key-once-per-machine). User secrets are only read in the
  Development environment, which is what `dotnet run` uses by default.
- **Docker:** compose stops with `Set SMARTX_ATTACHMENT_KEY - copy .env.example to .env`. Create `.env`
  as in [step 1](#1-create-your-env). A key must decode to exactly 32 bytes, which is 44 base64
  characters ending in `=`.

### Attachments disappeared after a restart

- The API log says `No Attachments:EncryptionKey is configured`: the run used a temporary key. Set the
  development key, and upload again.
- The API log says `stored attachments were encrypted with a different key and are hidden`: the key has
  changed since they were uploaded. Set the previous key again and they reappear.
- **Docker:** check the volume still exists with `docker volume ls`. `docker compose down --volumes`
  deletes it.

### The sensor page says "Sensor not found" after a restart

Sensors are held in memory, so a restarted API has forgotten any registered by hand. Register them again,
or run with the demo mesh, which is seeded on every start.

### The Docker client container keeps restarting

Run `docker compose logs client`. If you see `not found` or `bad interpreter` for
`40-smartx-client-config.sh`, the script was checked out with Windows line endings. The repository's
`.gitattributes` prevents that on a fresh clone. For an existing checkout, run `git add --renormalize .`,
then check the script back out and rebuild with `docker compose up --build`.

## Tests

```bash
dotnet test
```

The suite covers packets and operators, zone validation and tree walks, the staging buffer and ring
buffer, the ingestion endpoints, the trust engine and heartbeat monitor, and streamed encrypted
attachments, including tampering, type spoofing, path traversal and restarts. It also covers the demo
seeder and scenarios. The API tests boot the real API in memory, each with its own stores and its own
temporary storage directory.
