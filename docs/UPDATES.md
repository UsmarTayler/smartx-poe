# Update ledger

Changes made to Smart-X in response to feedback and review, oldest first. Each entry records what was
wrong, why it mattered, what changed, and the commit where it happened, so any change can be traced back
to the reason for it and checked with `git show <commit>`.

New entries are appended at the end using the [template](#entry-template). Part 3 asks for a list of
updates made from Parts 1 and 2 feedback. This file is that list, kept while the work happens instead of
reconstructed afterwards.

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

## Entry template

```markdown
## U13. Short title of the problem

**Source:** where the feedback came from (lecturer feedback on Part 1, code review, testing)

**What was wrong.** The behaviour or code as it was, specifically enough to find it.

**Why it mattered.** The consequence: incorrect output, a cost that grows, a rubric criterion, a risk.

**What changed.** The fix, and any alternative that was considered and rejected.

- `path/to/changed/file.cs`

**Commit:** `abc1234` type(scope): message
```
