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

## Entry template

```markdown
## U7. Short title of the problem

**Source:** where the feedback came from (lecturer feedback on Part 1, code review, testing)

**What was wrong.** The behaviour or code as it was, specifically enough to find it.

**Why it mattered.** The consequence: incorrect output, a cost that grows, a rubric criterion, a risk.

**What changed.** The fix, and any alternative that was considered and rejected.

- `path/to/changed/file.cs`

**Commit:** `abc1234` type(scope): message
```
