# Update ledger

Changes made to Smart-X in response to feedback and review, oldest first. Each entry records what was
wrong, why it mattered, what changed, and the commit where it happened, so any change can be traced back
to the reason for it and checked with `git show <commit>`.

New entries are appended at the end using the [template](#entry-template). Part 3 asks for a list of
updates made from Parts 1 and 2 feedback. This file is that list, kept while the work happens instead of
reconstructed afterwards.

## Lecturer feedback on Part 1

**Criterion:** Gateway Startup Layout and API Integration. **Mark: 5/10.** This is the only criterion that lost marks in Part 1.

| # | Feedback, verbatim | Answered by | Notes |
| --- | --- | --- | --- |
| 1 | "The rest of your startup flow is flawless, with clean pillar disabling and fully asynchronous communication. There is no authentication on the API at all, so add a login step and protect your sensitive endpoints with it in Part 2." | [U21](#u21-the-api-had-no-authentication) | Operators log in with a JWT and have a role; devices authenticate with a per-device key; the hub requires a token. |

Entries U1 to U12 each name their own source (a review of the code, a test, a live run); none of them is recorded as lecturer feedback.

## Summary

| # | Date | Area | Change | Commit |
| --- | --- | --- | --- | --- |
| [U1](#u1-combining-readings-from-different-categories) | 2026-08-20 | Packets | Reject combining readings from different sensor categories | `33f0cb4` |
| [U2](#u2-new-nodes-reported-as-degrading) | 2026-09-11 | Trust | Add an `Unproven` band for nodes with no evidence | `76479dc` |
| [U3](#u3-a-rounding-error-could-clear-a-fault) | 2026-09-11 | Trust | Treat fully decayed evidence as unproven | `976e3ca` |
| [U4](#u4-inconsistent-configuration-binding) | 2026-09-11 | API | Bind telemetry retention through `IOptions` | `01d1a19` |
| [U5](#u5-n1-requests-on-the-trust-panel) | 2026-09-13 | Trust, client | Carry recent history in the trust list | `72596d6` |
| [U6](#u6-attachments-lost-on-restart) | 2026-09-14 | Attachments | Persist the attachment index and use a fixed development key | `432643a`, `7d08321` |
| [U7](#u7-simulated-data-did-not-arrive-like-a-devices) | 2026-09-29 | Simulation, ingestion | Move the simulator out of the API so it reports over HTTP, and queue accepted readings | `7216026`, `e6f8f0b` |
| [U8](#u8-silent-devices-stayed-unregistered-after-an-api-restart) | 2026-09-30 | Simulator | Re-register the whole fleet on the first 404 | `f528e93` |
| [U9](#u9-a-silent-devices-control-channel-was-implicit-and-untested) | 2026-09-30 | Simulator | State and test that a silent device keeps polling its shadow | `06e1195` |
| [U10](#u10-the-command-log-grew-without-bound) | 2026-10-04 | Commands, history | Move the command log into the retained history store | `d2039dc` |
| [U11](#u11-one-timing-test-could-fail-a-cold-first-run) | 2026-10-07 | Tests | Keep wall-clock assertions out of the default run | `a06eec1` |
| [U12](#u12-the-telemetry-page-made-one-request-per-sensor) | 2026-10-08 | Telemetry, client | Load every sensor's recent readings in one request | `21a315e` |
| [U13](#u13-a-gateway-error-was-shown-as-an-answer) | 2026-10-07 | Client | Treat 502 and 504 from the web server as the API being unreachable | `34ceca0` |
| [U14](#u14-the-baseline-for-lift-counted-occurrences-not-time) | 2026-10-07 | Patterns | Measure the lift baseline as the time an action's windows cover | `3fb9dc1` |
| [U15](#u15-the-general-level-compared-a-pump-with-every-pump) | 2026-10-07 | Patterns | Give the general level a baseline per zone | `2c7c61f` |
| [U16](#u16-one-click-was-learned-as-twelve-for-twelve) | 2026-10-13 | Patterns | Credit an action to the latest trigger of each kind | `ff6dd24` |
| [U17](#u17-the-forecast-could-not-see-a-fall-that-began-after-a-flat-stretch) | 2026-10-13 | Insights | Forecast from the most recent straight stretch of a trust series | `bbbde6f` |
| [U18](#u18-suggestions-deadlocked-with-the-device-index) | 2026-10-13 | Suggestions | Take engine events from an inbox so ranking never meets the index lock | `96c4320` |
| [U19](#u19-compose-would-not-start-without-an-env-file) | 2026-10-13 | Docker | Default the development key so a fresh clone starts with no `.env` | `ae8a4e1` |
| [U20](#u20-accessibility-findings-in-the-light-theme) | 2026-10-13 | Client | Contrast, names, landmarks and live regions found by an axe pass | `b62dccc`, `5c23e63`, `7b5e0fc`, `3bd34b4`, `718d7e0` |
| [U21](#u21-the-api-had-no-authentication) | 2026-10-14 | API, client, simulator | Add a login with roles, per-device keys, a protected hub and a login page; protect every endpoint but health and login | `5b33a2b`, `b845474`, `76355a3`, `3fcb356`, `7920580`, `94be739`, `36d261e`, `fbe6bb3`, `1041e6b` |
| [U22](#u22-history-range-queries-walked-from-the-oldest-entry) | 2026-10-22 | History | Replace the sorted-dictionary store with an AVL tree as the default, so a time-range query seeks | `6b706e9`, `ea4e45c`, `df974a5`, `ecfefe0` |
| [U23](#u23-the-mesh-was-not-modelled-as-a-network) | 2026-10-22 | Topology | Model the mesh as a weighted graph built from device-reported neighbours, with breadth-first reachability and articulation points | `e52b330`, `ce6c806`, `39d0dac` |
| [U24](#u24-the-backbone-of-the-mesh-and-what-happens-when-part-of-it-fails) | 2026-10-22 | Topology | Compute the minimum spanning tree (Prim's, checked by Kruskal's), route devices along it, compare with shortest paths, and record routes that heal | `380db8a`, `69fed7a`, `25d9b52`, `4a15b8a` |
| [U25](#u25-the-test-suite-had-redundant-and-slow-tests) | 2026-10-22 | Tests | Remove duplicate tests and run theory rows on one host, using the test audit: 1,429 tests to 1,347 and 26 s to 20 s | `4eeb56a`, `5733243`, `2a47ae4`, `89d1fa1`, `16c9582` |

---

## U1. Combining readings from different categories

**Source:** Part 1 review of the generic packet types, raised in Prompt 3

**What was wrong.** `TelemetryPacket<T>.EnsureCombinable`, the guard behind the `+`, `-`, `<` and `>`
operators on `NumericPacket<T>`, only compared units. Two readings with the same unit and different
categories passed. For example, an `Environmental` humidity sensor reporting `%` could be added to an
`Actuator` valve reporting its position in `%`.

**Why it mattered.** Both are `NumericPacket<float>`, so the compiler cannot tell them apart. The sum is a
number with no physical meaning, and it would have come back as a valid packet that could feed straight
into an aggregate. Operator overloading is one of the assessed concepts, and an operator that quietly
produces nonsense is worse than one that isn't there.

**What changed.** `EnsureCombinable` now checks category as well as unit, and throws
`InvalidOperationException` naming both categories. Equality still does not throw, because asking whether
two different readings are equal has a correct answer: they are not. Tests cover each operator with a
category mismatch.

- `src/SmartX.Shared/Packets/TelemetryPacket.cs`
- `tests/SmartX.Tests/NumericPacketTests.cs`

**Commit:** `33f0cb4` fix(shared): reject combining readings from different categories

---

## U2. New nodes reported as degrading

**Source:** Part 1 review of the trust engine, raised in Prompt 6

**What was wrong.** A node with no outcomes scores the Laplace prior, `(0 + 1) / (0 + 0 + 2) = 0.5`. Under
the default thresholds (fault below 0.5, nominal from 0.75) that is **Degrading**. Every newly registered
sensor appeared on the dashboard as failing before it had sent anything.

**Why it mattered.** "Nobody has heard from this node yet" and "this node is getting worse" are different
states, and the engine reported both the same way. New nodes joined the degraded set that alerting reads,
mesh confidence was pulled toward 0.5 on every registration, and a healthy mesh looked unhealthy right
after setup.

**What changed.**

- Added `TrustBand.Unproven`. A node with no outcomes is unproven whatever its score.
- Unproven nodes are kept out of the degraded set and out of the mesh-confidence average. They are still
  counted in the summary.
- Worst-first ordering uses an explicit `TrustFormula.Severity` rather than the enum's numeric order.
  Otherwise an unproven 0.5 would sort above a degrading 0.6.

Files:

- `src/SmartX.Shared/Trust/TrustBand.cs`
- `src/SmartX.Shared/Trust/TrustFormula.cs`
- `src/SmartX.Shared/Trust/TrustSummary.cs`
- `src/SmartX.Api/Trust/TrustEngine.cs`
- `tests/SmartX.Tests/UnprovenBandTests.cs`

**Commit:** `76479dc` fix(trust): add unproven band for nodes with no outcomes

---

## U3. A rounding error could clear a fault

**Source:** Part 1 review of the trust engine, raised in Prompt 6

**What was wrong.** Evidence decays with a ten-minute half-life. After a long silence the weighted success
and failure totals drop below about 1e-16. At that point `s + 1` rounds to exactly 1 and `s + f + 2` to
exactly 2, so the score becomes exactly 0.5. A faulted node whose evidence had decayed away was then
classified as Degrading.

**Why it mattered.** Fault to Degrading is a recovery, and it raises `FaultCleared`. Floating-point
rounding would have announced a repair that never happened. It was also the exact case U2 was meant to
handle, but it didn't trigger, because the node still had outcomes and they just weighed nothing.

**What changed.** Added `TrustFormula.MinimumEvidence = 1e-9`. When the decayed evidence totals less than
that, the band is Unproven regardless of score, so the U2 rule applies. A node that goes from Fault to
Unproven ends its fault episode without raising `FaultCleared`, so an alert is never cleared for a node
nobody repaired. The threshold is several orders of magnitude above where rounding starts, so it triggers well
before the arithmetic breaks down.

- `src/SmartX.Shared/Trust/TrustFormula.cs`
- `src/SmartX.Api/Trust/TrustEngine.cs`
- `tests/SmartX.Tests/EvidenceGuardTests.cs`

**Commit:** `976e3ca` fix(trust): treat fully decayed evidence as unproven

---

## U4. Inconsistent configuration binding

**Source:** Part 1 review of the pipeline and trust configuration, raised in Prompt 6

**What was wrong.** The trust section was bound through `IOptions<TrustOptions>` with a validator and
`ValidateOnStart`. Telemetry retention in the same API was different: it was read with
`builder.Configuration.GetValue<int?>` and passed into the store's constructor through a factory.

**Why it mattered.** One API used two configuration patterns, so anyone reading `Program.cs` would ask
which one was intended. The retention path also skipped startup validation. A negative value would only
fail when the singleton store was first built, which meant on the first telemetry request instead of at
startup.

**What changed.** Added `TelemetryOptions`, bound from the `Telemetry` section and validated at startup.
`InMemoryTelemetryStore` now takes `IOptions<TelemetryOptions>`. The constructor keeps its own guard,
because tests and other hosts can build the store without startup validation.

- `src/SmartX.Api/Storage/TelemetryOptions.cs`
- `src/SmartX.Api/Storage/InMemoryTelemetryStore.cs`
- `src/SmartX.Api/Program.cs`
- `src/SmartX.Api/appsettings.json`

**Commit:** `01d1a19` refactor(api): bind telemetry retention through IOptions

---

## U5. N+1 requests on the trust panel

**Source:** Part 1 review of the client UI, raised in Prompt 7

**What was wrong.** Every 3 seconds the trust panel fetched the summary and the node list, then made one
more request per node for its sparkline history. That is N + 2 requests per refresh.

**Why it mattered.** The load grew with the size of the mesh. A 40-sensor mesh (the size the seeder
registers) meant 42 requests every 3 seconds from each open browser tab, about 840 a minute, nearly all
fetching history the list response could have carried.

**What changed.**

- `NodeTrustSnapshot` now carries `RecentHistory`: the last `Trust:SnapshotHistoryPoints` samples (30 by
  default, about five minutes). They are read from the tail of the node's ring buffer by index, so a
  snapshot allocates only what it returns.
- The client's per-node history calls are gone, and a refresh is a fixed two requests.
- The full per-node history endpoint is still there for anything that needs the whole series.
- A test counts outgoing requests with 3 nodes and with 40, and asserts exactly two both times.

Files:

- `src/SmartX.Shared/Trust/NodeTrustSnapshot.cs`
- `src/SmartX.Api/Trust/NodeTrust.cs`
- `src/SmartX.Api/Trust/TrustOptions.cs`
- `src/SmartX.Client/Services/PollingTrustFeed.cs`
- `tests/SmartX.Tests/TrustFeedRequestTests.cs`

**Commit:** `72596d6` perf(trust): carry recent history in the trust list

---

## U6. Attachments lost on restart

**Source:** Part 1 review of the attachment feature before the demo recording, raised in Prompt 8

**What was wrong.** Two separate problems had the same result:

1. With no key configured, the development API encrypted attachments with a random key that only lived as
   long as the process. After a restart, files on disk could never be decrypted.
2. The attachment index (names, sizes, which sensor each file belongs to) was held in memory. After a
   restart the files were still on disk but nothing could find them.

**Why it mattered.** Restarting the API lost every attachment, which makes a demo hard to record. It would
also have made the Docker volume pointless, since the volume would have kept files nothing could find or
read.

**What changed.**

- `FileAttachmentStore` writes a small JSON index entry next to each encrypted file, under the same GUID,
  through a `.partial` file and a rename. Entries are loaded once at startup and served from memory.
- Each entry records a fingerprint of the key its file was encrypted with: the first 64 bits of the key's
  SHA-256. Entries for a different key are hidden rather than listed as downloads that would fail. They are
  not deleted, so restoring the key brings them back.
- A fixed development key is published in the README and in `.env.example`. It is set once with
  `dotnet user-secrets`, or through `.env` for Docker, and is never in source or a settings file the API
  loads. The API recognises the key by its fingerprint and logs a warning if it is used outside Development.

Files:

- `src/SmartX.Api/Attachments/FileAttachmentStore.cs`
- `src/SmartX.Api/Attachments/AttachmentStorage.cs`
- `src/SmartX.Api/Attachments/AttachmentKeyProvider.cs`
- `tests/SmartX.Tests/AttachmentStorageTests.cs`

**Commits:**

- `432643a` feat(api): fingerprint the attachment key and flag the published one
- `7d08321` feat(api): persist the attachment index so uploads survive restarts

---

## U7. Simulated data did not arrive like a device's

**Source:** Part 2 brief, which says simulated data "has to hit the API like it was a device"

**What was wrong.** Part 1's seeder ran inside the API process. It registered sensors by calling the sensor
registry directly, and injected readings by calling the telemetry store and trust engine directly. The
brief's Part 2 requirement is that simulated data reaches the API the way a real device's would.

**Why it mattered.** Nothing the seeder did went through HTTP, routing, validation or serialisation, so it
proved nothing about how the API behaves under real traffic. A bug in any of those layers would have been
invisible in the demo. It also meant the "device" and the gateway were one program, which is not how the
system is deployed.

**What changed.**

- A new project, `SmartX.DeviceSimulator`, registers its devices with `POST /api/sensors`, posts telemetry
  with `POST /api/telemetry`, and backfills history through the same endpoint using timestamps. It references
  `SmartX.Shared` only, so it has no route into the API's code.
- The in-process seeder is deleted. The zone hierarchy stays in the API, selected with `Zones:Hierarchy`,
  because zones are gateway configuration and not device data.
- The simulator can no longer be told to change scenario by a method call, so scenario control became a
  device shadow: the API stores desired state and each device polls for its own.
- With real traffic arriving, validating and processing on the same thread no longer suited the API.
  Validation stays synchronous, so rejections still return the same status codes and the same text as in
  Part 1, and processing moved behind a bounded FIFO queue and a priority queue for critical alerts.

One Part 1 behaviour did change, and is stated here rather than left to be discovered: an accepted reading's
response body is now a receipt (lane and queue position) instead of the stored packet, because at the moment
the response is written the packet has not been stored yet. `POST /api/telemetry/batch` no longer reports how
many readings the node's buffer holds, for the same reason. The Part 1 tests that read the response body were
changed to wait for the worker and read the stored packet back from history, which asserts what was actually
retained. No other Part 1 test needed changing.

- `src/SmartX.DeviceSimulator/`
- `src/SmartX.Api/Ingestion/`
- `src/SmartX.Api/Endpoints/TelemetryEndpoints.cs`
- `src/SmartX.Api/Endpoints/SimulationEndpoints.cs`
- `tests/SmartX.Tests/DeviceSimulatorTests.cs`
- `tests/SmartX.Tests/IngestionQueueTests.cs`

**Commits:**

- `7216026` feat(api): validate at the edge and process readings behind two queues
- `e6f8f0b` feat(simulator): add device simulator that reports over http

---

## U8. Silent devices stayed unregistered after an API restart

**Source:** Part 2 review of the simulator, raised in Prompt 10

**What was wrong.** When the API restarted it forgot every registered device, because it holds them in memory.
The simulator recovered by re-registering a device the first time that device got a `404` for a report. A device
running the `Disconnect` scenario sends nothing, so it never got a `404` and never re-registered.

**Why it mattered.** After an API restart, the four deliberately silent devices stayed missing for good, and
because they were missing the operator could not send them a scenario change either (`404`, no such sensor).
Recovery was partial in exactly the cases where an operator most wants a device back.

**What changed.** A restart forgets *every* device at once, so the simulator now treats it that way: the first
`404` any device sees re-registers the whole fleet. Concurrent `404`s are collapsed into one sweep with a
semaphore and a "registered since I asked" check, so forty devices hearing the same restart cause one round of
registration and not forty. Tests restart the API under a running fleet and check that all thirteen devices in
the test fleet come back, including the silent one, and that thirteen concurrent `404`s make thirteen
registrations and not a hundred and fifty-six.

- `src/SmartX.DeviceSimulator/DeviceFleet.cs`
- `tests/SmartX.Tests/DeviceSimulatorTests.cs`

**Commit:** `f528e93` fix(simulator): re-register the whole fleet on the first 404

---

## U9. A silent device's control channel was implicit and untested

**Source:** Part 2 review of the simulator, raised in Prompt 10

**What was wrong.** The simulator kept polling a `Disconnect` device's shadow, which is what lets an operator
bring it back, but only because of the order of two `if` statements. Nothing said that was intended, and
nothing tested it. A tidy-up that moved the silence check earlier would have made a silent device impossible to
switch back to `Nominal`, with no failing test.

**Why it mattered.** The requirement is that an operator can bring a silent device back. That behaviour has to
be a stated design decision with a test, not a side effect.

**What changed.** A comment on the tick loop states the split: telemetry is the data plane, which is what the
simulated device is sending and which `Disconnect` switches off; polling the shadow is the harness's control
plane, which is not something the simulated device is doing and so keeps running. A test switches a silent
device to `Nominal` and checks that it starts reporting.

- `src/SmartX.DeviceSimulator/DeviceFleet.cs`
- `tests/SmartX.Tests/DeviceSimulatorTests.cs`

**Commit:** `06e1195` fix(simulator): keep polling the shadow while a device is silent

---

## U10. The command log grew without bound

**Source:** design review of the command log, raised in Prompt 11

**What was wrong.** `InMemoryCommandLog` appended every command event to a list and never removed one. A gateway
that ran for a month with a busy operator would hold every event it had ever logged, in a structure that could
also only be searched by scanning from the newest entry.

**Why it mattered.** Memory use that grows with uptime is a fault that appears days after it was introduced, which
makes it a bad one to find in production. The log is also the record the pattern analysis reads, so it needed to
sit alongside the readings and alerts it will be read with, in time order.

**What changed.** The log now lives in the history store behind the same `ICommandLog` interface, so the code that
writes to it did not change. Its entries share the retention window with every other kind of history, and each
carries the alerts that were active across the mesh when it was written. Reading goes through the mesh-wide event
stream, which holds only events, so finding a command does not walk a node's readings. A test appends a command,
advances the clock past the window, runs retention, and checks it is gone while a recent one is kept.

- `src/SmartX.Api/Commands/CommandLog.cs`
- `src/SmartX.Api/Commands/CommandService.cs`
- `tests/SmartX.Tests/CommandHistoryTests.cs`

**Commit:** `d2039dc` feat(api): add alert set and device index, feed history from the pipeline

---

## U11. One timing test could fail a cold first run

**Source:** review before Prompt 12: `CriticalBypassMeasurementTests` failed once on a cold first run

**What was wrong.** A test asserted wall-clock thresholds: the critical reading must wait under 100 ms and the normal
one over 500 ms. On a cold or busy machine a threshold can be missed for no fault of the code, and a marker running
`dotnet test` once, cold, would read one red test as a broken build. Two later tests did the same in smaller ways (a
range query ratio and a 250 ms search bound).

**Why it mattered.** A flaky test is worse than no test: it teaches people to ignore red.

**What changed.** The default suite now proves the behaviour by order only. A gated store holds the worker mid-reading,
the critical and ordinary readings are queued behind a backlog (in both arrival orders), and the test asserts the order
the store saw them in. The timings moved behind `[Trait("Category", "Benchmark")]` and are excluded from a bare
`dotnet test` by a runsettings file the test project applies. They still print their numbers when run on purpose; the
command is in the README. Five clean-build runs of the full suite passed.

- `tests/SmartX.Tests/CriticalLaneOrderTests.cs`
- `tests/SmartX.Tests/CriticalBypassMeasurementTests.cs`
- `tests/SmartX.Tests/default.runsettings`

**Commit:** `a06eec1` test: keep wall-clock assertions out of the default run

---

## U12. The telemetry page made one request per sensor

**Source:** Part 1 review of the client, left open when the trust panel was fixed (U5); raised again in Prompt 13

**What was wrong.** Opening the telemetry page asked the API for each sensor's recent readings separately: one request
per sensor, 48 with the demo fleet, sent together. It was the same fault as the trust panel's N + 2 (U5), in the one
page that had not been given the same treatment.

**Why it mattered.** The cost of opening a page grew with the size of the mesh, and every visit paid it. Navigation is
assessed, and a page that fires dozens of requests on every open is the opposite of instant.

**What changed.** `GET /api/telemetry/recent` returns the retained readings for every registered sensor, keyed by MAC, in
one response. A sensor with no readings has an empty list, so the keys are exactly the registered sensors. The client's
`TelemetryService.LoadAllAsync` makes that one call. The single-sensor endpoint stays, and the page still uses it after
sending a reading by hand. A test counts outgoing requests with 3 sensors and with 48 and asserts exactly one both times,
and that every card still has its readings.

- `src/SmartX.Api/Endpoints/TelemetryEndpoints.cs`
- `src/SmartX.Client/Services/SmartXApiClient.cs`
- `src/SmartX.Client/Services/TelemetryService.cs`
- `tests/SmartX.Tests/TelemetryLoadRequestTests.cs`

**Commit:** `21a315e` perf(telemetry): load every sensor's recent readings in one request

---

## U13. A gateway error was shown as an answer

**Source:** found in a live run of Prompt 12 (real-time push), with the API stopped behind nginx

**What was wrong.** The client treated a refused connection as "the API is unreachable" and kept the last data on screen. With the
API down behind nginx, nginx answers `502 Bad Gateway` (or `504`), which is an HTTP response, so the client took it as the API's
answer and showed "Bad Gateway" in place of the data.

**Why it mattered.** The same fact, said by a different party, produced a different and worse behaviour: the panel lost its last
known state exactly when an operator most needed it. Navigation and recovery behaviour are assessed.

**What changed.** `SmartXApiClient` maps 502 and 504 to the unreachable result. 503 is deliberately not included, because the API
itself uses it to say a queue is full. A test covers the gateway error.

- `src/SmartX.Client/Services/SmartXApiClient.cs`
- `tests/SmartX.Tests/ClientRealtimeTests.cs`

**Commit:** `34ceca0` fix(client): treat 502 and 504 as the API being unreachable

---

## U14. The baseline for lift counted occurrences, not time

**Source:** found by running the brief's example in Docker in Prompt 14, which failed

**What was wrong.** Lift compares how often an action follows a trigger with how likely the action is in any window anyway. The first
baseline counted how many times the action happened and turned that into a Poisson chance. Three opens of a device in ten seconds
counted as three occurrences, which inflated "likely anyway".

**Why it mattered.** The example the brief describes came out with a lift of 0.65, below one, so the engine said the trigger made
no difference and the pattern never qualified. A baseline that depends on how bursty an operator is does not measure the thing it
claims to.

**What changed.** The baseline is now the share of time that falls within a window of an occurrence of the action. A burst covers one
window, constant activity covers everything, and an action seen now and then covers a window each time. Both the covered time and the
watching time fade with the same half-life. The cost, stated in the README: an engine that has watched for only a few windows cannot
yet show much lift, so it needs hours at the default window. Qualified associations also sort ahead of unqualified ones, because lift
alone cannot order them once counts fade together.

- `src/SmartX.Api/Patterns/PatternEngine.cs`
- `src/SmartX.Api/Patterns/PatternOptions.cs`
- `tests/SmartX.Tests/PatternEngineTests.cs`

**Commit:** `3fb9dc1` fix(patterns): measure the baseline for lift as the time an action's windows cover

---

## U15. The general level compared a pump with every pump

**Source:** found by the same Docker run as U14, after it

**What was wrong.** The engine learns at two levels: a specific device, and a general kind of device ("a pump"). The general
baseline was how often any pump in the mesh was opened. A pump opened constantly in another zone made every pump look as if it
followed everything.

**Why it mattered.** A real pattern was hidden (lift 1.07 where it should have been several times that), and the "similar devices"
suggestion that depends on it would never have appeared where it is useful.

**What changed.** The general action is now a kind of device in a zone ("a pump in Zone 1"), which has its own baseline, built from
the same recursive zone walk Part 1 uses. The support tolerance moved into `Patterns:SupportTolerance` so it can be configured and
validated. A general pairing is counted only for an action in the same group as the trigger, which is a stated limit.

- `src/SmartX.Api/Patterns/PatternEngine.cs`
- `src/SmartX.Api/Patterns/PatternOptions.cs`
- `tests/SmartX.Tests/PatternEngineTests.cs`
- `tests/SmartX.Tests/PatternEndpointTests.cs`

**Commit:** `2c7c61f` fix(patterns): give the general level a baseline per zone

---

## U16. One click was learned as twelve for twelve

**Source:** found in a Docker run of Prompt 15: the operator's own click on a device appeared as a habit that had held "12 of 12 times"

**What was wrong.** An action was paired with every trigger still in the window. A noisy sensor had fired twelve times in ten
minutes, so one click was credited to all twelve firings, and the evidence read twelve out of twelve.

**Why it mattered.** One response to a noisy sensor looked like a perfect habit, so the suggestion ranked far above what the evidence
supports, and an explanation that says "12 of 12" is a false statement.

**What changed.** An action answers each kind of trigger once: it is credited to the latest firing of each kind in the window. Every
firing is still counted as a trigger, which is what makes a noisy sensor's confidence low, as it should be. This changed Prompt 14's
pairing rule, and the engine benchmark was unchanged. Four tests cover it.

- `src/SmartX.Api/Patterns/PatternEngine.cs`
- `src/SmartX.Client/CommandStream/SuggestionPresentation.cs`
- `tests/SmartX.Tests/PatternReplayTests.cs`

**Commit:** `ff6dd24` fix(patterns): credit an action to the latest trigger of each kind

---

## U17. The forecast could not see a fall that began after a flat stretch

**Source:** found by running the forecast against a simulated fouling probe in the Docker stack, in Prompt 15

**What was wrong.** The first forecast fitted one line to the whole four-minute window of a node's trust samples. A node that is fine
and then starts to fall is a hockey stick, and one line through both parts fits badly (R² 0.04 to 0.5) for as long as the flat part
is in the window, which is exactly when an operator wants to know. It showed nothing for the first minutes of every fall.

**Why it mattered.** The point of the feature is to flag a node before it faults. The tests passed because their series were straight
lines from the first sample, which real nodes are not.

**What changed.** The forecaster tries the whole window and its most recent three quarters, half and third (never fewer than the
minimum samples), and of those that clear every bar it takes the longest whose fit is within 0.02 of the best. A steady fall is judged
on the whole window and a fall that has only just begun on the part that is falling. A flat series, too few samples, a flattening
fall and a reversal are tested, and a host test drives the real sampler. A straight line still cannot know that a fall will level
off, which is stated in the README.

- `src/SmartX.Api/Patterns/TrustForecaster.cs`
- `src/SmartX.Api/Patterns/InsightService.cs`
- `tests/SmartX.Tests/TrustForecasterTests.cs`
- `tests/SmartX.Tests/TrustSamplingHostTests.cs`

**Commit:** `bbbde6f` feat(insights): forecast from the most recent straight stretch of a trust series

---

## U18. Suggestions deadlocked with the device index

**Source:** found in the Docker stack in Prompt 15: `/api/suggestions` stopped answering, and the forecast never appeared there although it did in tests and in a local run

**What was wrong.** The suggestion service handled the pattern engine's events on whichever thread raised them. The alert set raises
its changes inside the lock it shares with the device index, and an alert is a trigger, so the engine could be told something while
that lock was held. Ranking asks the index about devices while holding the service's own lock. Two threads took the two locks in
opposite orders and both waited for ever.

**Why it mattered.** It hung a request endpoint, and only under real concurrency, which the single-threaded tests never produced.

**What changed.** The handlers only post the event to an inbox and return holding nothing, and one loop works through it, so no
thread holds one lock while asking for the other. Tests run the service inline (`processInline`) for determinism, and a regression test
has one thread raise 3,000 triggers from inside the index's lock while another ranks 3,000 times: a deadlock is a test that does not
finish. The same sweep serialised a stale write in the insight service, where a refresh could publish an older picture after a
newer pass.

- `src/SmartX.Api/Patterns/SuggestionService.cs`
- `src/SmartX.Api/Patterns/InsightService.cs`
- `tests/SmartX.Tests/SuggestionServiceTests.cs`

**Commit:** `96c4320` fix(suggestions): take events from an inbox so ranking never meets the index lock

---

## U19. Compose would not start without an env file

**Source:** the final-pass fresh-clone check in Prompt 16: a clone with no `.env`, as a marker would have

**What was wrong.** `docker-compose.yml` required `SMARTX_ATTACHMENT_KEY` and stopped with "copy .env.example to .env" when it was
missing. `.env` is gitignored, so on a clone the first `docker compose up` always failed. It had only ever worked here because a
local `.env` existed.

**Why it mattered.** The brief asks for explicit instructions that work, and a marker's first command is the one they judge by.

**What changed.** Compose defaults the key to the development key already published in the README and `.env.example`. That adds no
exposure, since the key is already public, and the API already logs a warning when it is used outside Development. `.env` is now
optional, and the README quick start says so. Everything else in compose already had a default.

- `docker-compose.yml`
- `.env.example`

**Commit:** `ae8a4e1` fix(docker): start without a .env by defaulting the development key

---

## U20. Accessibility findings in the light theme

**Source:** one axe-core pass per route in the light theme (Playwright, Edge), in Prompt 16

**What was wrong.** Serious or critical: success and warning text failed 4.5:1 contrast on a light surface (the Open and active-alert
chips); accessible names did not contain the visible text on the trust rows (55) and the "Show decay window" buttons, which breaks
voice control; device rows were buttons with `role="listitem"` and `aria-pressed`, which ARIA does not allow. Moderate: no `<main>`
landmark, an unlabelled `<nav>`, zoom disabled in the viewport meta, a heading level skipped on the telemetry card, no `h1` on the
not-found page. The live regions for suggestions, insights and alerts were created together with their first item, so a screen reader
would not announce it, and pinned critical readings had no live region at all.

**Why it mattered.** Navigation and accessibility are assessed, and the live regions are how "new suggestions and alerts appear
without a reload" reaches someone who cannot see them.

**What changed.** Darker success and warning in the light palette only; names that mirror the visible text; the list item as a wrapper
around the button; a `<main>`, a nav label, zoom allowed; the heading fixes; and always-rendered `aria-live` wrappers (polite for
suggestions, insights and alerts, assertive for pinned critical readings, with `aria-relevant="additions"` so a ticking counter is not
re-read). The live stream of every reading stays `aria-live="off"` on purpose. Six tests hold the live regions, the name and the
roles in place. One finding is left: a second `<nav>` inside MudBlazor's own nav group (moderate, in the library's markup).

- `src/SmartX.Client/Theme/SmartXTheme.cs`
- `src/SmartX.Client/Layout/MainLayout.razor`, `NavMenu.razor`, `wwwroot/index.html`
- `src/SmartX.Client/Components/**` (the trust row, the device panel, the investigation card and the four live-region panels)
- `tests/SmartX.Tests/AccessibilityTests.cs`

**Commits:** `b62dccc` colour, `5c23e63` names and roles, `7b5e0fc` landmarks and zoom, `3bd34b4` live regions, `718d7e0` headings

---

## U21. The API had no authentication

**Source:** lecturer feedback on Part 1, criterion Gateway Startup Layout and API Integration, 5/10: "There is no authentication on the API at all, so add a login step and protect your sensitive endpoints with it in Part 2." It was the only criterion that lost marks.

**What was wrong.** Every endpoint was open. Anyone who could reach the API could read the whole mesh, register sensors, send readings, issue commands to actuators, change a device's scenario, upload and delete files and give verdicts. The simulator's calls were as open as an operator's, so anyone could also post fake readings as any device.

**Why it mattered.** The API controls physical actuators and decides what an operator believes about the mesh. Open to the network it is a way to shut valves and to lie about sensors, and a dashboard that anyone can write to cannot be trusted by the people reading it. The brief also asked for it, and the marks were lost for it.

**What changed.**

- **Operators log in.** `POST /api/auth/login` checks a password against a hash made with ASP.NET Core Identity's `PasswordHasher` and returns an HMAC-SHA256 JWT with the user's name, role and an expiry (an option, 60 minutes). A wrong username and a wrong password get the same message and about the same time, the endpoint is rate limited, and an account is locked after repeated failures (names that do not exist lock the same way). Two roles: **Viewer** reads everything; **Operator** also does everything that changes something.
- **Protection is by policy on groups.** Five named policies on four endpoint groups, with a default (`Console`) under which a read is a Viewer's and anything else an Operator's, so a new endpoint is safe whichever way it is written. Public: health and login only. A test reads the routing table and fails on any endpoint that lacks authorization, and others call every endpoint anonymously, as a Viewer and as an Operator.
- **Devices have their own keys.** A device's key is `HMAC-SHA256(fleetSecret, MAC)` (Azure IoT Hub's derived device keys), recomputed and never stored. A device may act only for its own MAC: a valid key used for another MAC is a 403. The simulator signs every call, registers each device with that device's own key (so no operator credential is ever in the simulator, which stands in for field hardware), and stops with one clear message if its secret is wrong instead of retrying for ever.
- **The hub needs a token.** Sent as `access_token` in the query string (a browser cannot put a header on a WebSocket) and read for the hub's path only; nginx's access log for `/hubs/` omits the query string; a connection closes when its token expires.
- **The client.** A login page; a session in `sessionStorage` (survives a refresh, ends with the tab); a `DelegatingHandler` that adds the token and ends the session on a 401; every page but login and not-found requires a user, with a return address; the app bar shows the name and role and a Log out. A Viewer sees the controls that change something disabled with "Operators only" beside them, and none of those calls is made. Attachment thumbnails, downloads and uploads, which an `<img>`, a link and a bare XHR cannot authenticate, go through the token.
- **Configuration.** Every secret, expiry, lockout and rate-limit value is an option. Development uses published values (like the attachment key), and the API warns at start-up for each one in use elsewhere; it refuses to start outside Development without real secrets. `.env` stays optional.
- **Tests.** The existing 1,188 tests run as an Operator, or as the device they play, with no assertion changed. 153 new ones cover login, lockout and rate limit, forged, expired and altered tokens, every endpoint's protection, a Viewer's 403 on every write, device keys and scope, the hub, the session, the token handler, route protection, the login page, the Viewer's disabled controls and the layout. Two mutations (an unprotected endpoint; a device scope that always allows) were each shown to fail the build.
- **Documentation.** An Authentication section in the README, and every `curl` in it, and in the two demo scripts, now sends the token.

Considered and not done: giving the simulator an Operator login to register (a person's credential inside hardware); a refresh token (more moving parts than a one-hour session needs here); putting the token in an httpOnly cookie (needs same-site deployment and CSRF handling). Limits, stated in the README: tokens cannot be revoked before they expire, users are in memory, and a token in `sessionStorage` can be read by script running in the page.

- `src/SmartX.Shared/Auth/`
- `src/SmartX.Api/Security/`, `Endpoints/AuthEndpoints.cs`, `Program.cs` and the endpoint files
- `src/SmartX.DeviceSimulator/GatewayClient.cs`, `DeviceKeyring.cs`
- `src/SmartX.Client/Auth/`, `Pages/Login.razor`, `Layout/MainLayout.razor` and the components with a control that changes something
- `src/SmartX.Client/nginx/default.conf`, `docker-compose.yml`, `scripts/`
- `tests/SmartX.Tests/AuthenticationTests.cs`, `EndpointProtectionTests.cs`, `DeviceAuthenticationTests.cs`, `ClientAuthTests.cs`, `ClientAuthComponentTests.cs`

**Commits:** `5b33a2b` shared contracts and device keys, `b845474` API login, roles and protected groups, `76355a3` nginx log, `3fcb356` simulator keys, `7920580` login page and session, `94be739` attachments through the token, `36d261e` Viewer controls, `fbe6bb3` compose, `1041e6b` README

---

## U22. History range queries walked from the oldest entry

**Source:** the Part 3 brief ("pulling all moisture readings between 14:00 and 16:00 for Node A without iterating through a massive, unorganised list"), and a limit this project had already measured and written down in the Part 2 README and in `SortedDictionaryHistoryStore`.

**What was wrong.** History sat in a `SortedDictionary` per node. A `SortedDictionary` cannot seek, so `GetRange` enumerated from the oldest entry and skipped until it reached `from`: O(n + k) for k results. A window near the start was cheap and a window near the end walked everything. `GetLatest` had the same fault and walked the whole history to keep the last few.

**Why it mattered.** The cost of a query grew with the amount of history and not with the amount asked for. At 1,000,000 entries a hundred readings from the middle of the history took about 3.8 ms and from the newest end about 8.7 ms. The Part 3 brief asks for exactly this query, and its rubric names a self-balancing tree for it.

**What changed.** A generic `AvlTree<TKey, TValue>` written from scratch (height stored in each node, four rotations, `Range`, `From`, `Floor`, `Ceiling`, `Min`, `Max`, reverse order, and `ValidateInvariants`), and `AvlHistoryStore`, which has the same layout, key (timestamp, then arrival sequence), lock and results as the old store. A range query descends to the first key at or after `from` and reads forward: O(log n + k). `GetLatest` reads backward from the maximum: O(log n + N). The existing `HistoryStoreContract` suite runs against the new store through one added subclass and was not edited. The AVL store is the default through `History:Store`; `SortedDictionary` stays selectable so the two can be compared. Two new endpoints: `GET /api/history/{mac}/range` (raw readings, paged, Viewer) and `GET /api/history/stats` (entries and tree height against ⌈log₂(n+1)⌉). Measured in Release at 1,000,000 entries, a hundred-reading window takes 1.3 to 1.9 µs on the tree wherever it sits, against 3,841 µs (middle) and 8,705 µs (newest) on the dictionary; the dictionary is level or marginally ahead only for the oldest window. Height at 1,000,000 entries is 20, the minimum possible. A red-black tree was considered and not used: it rotates less on insert, but history is read far more than it is rewritten, and the tighter balance of AVL gives shorter paths.

- `src/SmartX.Shared/Collections/AvlTree.cs`
- `src/SmartX.Api/History/AvlHistoryStore.cs`, `IHistoryStore.cs` (adds `GetStats`), `HistoryOptions.cs`, `SortedDictionaryHistoryStore.cs`
- `src/SmartX.Api/Endpoints/HistoryEndpoints.cs`, `src/SmartX.Shared/History/HistoryRangeContracts.cs`, `src/SmartX.Api/Program.cs`
- `tests/SmartX.Tests/AvlTreeTests.cs`, `AvlHistoryStoreTests.cs`, `HistoryRangeEndpointTests.cs`, `AvlHistoryBenchmarkTests.cs`

**Commits:** `6b706e9` the tree, `ea4e45c` the store and its configuration, `df974a5` the endpoints, `ecfefe0` the benchmark and README

---

## U23. The mesh was not modelled as a network

**Source:** the Part 3 brief, which asks for the IoT mesh as a graph, a traversal that finds what is connected and what is down, and the weak points; and a design note of our own: `DeploymentLocation.NodeId` called a device's gateway a "mesh node", but nothing recorded which devices could hear which.

**What was wrong.** The gateway knew each device's zone and a label for the node it reported through, and nothing about links. It could not say which devices were reachable, what route a packet would take, or which device's failure would strand others. A device that failed was found by its silence, with no view of what that did to its neighbours.

**Why it mattered.** The question an operator asks of a mesh is "what depends on this device?", and the data to answer it did not exist. It is also the substance of Part 3: without a graph there is nothing for a traversal or a spanning tree to run on.

**What changed.** Devices report their neighbour table (and their position) to `POST /api/devices/{mac}/neighbours` every 30 seconds, signed with their own key like telemetry, and a device may report only for its own MAC. The simulator lays the fleet out on a plan from a fixed seed (three gateways, each device hearing its two to four nearest neighbours and a gateway in range, zones joined by single links so some single points of failure remain) and reports the part of that plan that is transmitting: a device in Disconnect stops reporting and leaves other devices' tables. `MeshGraph` is an adjacency list built from those reports. A link exists if either end reports it and uses the weaker reading; its cost is `rssiCost × (1 + penaltyFactor × (1 − lowerTrust))`. Devices in the Fault band, or silent for three intervals, are left out. The API keeps an immutable snapshot, rebuilt at the next read after reports change or after two seconds, so traversals take no lock. A multi-source breadth-first search from the gateways gives each device its nearest gateway and hop count (O(V + E)), and Tarjan's low-link search finds the articulation points (O(V + E)), each with the devices it would cut off. Both are checked against brute force on 200 random graphs. Three read endpoints under the Viewer policy: `/api/topology`, `/api/topology/reachability`, `/api/topology/articulation-points`. In the seeded layout: 58 vertices, 141 links, 7 articulation points. Run in Docker with no `.env`, 50 devices and 3 gateways are live in one connected component with none cut off, and a device set to Disconnect leaves the graph after three report intervals. Considered and not done: requiring both ends to report a link (a device that misses one report would drop out of its neighbours' routes), and a lock around the mutable table (a slow traversal would hold up every report).

- `src/SmartX.Shared/Topology/` (`MeshGraph.cs`, `LinkCost.cs`, `MeshTraversal.cs`, `MeshLayout.cs`, `TopologyContracts.cs`)
- `src/SmartX.Api/Topology/`, `src/SmartX.Api/Endpoints/TopologyEndpoints.cs`, `Program.cs`, `appsettings.json`
- `src/SmartX.DeviceSimulator/DeviceFleet.cs`, `GatewayClient.cs`, `SimulatedDevice.cs`, `SimulatorOptions.cs`, `docker-compose.yml`
- `tests/SmartX.Tests/TopologyTests.cs`

**Commits:** `e52b330` the graph, cost, traversals and layout, `ce6c806` reports, snapshot and endpoints, `39d0dac` tests

---

## U24. The backbone of the mesh, and what happens when part of it fails

**Source:** the Part 3 brief ("implement Prim's or Kruskal's algorithm to calculate the Minimum Spanning Tree of the network ... the most power-efficient routing path for telemetry data to hop through the mesh nodes to reach the central server"), and the graph built in U23, which could say what was connected but not how to connect it cheaply.

**What was wrong.** The mesh graph answered "what can reach a gateway" (breadth-first) and "what can we not lose" (articulation points), but it had no notion of a route: nothing chose which links to use, so nothing said how much it costs to keep the network connected, or what a device's path to its gateway is. When a device failed, the graph was rebuilt without it and nothing recorded what that did to anyone's route.

**Why it mattered.** Choosing links is the part of mesh routing the brief marks; and "the mesh heals itself" is a claim that needs something to show for it.

**What changed.** `MeshSpanning` (from scratch, in `SmartX.Shared`): Prim's algorithm with a lazy `PriorityQueue` that skips stale entries (O(E log E)); several gateways handled by a virtual root joined to each at cost 0, run from, then dropped, which gives one tree per gateway; Kruskal's algorithm with a `DisjointSet` (path compression and union by rank) as the independent check; and Dijkstra's algorithm, run from all gateways at once, for the cheapest cost of each device on its own. Devices with no path to a gateway (U23's cut-off list) are left out of both trees. A device is routed along the tree, not by Dijkstra's, because the tree is the backbone: it minimises the cost of the whole network, so a device's route through it can cost more than its own best, and the endpoint returns both. The spanning forest belongs to the topology snapshot, so it cannot disagree with the graph it is shown beside. Self-healing is a record and not a mechanism, because routes are recomputed from each snapshot anyway: each new snapshot is compared with the last, and a device whose route has broken (a device left the graph, or a link was lost) gets a route change with the old and new path and a reason such as `28 excluded: Stale`, kept in a ring buffer of 200. A timer rebuilds the snapshot every 5 seconds so a change is noticed when it happens and not at the next read. New: `GET /api/topology/mst`, `GET /api/topology/route/{mac}`, `GET /api/topology/route-changes`, all under the Viewer policy. In the seeded layout the tree weighs 74.95 over 55 links, Prim's and Kruskal's agree, and 29 of 55 devices have a tree route dearer than their shortest path.

The first version recorded every change of path, and in Docker produced 79 route changes in 40 seconds on a healthy mesh: trust scores move all the time, so the tree flipped between near-equal routes with every wobble, and every flip was "a link on the route changed". It now records a change only when the old route no longer holds, and nothing in the first 30 seconds while the fleet's reports are still arriving. Same mesh afterwards: 11 changes in the minute around a real failure, every one of them caused by a device that really had left (ten by the device taken off the air, one by a device that fell into Fault). Checked in Docker with no `.env` and a 5-second report interval: a device on eight routes (not an articulation point) set to Disconnect, and about 14 seconds later nine route changes were recorded (the device's own and eight of the routes that had passed through it), each with the reason `excluded: Stale` and none of the new routes going through it.

- `src/SmartX.Shared/Topology/MeshSpanning.cs`, `TopologyContracts.cs`, `MeshLayout.cs` (now uses `DisjointSet`)
- `src/SmartX.Api/Topology/TopologyService.cs`, `TopologyRefreshService.cs`, `TopologyOptions.cs`, `src/SmartX.Api/Endpoints/TopologyEndpoints.cs`, `Program.cs`, `appsettings.json`
- `tests/SmartX.Tests/MeshRoutingTests.cs`

**Commits:** `380db8a` the algorithms, `69fed7a` routes, the route record and the endpoints, `25d9b52` tests, `4a15b8a` record a change only when the old route no longer holds

---

## U25. The test suite had redundant and slow tests

**Source:** the test audit written at the end of Prompt 19 (`TEST_AUDIT.md`), which sorted all 1,429 tests by whether anything else proved the same thing and by how long they took.

**What was wrong.** Three kinds of waste. Duplicates: HTTP tests that repeated what a unit test of the same logic already proved more precisely (device search filters tested through the endpoint and again against the index, timeline downsampling tested through the endpoint and again against the builder). Near-duplicates: four tests of one record's equality, six that each asserted "mixing units throws" for a different operator. And Theory rows that each started a whole `WebApplicationFactory` host (about a second each) to ask one question: fourteen of them in the device search tests alone, eleven in the token tests.

**Why it mattered.** The suite is run before every merge, and its time went mostly on starting hosts. A duplicate test also costs when behaviour changes on purpose: it has to be found and updated twice.

**What changed.** Test files only, apart from one parameter. Five search, five telemetry, three history, one alert, one end-to-end and one command-service test removed, each after reading the test that covers it; two of the audit's claims did not hold and were kept or corrected (see `TEST_AUDIT.md`). Near-duplicates collapsed (4 into 2, 2 into 1, 6 into 2, and two folds in the login tests). Theory rows that start their own host became one test on one host looping over the cases, with an assertion message naming the failing case, in eleven classes; theories of pure functions were left alone, because they cost nothing and show exactly which case broke. The login throttle's table cap became a constructor parameter (default unchanged) so its flood test could use 100 names and not 10,000: it took 3.1 seconds because each new name past the cap scanned the whole table, and takes 13 milliseconds. **1,429 tests became 1,347 and the full run went from 26 seconds to 20.** The contract suites, the endpoint-protection test and the randomised comparisons were not touched.

- `tests/SmartX.Tests/` (about twenty files), `src/SmartX.Api/Security/LoginThrottle.cs` (the cap)

**Commits:** `4eeb56a` removals, `5733243` near-duplicates, `2a47ae4` and `89d1fa1` theories on one host, `16c9582` throttle cap and sampling intervals

---

## Entry template

```markdown
## U26. Short title of the problem

**Source:** how it was found: lecturer feedback on Part 1, code review, a test, a live run

**What was wrong.** The behaviour or code as it was, specifically enough to find it.

**Why it mattered.** The consequence: incorrect output, a cost that grows, a rubric criterion, a risk.

**What changed.** The fix, and any alternative that was considered and rejected.

- `path/to/changed/file.cs`

**Commit:** `abc1234` type(scope): message
```
