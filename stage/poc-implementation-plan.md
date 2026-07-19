# Proof of Concept — Implementation Plan
## Falcon Tool Fabric (`stage\` design)

> **Purpose:** Prove the four load-bearing claims of the stage design at minimum cost — enough
> for the engineering lead / CTO to fund Wave 0 with confidence.
>
> **This is not Wave 0.** The design is DESIGN READY ([executive-summary.md](stage/executive-summary.md));
> the PoC answers whether the hardest implementation bets hold before money is spent on the full
> foundation. Wave 0 starts *after* the PoC gates pass.
>
> **Code lives in a standalone sandbox solution, not `C:\CamtekGit`.** Suggested path:
> `C:\poc-falcon-bus\`. Throwaway — do not submit to the monorepo.

---

## 1. The four claims the PoC must prove

The business case in [00-context-and-case.md](stage/00-context-and-case.md) rests on four
verifiable engineering claims. The PoC gates are wired directly to the assertion numbers from
[06 §6.10](stage/06-bus-implementation.md) — no invented acceptance criteria.

| # | Claim | Pass gate | Source assertion |
|---|-------|-----------|-----------------|
| C-1 | Publish ≤ 1 ms at p99.9 under disk co-load | Measured p99.9 ≤ 1 ms with 200 ms flush-delay injection active | **Assertion 1** |
| C-2 | Zero silent loss — class A survives any single crash point | `count == published AND distinct-count == published` across all four crash scenarios | **Assertion 5** |
| C-3 | External-sink outage fills the WAL; publish never drops at the sink hop | WAL at quota → withhold DELIVER_ACK → NACK flows back to journal → drain resumes cleanly below watermark; `count == published` after recovery | **Assertion 5b** |
| C-4 | Broker + client throughput headroom sufficient for the design load model | 1 000 msg/1 s burst drains in < 30 s (T-L2); record raw broker/client msg/s as a baseline number | **T-L2** |

> **Note on C-2:** assertion 5 is the nightly-tier test, not the PR-tier (assertions 1–4).
> Do not stop at PR-tier alone — C-2 is the single most important thing the PoC exists to demonstrate.

**Go / no-go** (§6): all four gates must pass. A failing gate names the exact design section
it challenges; do not fund Wave 0 past that gate until the section is revised.

---

## 2. What to build — one vertical slice

Build only the **SYS-1 flow** ([01-system-architecture.md §1.2](stage/01-system-architecture.md)):

```
Stub scanner ──publish scan.committed──► Publisher journal
                                              │
                                        Broker (named pipe)
                                              │
                                      Gateway BusSource
                                              │
                                        WAL spool (fsync)
                                              │ (async)
                                        Stub sink (in-memory counter)
