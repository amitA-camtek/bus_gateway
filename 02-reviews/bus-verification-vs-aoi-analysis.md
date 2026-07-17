# Bus Design Verification vs. AOI_Main Communication Analysis

> Cross-check of the bus fabric design ([camtek-messaging-bus-design.md](../01-proposal/camtek-messaging-bus-design.md), [a3-fused-bus-gateway-design.md](../04-history/a3-fused-bus-gateway-design.md)) against the independent inventory in [aoi\aoi-main-communication-analysis.md](../03-inputs/aoi-main-communication-analysis.md) (2026-07-17), which catalogued AOI_Main's **full** communication surface (~21 out-of-proc COM counterparts, 2 gRPC links, 2 MMF links) and recommends a **gRPC mesh (Alt A) + in-proc consolidation (Alt D)** — a different answer than the bus.
> Verdict up front: **the bus design survives for its scoped mission (tool-management event fan-out), but it is NOT a whole-AOI communication answer, one of its ground rules is overstated, and there is a genuine strategic conflict with the gRPC-mesh recommendation that needs a single decision.**

---

## 1. Where the analysis CONFIRMS the bus design

| Bus design position | Analysis finding | Status |
|---|---|---|
| Bulk data never rides the bus — pointers only; data plane stays on shared memory | §1.4/§2.4: TilePool + STIL MMF links exist and "must continue to" bypass the control plane | ✅ Aligned |
| `IPublisher`/PubSub `Publish` sites are the migration seam (fused P1a) | §1.6: exactly those call sites, config-driven transport, publish-only | ✅ Aligned — same seam |
| FalconWrapper hub may be a permanent bridge (fused design open question 7) | §4: "freeze as permanent COM façade until contractually renegotiated (risk M11)" — it's an **external customer automation contract**, not just internal tool clients | ✅ Aligned and **strengthened**: retirement is a *contract* question, not an engineering one. Fused doc Q7 should absorb this |
| Files/DB (`.mdb` DAO, INI, `c:\job`) out of bus scope | §1.7/§4: same — flagged for a separate DataServer/MDC path | ✅ Aligned |
| Sync state queries are a marginal bus fit (bus design §12.3: "acceptable… consider snapshot topic") | §2.2: "property-get chattiness must be audited" | ⚠️ Aligned in direction, but the bus doc under-weights it — see Gap 5 |

## 2. Gaps found in the bus/fused design

### Gap 1 (MAJOR) — the inventory is ~3× larger than the bus program's picture
The fused design migrates the tool-management edges (~7 COM interfaces). The analysis documents **~21 out-of-proc COM counterparts**, including ~15 .NET ROT singletons that appear **nowhere** in the bus/fused/investigation docs: RobotUI (rich callbacks), JobProvider, SystemLogger, WafersDatabase, InspectionMng, MaintenanceManager (+CB), BufferStationManager, WaferLevelCassetteManager, WaferLoader, WaferHandling/AutomationManager, BSI/EBI/FRT modules, CamtekUtils — plus native MachineSrv.exe, EfemSrv.exe, ScenarioManager.exe, WaferMapServer.exe.
**Impact:** the bus's "fabric-wide adoption" section (§12) hand-sketched `machine.*`/`dds.*` namespaces without this census. Most of these 21 links are **command/callback service relationships, not event fan-out** — i.e., per the bus's own fit table (§12.3), many are *poor bus candidates*.
**Action:** adopt the analysis's triage (sole-consumer → in-proc consolidation; shared services → RPC; event fan-out → bus). The bus must not be positioned as the replacement for all 21.

### Gap 2 (MAJOR) — a ground rule is overstated: AOI_Main already HOSTS a gRPC server in production
All our docs assert "AOI dials out, never listens — gRPC server on .NET FW 4.8 effectively impossible." The analysis proves AOI_Main ships **Grpc.Core as client (ADC inference :5000, CMM) *and* as a hosted server (`CmmReceiverServer`, localhost:50055)** — in production, on net48, in this exact process (`CmmReceiverApiRequetsHandler.cs:176-328`).
**Impact:** (a) the *feasibility* motivation for "never listens" is factually wrong — the rule survives only on its *merits* (deprecated Grpc.Core runtime, security surface, one-listener-per-process hygiene) and the docs must say so; (b) two live gRPC links + one inbound gRPC channel are **missing from every baseline diagram** we produced; (c) the "AOI has no inbound network surface" implication used in security reasoning is false today (:50055 exists).
**Action:** correct the ground-rules table (alternatives doc §0, fused doc §1) and add ADC + CMM (+:50055 inbound) to the baseline diagrams.

### Gap 3 (MAJOR, decisive) — the multi-PC question
The analysis scores scalability with "**AOI is a multi-PC tool**" and penalizes pipes for stopping at the machine boundary. The entire bus design is localhost-only by decision (named pipes; "cross-machine goes through the gateway door").
**Impact:** if any bus-relevant counterpart (DDS "pizza" farm nodes? grabber PCs? ScenarioManager?) runs off-box, the bus cannot reach it, and §12's `dds.*`/`machine.*` adoption plan is partly void. Note the analysis also says AOI_Main touches farm *results* only via TilePool MMF (same-machine by definition) — so the practical exposure may be small, but it is **unverified**.
**Action (blocking for §12 adoption phases):** census which processes run off-box on real tool configurations. Bus scope = same-PC processes only, stated explicitly; off-box = gateway/gRPC territory.

