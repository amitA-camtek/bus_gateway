# 6 — Bus Implementation Specification (`Camtek.Messaging`)

> Level: **implementation**. The complete build spec for the fabric: projects, API, envelope, wire protocol, journal mechanics, broker algorithms, request/reply, security, load model, and the test kit. This is the deepest document of the set — everything an implementer needs.
> Up-links: where the bus sits → [01-system-architecture.md](01-system-architecture.md); AOI usage → [02-aoi-architecture.md](02-aoi-architecture.md); adoption method → [03-appendix-four-lanes.md](03-appendix-four-lanes.md).
> Incorporates all resolutions of the concurrency/connectivity/load review (Revision 3).

---

## 6.1 Projects & packaging

```
Sources\Messaging\
  Camtek.Messaging\           net48;net8.0  — client: API, send queue, journal-writer thread,
                                              pump (split I/O), dedup, request/reply, storm control
  Camtek.Messaging.Contracts\ net48;net8.0  — envelope, topic registry, payload DTOs (no logic)
  Camtek.Messaging.Broker\    net8.0        — broker host (console; ToolHost child, startOrder 0)
  Camtek.Messaging.TestKit\   net48;net8.0  — contract-test base + fault-injection harness
  Camtek.Messaging.Tap\       net8.0        — bus-tap recorder CLI (diagnostics)
  Camtek.Messaging.Tests\     net8.0        — unit + protocol tests (xUnit + FakeItEasy)
```

Delivery: binary drops to `c:\bis\bin` **and** `c:\bis\bin\x64` (AOI builds both bitnesses); broker + net8 pieces deploy with ToolHost. Dependencies: `Newtonsoft.Json` (repo-standard) + `System.Threading.Channels` (netstandard2.0 — approval item). Native client `camtek_bus.dll` (flat C over the same protocol) is a later deliverable for machine-layer/DDS adoption — the protocol below is deliberately implementable in ~100 lines of C.

## 6.2 Public API

```csharp
public interface IBus : IDisposable
{
    // ≤1 ms ALWAYS: lock-free enqueue; class-A journaling happens on the
    // library's journal-writer thread BEFORE the pump sends (never on the caller).
    void Publish<T>(Topic topic, T payload, PublishOptions options = null);

    // Handlers run on pool threads; STA/UI marshaling is the HOST's job.
    ISubscription Subscribe<T>(Topic topic, Func<BusMessage<T>, Task> handler,
                               SubscribeOptions options = null);

    // Commands. Ttl mandatory; requester-side deadline mandatory (never wait bare).
    Task<Reply> RequestAsync<T>(Topic topic, T payload, TimeSpan ttl,
                                CancellationToken ct = default);
    ISubscription Serve<T>(Topic topic, Func<BusMessage<T>, Task<Reply>> handler);

    BusHealth Health { get; }        // connected, heartbeat age, queue depths, journal backlog
    IBusCounters Counters { get; }   // per-topic published/acked/delivered/dropped/dead-lettered
}
public static class BusFactory
{
    // NON-blocking: returns immediately, background jittered-backoff retry (infinite,
    // alarm after T). Subscriptions registered locally, replayed on (re)connect.
    public static IBus Connect(string sourceName, BusConfig config = null);
}
```

Topics are **declared, not stringly-typed** — durability class, payload type, publish ACL, and storm control are compile-time properties of the topic (registry example: [03-appendix-four-lanes.md](03-appendix-four-lanes.md) lane A).

## 6.3 Envelope (JSON v1 — field-readable at 3 am; protobuf later per-topic opt-in)

```json
{
  "messageId":   "0193f2a1-...",                 // GUID — R-R dedup key
  "topic":       "scan.committed",
  "correlationId": "wafer-BH01-20260718-0042",   // UnifiedLogger-aligned tracing
  "moduleId":    "frmScanTab",
  "source":      "AOI_Main",                     // identity from HELLO
  "seq":         18734,                          // per-source monotonic — ordering, loss, dedup
  "timestampUtc":"2026-07-18T11:02:03.412Z",
  "schemaVersion": 1,                            // additive-only; ignore-unknown (mixed versions
  "ttlMs":       null,                           //   are the DESIGNED steady state under ToolHost)
  "attempts":    0,                              // poison detection
  "payloadType": "ScanCommittedPayload",
  "payload":     { "...": "..." }
}
```

Frame cap **1 MB** — the bus carries *pointers* (paths / ids), never bulk data.

## 6.4 Wire protocol