```

This single flow exercises all four claims simultaneously and mirrors the exact path the
business case sells. Component-by-component builds are slower and less convincing.

---

## 3. The five components

### 3.1 `Camtek.Messaging.Contracts` (project: `poc.Messaging.Contracts`)

Start from [codeSnippets/01-bus-contracts.cs](stage/codeSnippets/01-bus-contracts.cs) —
sketch defects S-1..S-18 are already applied per [00-README.md](stage/codeSnippets/00-README.md).

Deliver: `BusEnvelope`, `Topic` (with `DurabilityClass`, `DurableSubscribers`, `PayloadBudgetBytes`),
`DurabilityClass` enum, `BusHealth`, `IBusCounters`, and the payload type
`ScanCommittedPayload` (paths/IDs only — no file content, per §1.4 payload contract).

One topic only: `scan.committed` (class A, one declared durable subscriber `ToolGateway`).

---

### 3.2 `Camtek.Messaging` client (project: `poc.Messaging`)

Start from [02-bus-client-api.cs](stage/codeSnippets/02-bus-client-api.cs) and
[03-bus-client-internals.cs](stage/codeSnippets/03-bus-client-internals.cs).

Must-implement internals:

| Internal | Why it's non-negotiable for the PoC |
|----------|-------------------------------------|
| Lock-free enqueue on `Publish` | C-1 (≤ 1 ms bound) |
| Journal-writer thread — single toucher, group-commit | C-1 + C-2 (durability under disk co-load) |
| `SourceEpoch` persisted next to journal; seq-sidecar | C-2 (dedup identity survives restart — §6.3) |
| Reconnect replay: `max(seq-sidecar, journal seq)` | C-2 (no regression after compaction) |
| Priority send queue (A > B/C; no starvation) | C-4 |
| NACK handling → stay in journal, redeliver on RESUME | C-3 |

Skip for PoC: storm control token bucket, per-topic payload budget enforcement,
`RequestAsync` / `Serve` R-R (no commands in the PoC slice).

---

### 3.3 `Camtek.Messaging.Broker` (project: `poc.Messaging.Broker`)

Start from [04-broker.cs](stage/codeSnippets/04-broker.cs).

Must-implement:

| Feature | Why |
|---------|-----|
| Named-pipe server, one pipe per client | C-1 / C-2 |
| HELLO → register subscriptions | C-2 |
| PUB / PUB_ACK + DELIVER / DELIVER_ACK frame flow | C-2 |
| E2E_ACK → trigger journal ack-tombstone | C-2 |
| NACK (queue full) / RESUME (queue drained) | C-3 |
| Class-A bounded queue, default 128 per subscriber | C-3 / C-4 |
| Split reader/writer tasks per connection | C-4 (no duplex deadlock) |
| Priority write lanes (A > B/C) | C-4 |
| PING / PONG priority-dequeued | needed to detect hung vs degraded |

Skip for PoC: OS-account publish ACL enforcement (use a deny-open stub — note the known
security gap explicitly), class-B retained slots, class-C drop queues, duplicate-identity
HELLO supersede logic. The `quarantine: never` restart policy is exercised by the TestKit
harness, not the broker itself.

---

### 3.4 `Camtek.Messaging.TestKit` (project: `poc.Messaging.TestKit`)

**Build from the §6.10 spec** ([06-bus-implementation.md](stage/06-bus-implementation.md));
no sketch exists for this component.

The TestKit is what *runs* the four gate assertions — it is not optional.

```csharp
// Minimum viable TestKit for the PoC gates
class BusHarness
{
    IBus Bus { get; }                   // in-proc fake or real client, same IBus contract

    void InjectBrokerDown();            // assertion 1, 5 (crash scenario 1)
    void InjectDiskDelay(int ms);       // assertion 1 (200 ms flush-delay injection)
    void InjectSlowSubscriber(Topic t); // assertion 4
    void CrashPublisher();              // assertion 5 (crash scenario 2)
    Task RestartAsync();                // replays journal on reconnect
    void AdvanceClock(TimeSpan t);      // Ttl gate tests (not needed for PoC gates, nice to have)
}

class TopicCaptor<T>
{
    IReadOnlyList<BusMessage<T>> Received { get; }
    Task AwaitCount(int n, TimeSpan timeout);
    void AssertNoDuplicateSideEffects();   // distinct-count == published
    void AssertCount(int expected);        // count == published
}

class FaultScript
{
    FaultScript At(FaultPoint step, Fault fault); // composable crash matrix
    Task RunAsync(Scenario scenario);             // drives assertions 5, 5b
}