### Gap 4 (MODERATE) — no sole-consumer triage in the bus program
The analysis's best insight: for singletons where AOI_Main is the **sole consumer** (candidates: RobotUI, WaferLoader, BufferStation, WaferLevelCassette, module UI helpers), the right move is **in-proc consolidation (D)** — callbacks become plain .NET events, zero wire, zero contracts, processes disappear. The bus/fused docs never consider "no IPC at all" as an alternative per edge.
**Action:** add the triage question to the fused migration gate: *"is this edge sole-consumer? → consolidate, don't bus it."* Cheaper than any transport.

### Gap 5 (MODERATE) — chatty synchronous property-gets
The bus fit table rates sync state queries "acceptable (request/reply)". At COM-wrapper reality (property chains on hot paths, e.g. RobotUI/Machine wrappers), request/reply-per-property over a broker would multiply latency.
**Action:** adopt the analysis's instrumentation step — **call-frequency telemetry on `ComServerWrappers\` before migrating any edge** — as a P0 item in the fused roadmap; downgrade the fit rating to "acceptable only below N calls/sec, else snapshot topic (class B)".

### Gap 6 (MINOR, verify) — where does ScanManager actually live?
Our feasibility review placed `CScanManager`/`CAutoCycleManager`/`CFalconEvents` in **FalconWrapper.exe**; the analysis reaches `CScanManager`/`IScanManagerInkingCB` via **ScenarioManager.exe** (`ScanManagerWrapper.cs`) and attributes external-control + `IFalconFireEvents` to FalconWrapper. Possibly two different objects/interfaces in two hosts — but the fused design's P2 (`scan.operations` consumers) depends on which process hosts what.
**Action:** reconcile in the repo before P2 planning; update fused §2.3 diagram accordingly.

### Gap 7 (MINOR) — AOI_Main has never been a subscriber
§1.6: "No Subscribe found — AOI_Main does not consume the bus" (today's PubSub). The fused design makes AOI a subscriber (BusAdapter: `gui.commands`, `tool.state`, `loader.events`). The STA-marshal duty is designed, but there is **zero in-process precedent** — this is greenfield inside the hub process.
**Action:** none new — but the P1a pilot should include one trivial AOI-side subscription (even diagnostic-only) to burn in the dispatcher/marshal path early.

## 3. The strategic conflict — bus vs. gRPC mesh (must be decided once)

The analysis recommends **gRPC mesh (A, score 21)** over **JSON-RPC/pipes (G, 19)** for AOI_Main, arguing: gRPC is already in-process both directions; a pipes dialect adds a *third* protocol; pipes stop at the machine boundary. The bus is, in its transport, a broker-based cousin of G — so this recommendation *competes* with the bus for the callback middle ground.

Honest counter-arguments the analysis doesn't weigh:
- **Grpc.Core is EOL/deprecated** — alternative A builds the hub's future on a runtime with no upstream; "it's already vendored" is a today-fact, not a ten-year strategy (grpc-dotnet can't host on net48, so server-side stays on the dead runtime until AOI leaves FW 4.8 — which is the one thing we're told won't happen).
- **A mesh has no fan-out story** — 21 point-to-point contracts with per-link server-streaming callbacks re-create the CB web in proto form: N×M wiring, no per-subscriber isolation, no durability classes, no dedup/journal. Those properties were the *reason* for the bus.
- The "third dialect" count cuts both ways: the mesh keeps **COM + gRPC** for years (native servers stay COM per its own tiering); bus + gRPC is also two-going-on-one.

**Proposed resolution (recommendation):** they are complementary if scoped by *shape*, and in conflict only if either claims everything:

| Link shape | Owner |
|---|---|
| One-to-many events, telemetry, state broadcast, lifecycle (tool.state, scan.*, alarms) | **Bus** (durability classes, isolation, fan-out are the point) |
| One-to-one service APIs — request/response over an object/query surface (JobProvider, WafersDatabase, InspectionMng, SystemLogger…) | **gRPC** (the analysis is right: contracts, already in-process, off-box capable) — matches the bus's own §12.3 "poor fit" row |
| Sole-consumer singletons | **Neither — consolidate in-proc (D)** |
| Bulk data | **MMF (unchanged)** |
| External/customer contracts (FalconWrapper, host GEM) | **Frozen façades** |

This split must be ratified as one decision (single ADR), or the two programs will fight over the callback edges indefinitely.

## 4. Actions summary

1. **Correct the ground-rules claim** (AOI can and does host Grpc.Core) in alternatives §0 + fused §1; add ADC/CMM/:50055 to baseline diagrams. *(doc fix, small)*
2. **Verify multi-PC topology** — which counterparts run off-box; bind bus scope to same-PC explicitly. *(blocking for bus §12)*
3. **Reconcile ScanManager host process** (FalconWrapper vs ScenarioManager) before fused P2. *(repo check)*
4. **Add triage gate to the fused roadmap**: sole-consumer→consolidate / service-shaped→gRPC / event-shaped→bus, + call-frequency telemetry on ComServerWrappers as P0. *(process fix)*
5. **Write the ADR** ratifying the bus-vs-gRPC-vs-consolidation split of §3. *(decision needed — owner: architecture forum)*
6. Absorb the FalconWrapper-is-a-customer-contract fact into fused Q7. *(doc fix)*
