# Smart-X

[![CI](https://github.com/UsmarTayler/smartx-poe/actions/workflows/ci.yml/badge.svg)](https://github.com/UsmarTayler/smartx-poe/actions/workflows/ci.yml)

An IoT telemetry ingestion and mesh-management system, built as a university Portfolio of Evidence.

Smart-X sits between a field network of sensors and the people who operate it. Sensors are registered
into a hierarchy of facilities, zones and sub-zones, and each registration is checked against the rules
of every level above it. Their readings pass through a staged ingestion pipeline that turns raw JSON into
strongly typed packets without boxing. A trust engine scores every node on recent behaviour and flags the
ones going quiet, and a live dashboard ranks the mesh worst first. Operators can attach configuration
files, installation photos and hardware logs to a sensor. These are encrypted at rest.

The brief has three pillars. Parts 1 and 2 deliver the first and lay the ground for the second:

1. **Sensor Data Ingestion and Telemetry**: built.
2. **Real-Time Command Stream and History**: in progress. Part 2 adds a separate device simulator that
   reports over HTTP, and two ingestion queues, a FIFO lane and a priority lane for critical alerts.
3. **Network Topology and Mesh Routing**: later part.

## Contents

- [Prerequisites](#prerequisites)
- [Architecture](#architecture)
- [Quick start](#quick-start)
- [Running with Docker](#running-with-docker)
- [The device simulator](#the-device-simulator)
- [Ingestion queues](#ingestion-queues)
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
  SmartX.DeviceSimulator/  a fleet of simulated ESP32 devices that report to the API over HTTP
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
| `SmartX.DeviceSimulator` | A console app that behaves like a fleet of ESP32s. It registers its devices with `POST /api/sensors`, posts telemetry at each device's interval, backfills history on first start, waits for the API with backoff if it isn't up yet, and polls the API for the scenario an operator wants each device to run. It references `SmartX.Shared` only. |
| `SmartX.Tests` | Unit tests for the shared types, and API tests that boot the real API in memory with `WebApplicationFactory`. |

**Why the client and the simulator don't reference the API.** Each of them references `SmartX.Shared`, but
none of them references the API, and the API references none of them. The client can only use the contracts and a base URL, never the API's stores,
engine or internal types, so the split is enforced by the compiler and not just by folder layout. The
client can also be deployed on its own. In Docker it is plain static files behind nginx with no .NET
runtime. The simulator follows the same rule for a plainer reason: it stands in for hardware, and hardware
cannot call into the gateway's code, only send it HTTP requests. The one exception is the test project,
which references all of them so it can check their behaviour against the real API.

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

The API starts empty, with the brief's four-zone hierarchy. To fill it with the larger demo deployment
and 40 simulated devices, start it with the demo zones, then run the [device simulator](#the-device-simulator)
in a third terminal (step 6):

```bash
dotnet run --project src/SmartX.Api -- --Zones:Hierarchy=Demo
```

### 5. Run the client (terminal 2)

```bash
dotnet run --project src/SmartX.Client
```

Open <http://localhost:5131>. The status in the app bar shows whether the client reached the API.

### 6. Run the device simulator (terminal 3, optional)

The API must be running with `--Zones:Hierarchy=Demo`, as in step 4.

```bash
dotnet run --project src/SmartX.DeviceSimulator
```

It registers 40 devices, posts about ten minutes of history so the dashboard is not empty, and keeps them
reporting. Start it before the API if you like: it waits and retries until the API answers.

### Ports

| Application | HTTP | HTTPS profile |
| --- | --- | --- |
| `SmartX.Api` | <http://localhost:5266> | <https://localhost:7059> |
| `SmartX.Client` | <http://localhost:5131> | <https://localhost:7258> |

The HTTP profile is the default. For HTTPS, add `--launch-profile https` to either command and trust the
development certificate once with `dotnet dev-certs https --trust`.

## Running with Docker

This is an alternative to the quick start. It builds the projects inside containers and runs all three
services together: the API, the client and the device simulator.

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

Three containers start, in order: the API first, then the simulator once the API reports healthy. The
dashboard fills within about half a minute. To run fewer devices, or to start every device healthy, change
`SMARTX_DEVICE_COUNT` or `SMARTX_SCENARIO_MIX` in `.env` (see [Docker Compose
settings](#docker-compose-settings)).

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
| `simulator` | .NET runtime, Release build | none | none | `app` (non-root) |

The simulator talks to `http://api:8080` over the compose network, exactly as a device would talk to a
gateway. It publishes no port, because it only makes requests.

Compose starts the simulator only once the API's health check passes (`condition: service_healthy`). That
narrows the start-up race but doesn't remove it, so the simulator also waits and retries with backoff on its
own. The API's health check is a request to `/api/health` made from inside the container with bash, because
the runtime image has no `curl`.

The browser only talks to the client container. nginx serves the app, and forwards any request under
`/api/` to the `api` service on the compose network, so the client's `ApiBaseAddress` is just `/`.
Because the browser sees one origin, no CORS is involved, and the browser never needs to resolve `api`,
a name that only exists inside the compose network. nginx streams uploads straight through without
buffering them or applying its own size limit, and the API enforces the real limit. The API's host port
is published so `curl` and the scenario endpoint can reach it directly.

Both images are multi-stage. The SDK stage restores and publishes in Release, and only the output is
copied into the runtime image, so neither image contains source or a compiler.

## The device simulator

`SmartX.DeviceSimulator` behaves like a fleet of ESP32s, and everything it does crosses the network. It
replaced Part 1's seeder, which ran inside the API and called its services directly. That was convenient,
but it proved nothing about the API under real traffic and it wasn't what the brief asks for: simulated data
has to hit the API like a device would.

**What it does**

1. **Waits for the API**, retrying with backoff from a quarter of a second up to five seconds. Compose
   starts containers in parallel, and real devices power on before their gateway does, so a simulator that
   crashed because it won the race would be wrong.
2. **Registers its devices** with `POST /api/sensors`. A `409` means the device is already registered and
   counts as success, so restarting the simulator is safe. Any other refusal stops it, loudly, because a fleet
   that half-registered isn't the fleet it claims to be.
3. **Backfills history** by posting timestamped readings, so the dashboard isn't empty. Each device reports
   normally until its scenario began a few minutes ago and follows the scenario after that, which is what
   gives each sparkline a bend.
4. **Reports** at each device's registered interval, a little inside it (at 80%) so a late tick can't be
   what makes a healthy device miss its heartbeat, and with jitter so forty devices don't all report in the
   same millisecond.
5. **Polls for its desired state** every five seconds, and applies any change.
6. **Re-registers if the API forgets it.** The API keeps its device list in memory, so restarting it while the
   simulator runs empties the list. A device that gets a `404` on a report registers itself again and resends,
   so the fleet recovers within a few seconds without being restarted.

The 40 devices and their zones live in the demo deployment: two facilities, 16 zones, three levels deep
(Facility → Zone → Sub-Zone), with constraints at every level. The zones stay in the API, selected with
`Zones:Hierarchy=Demo`, because a deployment hierarchy is gateway configuration and not something a device
carries. The devices come only from the simulator.

### Scenarios

| Scenario | Behaviour | What it exercises | Demo fleet |
| --- | --- | --- | --- |
| `Nominal` | In-range readings at the registered interval | Successes | 22 devices |
| `Drift` | Readings walk toward the top of the range, then hover across it | Range violations, gradually | 4 |
| `Spike` | Mostly normal, with occasional values well outside the range | Range violations, intermittently | 4 |
| `TypeViolation` | Mostly normal, with occasional wrong-typed payloads (`"ERR"`, `21.5` to an integer meter, `1` instead of `true`) | Schema failures | 4 |
| `Critical` | Occasional extreme but valid readings: a power spike or a moisture crash | The priority queue | 2 |
| `Disconnect` | Stops reporting entirely | Missed heartbeats | 4 |

`Drift`, `Spike` and `Critical` need a numeric range, so a `State` device (a switch or a door contact) can
only run `Nominal`, `TypeViolation` or `Disconnect`.

`Critical` differs from the others in one respect worth noticing. The device does not label its reading as an
alert. It sends a real value near the edge of its range, which passes validation, and the API decides from
its configured thresholds that the value is an emergency. A reading *outside* the registered range is a
different thing entirely: it is invalid and is rejected.

### Changing a scenario live: the device shadow

The simulator is a separate program, so the API can't reach into it to change a scenario, and shouldn't try:
that would mean the gateway holding a connection open to every device. Real IoT platforms solve this with a
**device shadow** (AWS IoT) or **device twin** (Azure): the platform stores the state an operator *wants*,
each device polls for its own and applies it, and the device is the only thing that ever changes the device.

`POST /api/simulation/{mac}/scenario` writes the desired state. It answers `202 Accepted`, not `200`,
because the change hasn't happened yet: the device applies it on its next poll, within about five seconds.

macOS, Linux or Git Bash:

```bash
curl -X POST http://localhost:5266/api/simulation/B8:27:EB:10:00:01/scenario -H "Content-Type: application/json" -d "{\"scenario\":\"Critical\"}"
```

PowerShell:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5266/api/simulation/B8:27:EB:10:00:01/scenario -ContentType application/json -Body '{"scenario":"Critical"}'
```

The response is the desired state, with a version that increases on every change. A device that finds a
version it has already applied does nothing. To see what has been asked of every device:

```bash
curl http://localhost:5266/api/simulation
```

The device's own endpoint is `GET /api/devices/{mac}/desired`, which answers `404` when nothing has been
asked of it. A device reads that as "carry on as you are", which keeps the common case a single cheap request.
Part 3's command acknowledgement uses the same polling channel.

**Useful devices for a demo:**

| MAC | Device | Starts as |
| --- | --- | --- |
| `B8:27:EB:10:00:01` | Server room thermometer, 18–32 °C, `Real` | `Nominal`, a good one to fail on camera |
| `B8:27:EB:10:00:04` | Server room humidity, 30–70 %RH | `Critical`, a moisture crash |
| `B8:27:EB:10:00:0C` | Compressor, 0–7500 W, `Integer` | `Critical`, a power spike |
| `B8:27:EB:10:00:06` | CRAC unit, `State` | `Nominal` |
| `B8:27:EB:10:00:13` | Switchgear feeder, 0–200 kW | `Disconnect`, a good one to reconnect with `Nominal` |

## Ingestion queues

A valid reading is validated while the caller waits and processed after the caller has gone.

```text
device ──HTTP──▶ EDGE (synchronous, on the request thread)
                 parse · is the MAC registered? · right type? · in range?
                    │                                       │
                    │ rejected                              │ valid
                    ▼                                       ▼
           400 / 404 with the reason              classify: critical?
           and scored against trust                 │              │
                                                    │ yes          │ no
                                                    ▼              ▼
                                            PriorityQueue     Queue<T>
                                         (severity, sequence)   (FIFO)
                                                    │              │
                                                    └──────┬───────┘
                                                           ▼
                                        WORKER (BackgroundService, asynchronous)
                                        critical lane first · then storage,
                                        trust scoring and the ring buffer

           ◀── 202 Accepted + receipt (lane, position) ─── returned at enqueue, not at storage
           ◀── 503 + Retry-After ─────────────────────────── when the lane is full
```

**Why validation stays synchronous.** Rejection is cheap, and the caller needs the answer now. The
telemetry page still says `21.5 rejected: sensor is registered as Integer…` the moment you send it, with the
same status code and the same text as in Part 1. Processing is the expensive part, and it belongs behind the
queue. A rejected reading never enters a queue, and still counts against the node's trust.

**The standard lane is `Queue<T>`.** Guarded by a lock, because `Queue<T>` isn't thread-safe and requests
arrive on many threads, and signalled to the worker with a `SemaphoreSlim`, because a lock can't make a
worker *wait* for an item. `System.Threading.Channels.Channel<T>` is what production .NET would use: it is
bounded, thread-safe and async-aware out of the box. `Queue<T>` is used here because the brief asks for it and
because it keeps the FIFO mechanics visible. The cost is that the locking is this project's own code to get
right, which is what the tests around it are for.

**It is bounded.** When a lane is full, the API answers `503 Service Unavailable` with a `Retry-After`
header instead of silently dropping the reading or growing until it runs out of memory. That is backpressure,
and it is what a real gateway does. The simulator honours it: it waits, tries once more, and if the gateway is
still busy drops that reading, which is what a device with no storage of its own would do.

**Critical readings use a `PriorityQueue`.** A reading is critical if it crosses a configured threshold for its
category and unit, decided at the edge, so the alert never waits behind the backlog it is meant to bypass. The
worker always drains the priority lane first. Defaults (all configurable):

| Category | Unit | Critical when | Reason |
| --- | --- | --- | --- |
| `PowerConsumption` | W | above 5000 | power spike |
| `PowerConsumption` | kW | above 180 | power spike |
| `Environmental` | %RH | below 35 | moisture crash |
| `Environmental` | °C | above 40 or below −27 | temperature excursion |
| `Environmental` | ppm | above 2200 | air quality |

**Two subtleties, both handled.**

1. **`PriorityQueue` isn't stable.** It's a binary heap, and .NET doesn't promise to keep elements of equal
   priority in insertion order. Two alerts of the same severity could be processed out of the order they
   arrived. The fix is a composite priority, `(severity, sequenceNumber)`, where the sequence number is a
   counter that only increases. Every priority is then unique, and arrival order survives. A test asserts it,
   and a second test shows that without the sequence number the framework's heap really does reorder equal
   priorities.
2. **Starvation.** If critical readings never stop arriving, a strict priority lane would leave the standard
   lane untouched until it filled and started refusing readings, and ordinary telemetry would stop at
   exactly the moment the mesh was busiest. After `Ingestion:MaxConsecutiveCritical` alerts in a row
   (default 20) the worker takes one normal reading, if one is waiting. Twenty keeps alerts effectively
   immediate, because the detour costs one normal reading's processing time, while guaranteeing the
   standard lane always moves. Pure priority would also be defensible, but only if argued; not thinking about it
   would not be.

### Measured: critical versus normal under load

Rather than claim that critical alerts are faster, `CriticalBypassMeasurementTests` measures it. It runs the real
worker against a store made deliberately slow (0.3 ms per reading, a wall-clock busy-wait so the cost is the same
on any machine), queues 5,000 ordinary readings behind it, and then sends one critical and one normal reading
at the same instant.

| | Wait before being processed |
| --- | --- |
| Critical reading | **0.4 ms** |
| Normal reading, behind a 5,001-deep backlog | **1,691 ms** |
| Difference | the critical reading was processed **about 4,500 times sooner** |

The alert was processed while almost all of the backlog was still waiting. A second test covers the other
side of the trade-off: with 2,000 critical readings arriving over 200 queued normal ones and the valve set to
10, the worker had processed 102 normal readings by the time it had processed 1,028 critical ones, so
ordinary telemetry keeps moving. The exact figures vary a little from run to run. Run them yourself:

```bash
dotnet test --filter "FullyQualifiedName~CriticalBypassMeasurementTests" --logger "console;verbosity=detailed"
```

### Watching the pipeline

`GET /api/pipeline/stats` reports the depth of each lane, readings processed per lane, the average and
maximum wait per lane, and how many readings were refused with `503`. Critical is listed first.

```bash
curl http://localhost:5266/api/pipeline/stats
```

## Feature walkthrough

These steps assume the API and the device simulator are both running, so there is a fleet to look at. Each one also works on an empty API once
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
| FIFO queue | [`TelemetryIngestionQueue.cs`](src/SmartX.Api/Ingestion/TelemetryIngestionQueue.cs) | `Queue<QueuedReading> _standard`, with the lock, the `SemaphoreSlim` signal and the bound; the class comment explains the trade-off against `Channel<T>` |
| Priority queue | [`TelemetryIngestionQueue.cs`](src/SmartX.Api/Ingestion/TelemetryIngestionQueue.cs) | `PriorityQueue<QueuedReading, (int Severity, long Sequence)>`: the composite priority that makes it stable, and `DequeueAsync`'s starvation valve |
| Classifying at the edge | [`CriticalClassifier.cs`](src/SmartX.Api/Ingestion/CriticalClassifier.cs), [`TelemetryEndpoints.cs`](src/SmartX.Api/Endpoints/TelemetryEndpoints.cs) | `IsCritical` against configured rules; `Enqueue` and `QueueFull` (503 with `Retry-After`) |
| Asynchronous processing | [`TelemetryProcessor.cs`](src/SmartX.Api/Ingestion/TelemetryProcessor.cs) | the `BackgroundService` that drains the queues, and why there is one worker |
| Device shadow | [`SimulationEndpoints.cs`](src/SmartX.Api/Endpoints/SimulationEndpoints.cs), [`DeviceShadowStore.cs`](src/SmartX.Api/Storage/DeviceShadowStore.cs) | desired state with a version; `GET /api/devices/{mac}/desired` |
| Device simulator | [`DeviceFleet.cs`](src/SmartX.DeviceSimulator/DeviceFleet.cs), [`GatewayClient.cs`](src/SmartX.DeviceSimulator/GatewayClient.cs) | registration, backfill, backoff, and applying desired state, all over HTTP |
| Dictionary and HashSet | [`TrustEngine.cs`](src/SmartX.Api/Trust/TrustEngine.cs) | `_nodes`, keyed by MAC for O(1) lookup per packet, and `_degraded`, where each failing node appears once |
| The ingestion pipeline, stage by stage | [`TelemetryEndpoints.cs`](src/SmartX.Api/Endpoints/TelemetryEndpoints.cs) | `IngestBatchAsync`, with each stage numbered in comments, and `TryCreatePacket` |
| Polymorphic JSON | [`ITelemetryPacket.cs`](src/SmartX.Shared/Packets/ITelemetryPacket.cs) | `[JsonDerivedType]` over the packet types |
| Trust formula | [`TrustFormula.cs`](src/SmartX.Shared/Trust/TrustFormula.cs) | `Score` (Laplace smoothing), `DecayWeight` (half-life), `Classify` |
| Background services | [`HeartbeatMonitor.cs`](src/SmartX.Api/Trust/HeartbeatMonitor.cs), [`TelemetryProcessor.cs`](src/SmartX.Api/Ingestion/TelemetryProcessor.cs), [`DeviceFleet.cs`](src/SmartX.DeviceSimulator/DeviceFleet.cs) | `PeriodicTimer` driven by an injected `TimeProvider`; a worker that survives one bad reading |
| Async all the way down | [`SmartXApiClient.cs`](src/SmartX.Client/Services/SmartXApiClient.cs), [`ISensorRegistry.cs`](src/SmartX.Api/Storage/ISensorRegistry.cs) | no `.Result`, `.Wait()` or `GetAwaiter().GetResult()` anywhere |
| Streamed, encrypted I/O | [`AttachmentUploader.cs`](src/SmartX.Api/Attachments/AttachmentUploader.cs), [`AesGcmEncryptingStream.cs`](src/SmartX.Api/Attachments/AesGcmEncryptingStream.cs) | `MultipartReader`, not `IFormFile`, and chunked AES-256-GCM |

## Configuration reference

The API reads configuration in the standard ASP.NET Core order: `appsettings.json`, then
`appsettings.{Environment}.json`, then user secrets (Development only), then environment variables, then
command-line arguments. Later sources win. An environment variable uses `__` for each `:` in a setting
name, so `Trust:HalfLife` becomes `Trust__HalfLife`. The `Attachments`, `Zones`, `Ingestion`, `Telemetry` and
`Trust` sections are validated at startup, and a bad value stops the API with a message naming the
setting.

### API: `Attachments`

| Setting | Default | What it does |
| --- | --- | --- |
| `Attachments:StoragePath` | `App_Data/attachments` (`/data/attachments` in Docker) | Where encrypted files and their index entries are written. A relative path is resolved against the API's content root. |
| `Attachments:MaxFileBytes` | `104857600` (100 MiB) | The largest file accepted. It is enforced byte by byte while the upload streams, and published to the client at `/api/attachments/limits`. |
| `Attachments:EncryptionKey` | *(none)* | Base64-encoded 256-bit AES key. See [the encryption key](#the-encryption-key). |

### API: `Zones`

| Setting | Default | What it does |
| --- | --- | --- |
| `Zones:Hierarchy` | `Brief` | Which deployment hierarchy the API manages. `Brief` is the four zones from the brief, and what the tests expect. `Demo` is the 16-zone, two-facility hierarchy the simulated fleet is deployed into. |

### API: `Ingestion`

| Setting | Default | What it does |
| --- | --- | --- |
| `Ingestion:MaxQueueDepth` | `10000` | The most readings the standard lane holds. When it is full, the API answers `503` with `Retry-After`. |
| `Ingestion:MaxCriticalQueueDepth` | `1000` | The most readings the critical lane holds, bounded separately so a full standard lane never blocks an alert. |
| `Ingestion:RetryAfterSeconds` | `5` | The value of the `Retry-After` header on a `503`. |
| `Ingestion:MaxConsecutiveCritical` | `20` | After this many critical readings in a row, the worker takes one normal reading if one is waiting. The starvation valve. |
| `Ingestion:CriticalRules` | see [Ingestion queues](#ingestion-queues) | The thresholds that make a reading critical: category, unit, an upper and/or lower limit, a severity (lower is more urgent) and a reason. Set as a list in `appsettings.json`. |

### Simulator: `Simulator`

Bound by `SmartX.DeviceSimulator`, from `src/SmartX.DeviceSimulator/appsettings.json` or the environment
(`Simulator__ApiBaseUrl` and so on).

| Setting | Default | What it does |
| --- | --- | --- |
| `Simulator:ApiBaseUrl` | `http://localhost:5266` (`http://api:8080` in Docker) | Where the API is. |
| `Simulator:DeviceCount` | `40` | How many of the demo devices to run, from the top of the list. |
| `Simulator:Mix` | `Demo` | `Demo` gives each device its own scenario, for a spread of bands. `Nominal` starts every device healthy. |
| `Simulator:Scenarios:<mac>` | none | Starts one device in a named scenario, on top of the mix. A MAC contains colons, which can't be in an environment variable name, so write it with underscores: `Simulator__Scenarios__B8_27_EB_10_00_01=Critical`. |
| `Simulator:BackfillDuration` | `00:10:00` | How much history is posted on first start. `00:00:00` turns backfill off. |
| `Simulator:BackfillConcurrency` | `8` | How many backfill requests may be in flight at once. |
| `Simulator:ShadowPollInterval` | `00:00:05` | How often each device asks the API what it should be doing. Also how long a scenario change takes to appear. |
| `Simulator:TickInterval` | `00:00:00.5` | How often the fleet checks which devices are due to report. Must be well under a fifth of the shortest heartbeat, which is five seconds in the demo fleet. |
| `Simulator:RandomSeed` | `1729` | Seeds the generated values, so two runs produce the same readings. |
| `Simulator:StartupTimeout` | `00:05:00` | How long to keep retrying while the API isn't answering before giving up. |

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
| `SMARTX_ZONE_HIERARCHY` | `Demo` | Becomes `Zones__Hierarchy` for the API. |
| `SMARTX_QUEUE_DEPTH` | `10000` | Becomes `Ingestion__MaxQueueDepth`. |
| `SMARTX_DEVICE_COUNT` | `40` | Becomes `Simulator__DeviceCount`. |
| `SMARTX_SCENARIO_MIX` | `Demo` | Becomes `Simulator__Mix`: `Demo` or `Nominal`. |
| `SMARTX_BACKFILL` | `00:10:00` | Becomes `Simulator__BackfillDuration`. |
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
| `POST` | `/api/telemetry` | One reading: `202` with a receipt (lane and queue position), `400` (rejected, with the reason), `404`, `503` with `Retry-After` when the queue is full |
| `POST` | `/api/telemetry/batch` | A jagged batch of readings, queued in order: `202` with per-reading errors, or `503` |
| `GET` | `/api/pipeline/stats` | Depth, throughput and wait time per lane, and the count of `503` refusals |
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
| `GET` | `/api/simulation` | The desired state recorded for each device |
| `POST` | `/api/simulation/{mac}/scenario` | `{ "scenario": "Drift" }` records the desired state: `202`, `400` (not a scenario), `404` |
| `GET` | `/api/devices/{mac}/desired` | A device's own desired state, polled by the device: `200`, or `404` when nothing has been asked of it |

Sensors, readings and trust are held in memory. Restarting the API clears them, apart from what the
simulator registers again when it starts. Attachments are the exception, and persist on disk.

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

Sensors are held in memory, so a restarted API has forgotten every sensor. Register them again, or restart
the device simulator, which re-registers its whole fleet on start.

### After restarting the API, some devices are missing

The API holds its device list in memory, so a restart empties it. Devices that are reporting notice, register
themselves again and are back within seconds. Devices running the `Disconnect` scenario send nothing, so
nothing tells them the API forgot them, and they stay unregistered until they reconnect. Restarting the
simulator (`docker compose restart simulator`) re-registers the whole fleet at once.

### Telemetry returns 503

The ingestion queue is full: the API is receiving readings faster than it processes them. The response's
`Retry-After` header says how long to wait. See `GET /api/pipeline/stats` for the queue depth. If it happens
under a normal load, raise `Ingestion:MaxQueueDepth`; if a device is flooding the API, that is what the
limit is for.

### The dashboard is empty when running the simulator

- The API must be started with `--Zones:Hierarchy=Demo` (or `SMARTX_ZONE_HIERARCHY=Demo` in Docker). With the
  default hierarchy, the API refuses the demo devices' zones and the simulator stops with a message naming
  the first device it could not register.
- The simulator logs `Waiting for the gateway` while the API isn't answering, and gives up after
  `Simulator:StartupTimeout`. Check the API is running and `Simulator:ApiBaseUrl` is right.
- In Docker, read the simulator's log with `docker compose logs simulator`.

### A scenario change doesn't take effect straight away

That is expected. The API records the desired state and the device applies it on its next poll, which is
every `Simulator:ShadowPollInterval` (five seconds by default). Trust then takes a few heartbeats to
move.

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
device simulator, the ingestion queues, and the measured critical bypass. The API tests boot the real API in memory, each with its own stores and its own
temporary storage directory.