Transport: one duplex Windows named pipe per process (`\\.\pipe\camtek.bus`), localhost only, **pipe-ACL authenticated** (identity per connection → per-topic publish ACLs for free). Framing: 4-byte length prefix + UTF-8 JSON.

| Frame | Purpose |
|---|---|
| `HELLO` | identity, subscriptions, per-class-A-topic publisher replay start (`resumeFromSeq` — publisher-declared; broker is stateless and never serves history) |
| `PUB` / `PUB_ACK` | publish / broker enqueued to all matched subscriber queues (sufficient for B/C) |
| `DELIVER` / `DELIVER_ACK` | delivery / subscriber processed (class A — sent only after durable ownership, e.g. the gateway's WAL append) |
| `E2E_ACK` | class-A end-to-end confirmation **per (message, subscriber-set snapshotted at PUB)** → journal appends the ack-tombstone |
| `NACK` / `RESUME` | class-A queue full → stays in publisher journal; redelivered on exponential backoff+jitter, seq order, bounded in-flight window; `RESUME` (queue below low-watermark) short-circuits |
| `REQ` / `REPLY` | commands (`requestId`, `ttlMs`); broker R-R queue-full → immediate `REPLY(rejected-busy)` |
| `PING` / `PONG` | heartbeat, **priority-dequeued**; self-check reports **measured loop lag** (degraded vs hung distinction) |

**I/O model:** reader and writer **split** (overlapped I/O) on both ends — a peer must always drain reads regardless of write progress (kills the duplex write-write deadlock). **Priority lanes** on every send queue and per-connection broker writer: `REQ/REPLY` > A > B > C (weighted — no total starvation). Per-frame write deadlines: client reconnects, broker disconnects the subscriber.

## 6.5 Client internals

### The publish path (the ≤1 ms bound + class-A durability)

```
caller thread:        enqueue(envelope)  ──►  returns ≤1 ms. No disk. No socket. No lock across I/O.
journal-writer thread: append batch ──► ONE group-commit flush per batch / per X ms
                       ──► release seq to pump (class A sends only after durable)
pump writer:           PUB ──► broker
pump reader:           E2E_ACK ──► enqueue Acked(seq) to journal thread ──► append {"ack":seq}
```

- **Single-writer journal**: only the journal thread touches journal files; "delete" = an appended **ack-tombstone** (append-only, crash-safe; replay = entries minus tombstones in seq order). Compaction: survivors → tmp → flush → atomic `ReplaceFile`; no racing appender exists by construction.
- **Durability contract (honest):** durable against **process crash immediately** (page cache survives process death); durable against **power loss within the group-commit interval ≤ X ms** (P0-measured under co-load) — acceptable because scan results also exist at their stable path.
- **Journal caps per topic** (default 100k entries / 256 MB, alarm at 50%): `scan.committed` = refuse-new + loud alarm at cap; error telemetry = drop+count beyond cap. **A journal failure never throws to the caller** (counted error + alarm). Journals/spool live on the system volume, **separate from tile/zip data**.
- **Reconnect algorithm:** replay journal strictly in seq order to recorded high-water H → then drain the live queue discarding class-A ≤ H → per-source FIFO holds; the caller never pauses. Reconnect backoff is jittered; replay paced by broker credit.
- **Dispatcher duties:** seq-contiguity dedup per (source, topic) — O(1), immune to replay-burst size (`messageId` LRU only as the R-R secondary net); two-stage Ttl gates on a **monotonic clock**; catch boundary (a handler exception never kills the process); poison → dead-letter file after N attempts + alarm; reply cache = atomic insert-or-get of an in-progress placeholder (concurrent redelivery awaits the same completion — no double execution); late REPLYs counted, never a fault.
- **Storm control** (topic-contract, in the library): error-class telemetry coalesced by `(source, errorCode)` window (first immediate, summaries every 10 s) + token bucket (10/s sustained, 100 burst).

## 6.6 Broker internals

- **Connection manager:** pipe server; identity = authenticated account + `HELLO.sourceName`; publish-ACL enforcement; **per-connection outbound writer task** with write deadline (a suspended subscriber is disconnected, never allowed to stall siblings).
- **Class-A queues** (bounded, default 128 = 2× worst burst): full → `NACK`; drained → `RESUME`. E2E-ack tracking bounded by Σ queue capacities; a disconnecting/unsubscribing subscriber leaves every pending set (its durability claim ends with registration); publisher disconnect purges its routing entries; **zero-durable-subscriber publish acks immediately** (no journal leak on gateway-disabled tools).
- **Class-B**: locked keyed-slot coalesce with atomic dequeue-marks-consumed (a naive replace-in-channel loses updates); **retained** — last value per (topic, key) delivered to every new subscriber on subscribe.
- **Class-C**: drop-oldest + counted; drop counters alarmed.
- **Heartbeat/health:** PING priority-dequeued; loop-lag self-check via a pipe-frame probe (a new ToolHost probe type); counters **pushed** to ToolHost each heartbeat (survive broker death).
- **Supervision:** ToolHost child, `startOrder: 0`, `quarantine: never`, `priorityClass: AboveNormal`; broker updates are maintenance-window-only.

## 6.7 Request/reply protocol (commands)

Reply = **ACCEPTED on successful post to the executing dispatcher** — never completion, never gated on execution. The **final Ttl gate runs as the first statement of the marshaled delegate on the executing thread**; expired-at-dequeue → command-expired event + the consumer's per-command compensation. Requester-side deadline mandatory. Residual window (host-told-accepted / command-expired) is compensated and documented — it cannot be zero. Ttl derives from per-site E30 timeout config minus a measured margin (GEM pre-/post-hops measured at P0).

## 6.8 Security

Pipe ACLs restricted to the ToolHost service account + the AOI user; per-topic publish ACLs (`*.commands` = GEM shim + gateway CommandPublisher only); no internal TCP listener exists; the gateway REST diagnostic surface may publish non-command topics only; command publishes and ACL rejections audited with `correlationId`.

## 6.9 Load model & sizing (normative)

| Traffic | Nominal | Burst | Storm (post-coalescing) |
|---|---|---|---|
| Per-wafer events (~25–40 msgs/wafer @ 60 wph) | ~0.5–1 msg/s | ~50 msgs / 2 s | — |
| `tool.state` / carrier | ~10/day | — | — |
| Error telemetry (class A) | ~0 | — | capped 10/s per source |
| Future `dds.frame.*` tier | — | — | class C only, own ring |

Derived: broker class-A queue 128; journal 100k/256 MB (alarm 50%); gateway channel 1000 (~30 min burst absorption); dedup OOO window 64; replay in-flight window 32. P0 publishes measured **single-instance ceilings** (broker msg/s + MB/s at p99, batch-fsync/s under co-load, FleetSink msg/s, TsmcSink wafers/h) — scale-out is a documented non-requirement with an expiry condition. Fleet-side herd control: jittered registration + drain start (0–120 s) + per-tool drain caps.

## 6.10 Test kit (no edge migrates without it)

| # | Assertion |
|---|---|
| 1 | Publish ≤1 ms p99.9 under broker-down / slow / hung **and disk co-load** (200 ms flush-delay injection) |
| 2 | Per-source FIFO per topic — including across reconnect replay; seq gaps counted |
| 3 | Duplicates absorbed (seq-contiguity) — side-effects once, including replay bursts larger than any cache |
| 4 | Slow / hung / **suspended** subscriber never delays publishers or siblings; a publisher's own bulk never delays its own `REQ/REPLY` (priority lanes) |
| 5 | Class A: zero loss across broker kill/restart, publisher crash+restart, subscriber outage, **and gateway crash between DELIVER_ACK and sink persistence** (WAL ordering) — verified by end-to-end delivery **count** |
| 6 | Class B coalesces (concurrent-publish: delivered = seq-max) + retained delivery on subscribe; class C drops counted |
| 7 | Expired command never dispatched **and never executed late** (queued-behind-a-5 s-stall test); in-flight redelivery executes once |
| 8 | Poison dead-letters after N attempts; process survives handler exceptions |
| 9 | Unknown envelope/payload fields ignored (mixed-version tolerance) |
| 10 | Unauthorized publish rejected + audited |
| 11 | NACK-with-healthy-pipe: delivery within T of `RESUME` (redelivery schedule) |
| 12 | Broker restart under load, 3 publishers with full journals → convergence, no NACK oscillation |
| 13 | R-R round-trip p99 < X ms while the same pipe carries saturated bulk (replay + class-C burst) |
| 14 | Load: **T-L1** soak 100 msg/s × 8 h (flat memory/journal) · **T-L2** burst 1000/1 s drained < 30 s · **T-L3** storm 1 kHz × 60 s → downstream ≤ 10/s, bound held · **T-L4** 1-h outage backlog drains < 10 min without restart · **T-L5** disk co-load · **T-L6** herd: 100 gateways register+drain with jitter, Fleet responsive |

The composite scenarios (5, 11–14) exist because every reviewed failure lived in **hand-offs under concurrency** while component-isolated tests passed.