enum FaultPoint
{
    BrokerDown,                              // scenario: broker kill + restart under live traffic
    GatewayDownAtPub,                        // scenario 1 — §6.10 assertion 5
    GatewayCrashWith128Queued,               // scenario 2 — stresses bounded class-A queue mid-backpressure
    GatewayDownBetweenDeliverAckAndSinkPersist, // scenario 3 — the durability hand-off gap
    PublisherCrash,                          // scenario 4 — publisher restart + journal replay
}
```

The harness must support killing and restarting real broker and gateway *processes*
(not just in-proc fakes) for assertions 5 and 5b — in-proc is not sufficient for the
process-boundary crash scenarios.

---

### 3.5 Gateway `BusSource` + WAL stub (project: `poc.Gateway.BusSource`)

Start from [12-gateway-additions.cs](stage/codeSnippets/12-gateway-additions.cs).

**Critical fix required before building:** sketch 12 carries an explicit
`TODO(R-4/X7-1..X7-3)` — the `OnScanCommitted` handler is ack-coupled to sink routing
(waits for Fleet delivery before sending DELIVER_ACK). This is the design's **old wrong
behavior**, not the correct one. The normative contract is
[07-toolconnect-design.md §7.4–7.5](stage/07-toolconnect-design.md):

> DELIVER_ACK is a function of the WAL append **only**. Sink routing is async from WAL.

Build the correct sequence from doc 07, not from sketch 12's stub:

```
receive DELIVER → append to WAL (tmp → fsync → rename) → send DELIVER_ACK
                                  ↓ (async, off the ack path)
                         route WAL entry → stub sink
```

WAL for PoC: flat per-entry files in a temp directory; no full per-sink state machine needed
beyond `pending → done`. The poison-vs-outage split, drain arbitration, and quota enforcement
(5b) are required for gate C-3.

Stub sink: an in-memory `ConcurrentQueue<ScanCommittedPayload>` with a configurable outage
flag. Sufficient to count delivered-vs-published and to simulate a down sink.

---

## 4. Explicit exclusions

These are out of scope for the PoC. Do not implement them.

| Excluded | When it matters |
|----------|-----------------|
| Pipe ACLs + OS-account publish authorization | Wave 0 security work-stream (§6.8, pre-P1a entry criterion) |
| mTLS on `:5007` + signed manifest | Same work-stream |
| `CommandPublisher` `:5007` + CMM proxy | Wave 2 |
| GEM shim + degraded contract | P4/P5 scope |
| Full `ToolHost` supervisor (job objects, SCM registration, `quarantine: never` restart loop) | Wave 0 foundation build; PoC uses a test-harness kill/restart instead |
| `ToolServices` host (`:5060`) | Wave 2 + trigger-gated |
| Real Fleet/TSMC sinks | Replaced by in-memory stub counters |
| Class-B retained slots and class-C queues | Only class A is needed for SYS-1 |
| `net48` dual-target | **Known gap to record:** AOI_Main loads the net48 build; the PoC proves the net8 build only. Wave 0 must target `net48;net8.0` and validate both bitnesses (R-TS-1). |
| FlaUI external-behavior suite, GEM record-replay | Wave-0 gate criteria, not PoC |
| Full 14-assertion TestKit (assertions 2, 6–14 in full) | The PoC runs assertions 1, 3, 4, 5, 5b, and T-L2 only (T-L3 requires the storm-control token bucket, excluded above) |

---

## 5. Solution structure

```
C:\poc-falcon-bus\
  poc.sln
  src\
    poc.Messaging.Contracts\        ← from sketch 01
    poc.Messaging\                  ← from sketches 02–03
    poc.Messaging.Broker\           ← from sketch 04
    poc.Messaging.TestKit\          ← from §6.10 spec (no sketch)
    poc.Gateway.BusSource\          ← from doc 07 §7.4–7.5 (fixing sketch 12 TODO(R-4))
    poc.Simulator\                  ← stub scanner publisher + stub Fleet sink
  tests\
    poc.Gates\                      ← xUnit; runs the four gate assertions
```

All projects target **net8.0 only**. No references to `C:\CamtekGit`. Dependencies:
`Newtonsoft.Json` (repo-standard) and `System.Threading.Channels`.

---

## 6. Milestones

> **Schedule risk — M1/M2:** Sketch 03 (`bus-client-internals.cs`) uses `// …` stubs for the
> unspecified machinery. The journal-writer thread, group-commit, and `max(seq-sidecar, journal seq)`
> reconnect replay are build-from-scratch work, not hardenings of the sketch. These are the
> concurrency paths that C-1 and C-2 test directly. If M1 slips, do not compress M2 — the
> journal internals are the highest-risk code and must be correctness-reviewed before the
> crash-scenario matrix runs.

