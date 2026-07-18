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
  "sourceEpoch": 42,                             // publisher incarnation (R-2) — see below
  "seq":         18734,                          // per-(source,epoch) monotonic — ordering/loss/dedup
  "timestampUtc":"2026-07-18T11:02:03.412Z",
  "schemaVersion": 2,                            // additive-only; ignore-unknown (mixed versions
  "ttlMs":       null,                           //   are the DESIGNED steady state under ToolHost)
  "attempts":    0,                              // poison detection
  "payloadType": "ScanCommittedPayload",
  "payload":     { "...": "..." }
}
```

Frame cap **1 MB** — the bus carries *pointers* (paths / ids), never bulk data.

**`sourceEpoch` — publisher incarnation (resolves R-2).** A publisher's `seq` restarts at 0 every process start, so without an incarnation marker a restart (AOI restarts daily) or a journal re-creation makes the next message's low `seq` look like a *duplicate* to a subscriber whose high-water is already high — silently dropping a fresh wafer. `sourceEpoch` is a monotonic counter persisted in a one-line file beside the journal, **incremented on every journal (re)creation** and read at startup. The dedup identity is therefore **`(source, sourceEpoch, topic, seq)`**, not `(source, topic)`. Rules: a higher epoch **resets** the subscriber's seq-contiguity baseline for that `(source, topic)` and is **logged + alarmed** as a distinct "publisher re-incarnated" event (never confused with a gap); class-A `seq` is restored from the journal high-water on start so a clean restart keeps the same epoch and continues the sequence. `schemaVersion` bumps to 2 (additive — old subscribers ignore the field and fall back to `(source, topic)`, which is safe during the mixed-version window because the gateway is upgraded before AOI in the same wave).

## 6.4 Wire protocol

Transport: one duplex Windows named pipe per process (`\\.\pipe\camtek.bus`), localhost only, **pipe-ACL authenticated** (identity per connection → per-topic publish ACLs for free). Framing: 4-byte length prefix + UTF-8 JSON.

| Frame | Purpose |
|---|---|
| `HELLO` | identity (`source` + `sourceEpoch`, R-2), subscriptions, per-class-A-topic publisher replay start (`resumeFromSeq` — publisher-declared; broker is stateless and never serves history) |
| `PUB` / `PUB_ACK` | publish / broker enqueued to all matched subscriber queues (sufficient for B/C) |
| `DELIVER` / `DELIVER_ACK` | delivery / subscriber processed (class A — sent only after durable ownership, e.g. the gateway's WAL append) |
| `E2E_ACK` | class-A end-to-end confirmation **per (message, declared durable-subscriber set)** → journal appends the ack-tombstone. The set is the topic's registry-declared durable subscribers (R-1), **not** the connections live at PUB — see §6.6. |
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
- **Reconnect algorithm:** the high-water **H = the max seq released to the pump**, recovered on restart as `max(journal seq)` for the current epoch (stated so it is not left implicit). Replay journal strictly in seq order to H → then drain the live queue discarding class-A ≤ H → per-source FIFO holds; the caller never pauses. Reconnect backoff is jittered; replay paced by broker credit.
- **Dispatcher duties:** seq-contiguity dedup per **(source, sourceEpoch, topic)** (R-2) — O(1), immune to replay-burst size (`messageId` LRU only as the R-R secondary net); a higher epoch resets the baseline (alarmed); two-stage Ttl gates on a **monotonic clock**; catch boundary (a handler exception never kills the process); poison → dead-letter file after N attempts + alarm; reply cache = atomic insert-or-get of an in-progress placeholder (concurrent redelivery awaits the same completion — no double execution); late REPLYs counted, never a fault.
- **Storm control** (topic-contract, in the library): error-class telemetry coalesced by `(source, errorCode)` window (first immediate, summaries every 10 s) + token bucket (10/s sustained, 100 burst).

### Gateway WAL lifecycle (resolves R-3, R-4)

The gateway's `BusSource` is the class-A subscriber; its WAL is the message's durable owner between the bus and the external sinks.

- **DELIVER_ACK is a function of the WAL append ONLY (R-4).** The gateway appends the message to the WAL (atomic: write tmp + `fsync` + rename) and *then* sends DELIVER_ACK — it does **not** wait for `RouteAsync` / sink delivery. Sink routing consumes from the WAL **asynchronously**. This severs the path by which a Fleet/TSMC outage back-propagated onto the bus (channel fills → routing blocks → ACKs stop → broker NACKs → AOI journal grows). An external cloud outage now lives entirely in the gateway spool, exactly as the design intends.
- **WAL entry state machine (R-3):** each entry is `received → routed{ FleetSink: pending|done, TsmcSink: pending|done } → deleted`. A **per-sink completion callback** from the `SinkDispatcher` marks its leg done; the entry is deleted only when **every** sink leg is done. This fixes the two duplication paths: (a) `MarkDeliveredAsync` is now actually driven by sink completion (it was dead code), so the 60 s drain never re-sends a delivered message; (b) one WAL entry with two sinks where only one succeeded is retried **only to the pending sink**, never re-sent to the one that already succeeded (the earlier single-WAL design re-sent to both — a regression against today's per-sink spools).
- **Gateway-side idempotency (R-3) — the gateway does not emit duplicates outside a narrow crash window.** The WAL is the durable dedup store: on every route the gateway checks the message's `(source, sourceEpoch, seq)` against a **persisted per-source high-water/bitmap recovered from the WAL on start**, and skips a message already delivered. So a redelivery after a crash — the case that produced duplicates — is recognized and dropped *at the gateway*, before either sink. This removes the correctness dependency on downstream dedup. **Residual (honest):** a crash in the narrow window *between* a sink acking the send and the WAL marking that leg done can still re-send that one leg on recovery. For that window only, the submission carries `messageId` and **downstream `messageId` dedup at Fleet/TSMC is defense-in-depth** (a hardening confirmation, no longer a correctness gate) — see §5.6.1 A-6, now a *recommended* cross-team item rather than a P1a blocker. Duplicates are thus eliminated to WAL-recovery granularity, not merely "guaranteed absent."
- **Spool-at-quota is backpressure, never sink-drop (R-4):** WAL quota reached → the gateway **stops sending DELIVER_ACK** → NACK flows back to the publisher journal (the alarmed, sized store) → journal 50 % alarm fires. Loss is never taken at the last hop before the customer; it is always pushed back to the one place that is sized and alarmed. Quota is sized ≥ journal cap × envelope size.
- **Test (extends assertion 5):** per-sink `count == published AND distinct-count == published` across {gateway-down-at-PUB, gateway-crash-with-128-queued, gateway-crash-between-sink-push-and-DELIVER_ACK, publisher-restart} — a plain count is blind to a loss cancelled by a duplicate.

## 6.6 Broker internals

- **Connection manager:** pipe server; identity = **OS-authenticated pipe account** (the `HELLO.sourceName` is a *label*, never the ACL key — R-7); publish-ACL enforcement keys on the account; **per-connection outbound writer task** with write deadline (a suspended subscriber is disconnected, never allowed to stall siblings). Duplicate-identity HELLO (crashed-but-not-dead client, or a second session) is resolved by `sourceEpoch`+PID: the higher epoch supersedes, the older connection is dropped with `GOODBYE(superseded)` and audited (never a silent `TryAdd` that leaves the new connection unrouted).
- **Durable subscribers are a static topic property, not a runtime connection fact (resolves R-1).** The topic registry declares each class-A topic's *durable subscribers* by identity — e.g. `scan.committed → { ToolGateway }`. E2E-ack completes only when **every declared durable subscriber has DELIVER_ACK'd**. A declared subscriber that is merely **disconnected** (gateway restart) does **not** shrink the set: its slot behaves like a persistent `NACK` — the message stays in the *publisher's* journal and redelivers on reconnect. Only an explicit **deregistration** (a config-profile change removing the subscriber) drops it from the set. This closes the gateway-restart silent-loss channel: "no live subscriber" (all declared subscribers down) keeps the message durable; **"no *declared* durable subscriber"** — a genuinely gateway-disabled tool via signed profile (§5.1 rule 2) — is the only case that acks immediately (no journal leak). Disabled-vs-down is now a config fact, not a timing accident.
- **Class-A queues** (bounded, default 128 = 2× worst burst): full → `NACK`; drained → `RESUME`. E2E-ack tracking bounded by Σ queue capacities; publisher disconnect purges its routing entries.
- **Class-B**: locked keyed-slot coalesce **per (topic, key)** with atomic dequeue-marks-consumed (a naive replace-in-channel loses updates, and a per-topic-only slot collapses multi-key state like `production.carrier`); **retained** — last value per (topic, key) delivered to every new subscriber on subscribe. **After a broker restart the in-memory retained slots are empty; every class-B publisher re-publishes its current value on (re)connect** (resolves R-5) — the broker stays persistence-free, and the publisher (which owns the state) restores it with the same `stateSeq`, which dedup absorbs. When serving a retained value the broker attaches `sourceConnected` + `retainedAtUtc` so a subscriber can treat a value whose owner is dead (`sourceConnected == false` beyond T) as a degraded-contract input rather than current truth.
- **Class-C**: drop-oldest + counted; drop counters alarmed.
- **Heartbeat/health:** PING priority-dequeued; loop-lag self-check via a pipe-frame probe (a new ToolHost probe type); counters **pushed** to ToolHost each heartbeat (survive broker death).
- **Supervision:** ToolHost child, `startOrder: 0`, `quarantine: never`, `priorityClass: AboveNormal`; broker updates are maintenance-window-only.

## 6.7 Request/reply protocol (commands)

Reply = **ACCEPTED on successful post to the executing dispatcher** — never completion, never gated on execution. The **final Ttl gate runs as the first statement of the marshaled delegate on the executing thread**; expired-at-dequeue → command-expired event + the consumer's per-command compensation. Requester-side deadline mandatory. Residual window (host-told-accepted / command-expired) is compensated and documented — it cannot be zero. Ttl derives from per-site E30 timeout config minus a measured margin (GEM pre-/post-hops measured at P0).

## 6.8 Security (resolves R-7 — the work-stream the earlier four review cycles never covered)

> **Owner: Security (Ofek Harel) — a named work-stream, a P1a entry criterion, not a footnote.** Grounded baseline today: no pipe ACLs and no TLS anywhere in `BIS\Sources`; every gRPC endpoint `Insecure`; `:5005` binds `0.0.0.0`; Fleet `:5050` is cleartext with no credentials. As originally drafted the fabric would have *increased* the command attack surface at P1a.
>
> **This section is a specified security skeleton, not a finished implementation.** Every design fork below is *decided* (so it no longer blocks the architecture), but standing up the work-stream will surface its own implementation detail — certificate/key management and rotation, a written threat-model sign-off, penetration testing — that a design document does not close. Read this as "the security design is settled and buildable," **not** "security is as done as R-1's durable-subscriber rule." Naming the owner rolls into the existing **pre-P0 "named owner" entry criterion** (§5.1 rule 4), extended to cover this work-stream.

**6.8.1 Identity is the OS-authenticated pipe account, never a self-asserted string (the linchpin — closes SEC-1/7/9).**
Today AOI_Main, the GEM shim, and ToolManager all run as one "AOI user," so a `HELLO.sourceName` string cannot separate them and any process as that user could publish `*.commands`. Resolution: **run the privileged publishers under distinct service accounts** — `svc-GemShim`, `svc-ToolManager`, `svc-Gateway`, plus the AOI-user for AOI_Main — and bind every per-topic publish ACL to the **impersonated pipe account** (`NamedPipeServerStream.GetImpersonationUserName` / SID), mapped account→ACL in the broker. `HELLO.sourceName` is a display label only. The ACL default is **deny** (the old `SenderToAcl => Acl.Any` was fail-open). The gateway also derives a message's `source` from the authenticated account, not the envelope field, so a spoofed `source` cannot inject `scan.committed`.

**6.8.2 Topic ACLs.** `gui.commands` / `tool.commands` publishable only by `svc-GemShim` and `svc-Gateway`; `tool.state` / `production.carrier` only by `svc-ToolManager`; `scan.*` only by the AOI user. The gateway's REST **diagnostic** surface runs on a *separate* bus connection under a non-command ACL, so the "diagnostic publishes non-command topics only" rule is enforced by the broker (by account), not by in-process convention (closes SEC-6).

**6.8.3 `:5007` external command door — default-deny, authenticated (SEC-3).** MES/CMM command intake does **not** bind or accept until authz is implemented. **Mechanism (decided): mTLS** — fab tools are not uniformly domain-joined and MES is off-box, so certificate-based mutual auth is the portable choice; Windows-auth is the documented fallback only for sites that are fully domain-joined and require it. This closes §5.6 item 7. Bound to the minimum interface (a dedicated MES VLAN, not `0.0.0.0`), with per-caller rate-limit + lockout. "`:5007` refuses unauthenticated callers" is a **P1a exit criterion** with a test. (Certificate issuance/rotation is work-stream implementation detail.)

**6.8.4 Child manifest is signed and verified (SEC-2 — SYSTEM code-exec otherwise).** ToolHost runs as LocalSystem and launches children by path from `toolbus.json`; today `ComputeHash()="TODO"` and there is no verification. Resolution: the manifest is **signed** (and the referenced exes Authenticode-signed); ToolHost **verifies the signature before launch and fails closed** on mismatch; the config directory ACL grants write only to the ToolHost service account (an explicit WiX `ServiceInstall` ACL). Signing authority + key location are recorded in the security work-stream doc.

**6.8.5 Data at rest (SEC-5).** Journals, WAL spool, and dead-letter files contain `scan.committed` envelopes (WaferId/LotId/ResultsPath — customer IP) as plaintext JSON. Their directory ACL is restricted to the ToolHost + gateway service accounts (explicit, not inherited); dead-letters have a retention + scrub policy with an alarm. Encryption-at-rest is optional and secondary to the ACL.

**6.8.6 Audit (SEC-8).** Command publishes and ACL rejections are written to an **append-only, service-account-owned sink OFF the bus** (not a coalesced `tool.telemetry` topic the AOI user can publish or storm away), **before** the command is published, with the authenticated account + `correlationId`. The audit stream is excluded from storm control.

**Net-surface honesty:** with §6.8.1–6.8.6 the tool ends with **fewer authenticated surfaces** than today (`0.0.0.0:5005` gone, `:50055` already loopback and contained, one audited external door at `:5007`). Without them, P1a would have *more* command surface — hence they gate P1a.

**Standing:** name the security owner (folds into the §5.1 rule-4 pre-P0 "named owner" criterion) and let the work-stream execute its implementation detail — certificate/key management + rotation, threat-model sign-off, pen-test. No design fork remains open; `:5007` authn is decided (mTLS).

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