| Milestone | Deliverable | Target |
|-----------|-------------|--------|
| **M0** | Solution scaffolded; Contracts project builds; `BusEnvelope` round-trips to JSON | Day 3 |
| **M1** | Client + broker: `IBus.Publish` → `Received` in a subscriber in the same process. Gate **C-1** (≤ 1 ms, assertion 1) passes | Week 2 |
| **M2** | Reconnect + journal replay: broker kill → restart → subscriber catches up. Assertion 3 (dedup) and assertion 4 (slow-subscriber isolation) pass | Week 3 |
| **M3** | Gateway `BusSource` with WAL-before-ACK wired. SYS-1 flow runs end-to-end: stub scanner → broker → WAL → stub sink | Week 4 |
| **M4** | TestKit `FaultScript` drives all four crash scenarios. Gate **C-2** (assertion 5, count + distinct-count) passes | Week 5 |
| **M5** | WAL quota withhold implemented. Gate **C-3** (assertion 5b) passes. T-L2 + T-L3 headroom run. Gate **C-4** recorded | Week 6 |

---

## 7. Go / no-go decision

After M5, evaluate each gate:

| Gate | Result | Action |
|------|--------|--------|
| C-1 ✅ | p99.9 ≤ 1 ms | Proceed |
| C-1 ❌ | p99.9 > 1 ms | Revisit journal group-commit interval and enqueue path before Wave 0 |
| C-2 ✅ | count = distinct = published across all 4 crashes | Proceed |
| C-2 ❌ | Loss or duplicate found | Identify which crash scenario fails; revise the WAL lifecycle in §7.4 before Wave 0 |
| C-3 ✅ | No drop at sink hop under quota | Proceed |
| C-3 ❌ | Drop found | Revise withhold/NACK path; check in-flight window invariant (§6.9) |
| C-4 ✅ | T-L2 burst drains < 30 s; raw broker/client msg/s recorded | Proceed |
| C-4 ❌ | Burst drain > 30 s or anomalous memory growth | Review broker queue sizing and priority lanes before sizing Wave 0 load model |

**All four must pass.** Partial passes do not unlock Wave 0 funding — they unlock targeted
re-design on the failing section only.

---

## 8. What the PoC does NOT prove (and Wave 0 must)

The following are explicitly deferred and **must be validated before or during Wave 0**,
not assumed from the PoC result:

1. **`net48` client build + both bitnesses** — AOI_Main loads the net48 client; the PoC
   only proves net8. Wave 0 must compile `Camtek.Messaging` for `net48;net8.0` and run
   assertion 1 on the net48 build in-proc with AOI.
2. **OS-account publish ACLs** — PoC runs with ACLs open. The security work-stream
   (named owner required as pre-P0 criterion) closes this.
3. **Full shadow comparator qualification** — the P1a dual-run safety net is a Wave-0
   exit criterion, not a PoC concern.
4. **P0 measurements with acceptance bounds** — group-commit interval under co-load,
   GEM pre-Ttl hop latencies, FleetSink ≥ 60 msg/s ceiling, TsmcSink ≤ 8.5 s/wafer.
   The PoC's C-4 is a sanity ceiling, not a substitute for the full P0 measurement
   campaign (§5.2 Wave 0).
5. ~~**Multi-PC topology census (A-1)**~~ — **ANSWERED: single-PC only** (all bus-relevant processes on one machine; multi-PC is not in scope). P2+ is no longer blocked on this question.

---

*Document date: 2026-07-19. Design basis: `stage\` DESIGN READY (7 adversarial cycles).
Normative references: [06-bus-implementation.md](stage/06-bus-implementation.md),
[07-toolconnect-design.md](stage/07-toolconnect-design.md), [01-system-architecture.md](stage/01-system-architecture.md).*
