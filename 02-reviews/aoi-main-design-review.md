# AOI_Main Complete Communication Design — Adversarial Review Record

> Iterative review of [aoi-main-complete-communication-design.md](../01-proposal/aoi-main-complete-communication-design.md). Three independent reviewers per round: design consistency, repo-grounded feasibility (`C:\CamtekGit`), production-operations/program risk.
> This document records each round's findings, the **resolution decided**, and the verification status.
> Round 1: 2026-07-17. Verdicts: consistency **NOT READY** · feasibility: 3 claims SHAKY, 1 WRONG · ops: **"as written: not survivable"** (architecture endorsed; program shape rejected).

---

## Round 1 — Findings & Resolutions

### CRITICAL

| # | Finding (reviewer) | Resolution decided → applied to design |
|---|---|---|
| R1-C1 | **Unregistered topics + namespace violations** — `cmm.requests`, `dds.status`, `machine.state/alarm` exist in no registry; two violate the namespace-ownership ACL rule (tool-mgmt publishing `dds.*`, gateway publishing `cmm.*`); DDS sequencing clashes with bus §12.5 (consistency) | `dds.status` → **`scan.dds-node-status`** (tool-mgmt-owned, class C, registered). `machine.*` rows renamed to the registry examples and marked *future — machine-layer-owned, post multi-PC census*. `cmm.requests` **deleted** — superseded by the CMM redesign (R1-C5). Every topic the doc uses now appears in Appendix A.1 with class + publisher |
| R1-C2 | **Command-ACL contradictions ×3** — FalconWrapper, ExternalControlCbUiWrapper, and AOI's `ChangeToolState` all publish command topics restricted to GEM shim + gateway (consistency) | No new command publishers. (a) FalconWrapper bridge → **AOI-side in-proc dispatch** (R1-C3); (b) ExternalControlCbUiWrapper at P4 dispatches through BusAdapter **in-process** — never publishes to the bus; (c) AOI's own state transitions stay **direct API calls** (COM today; unchanged at P5 — `tool.commands` is for *external* commands only). §3.5.3 note corrected |
| R1-C3 | **FalconWrapper bridge decided two ways** — E.2 "zero change" vs Flow A5/§3.2 hub-side native publisher; inbound commands can't ride "dual-publish" (consistency) | **Decision: AOI-side (option b).** Customer COM callback lands in `ExternalControlCbUiWrapper` exactly as today and is dispatched in-proc via BusAdapter (Ttl/mode gates apply; no bus round-trip). Flow A5 redrawn; `FW→BUS` edge removed from §3.2; FalconWrapper vcxproj stays untouched — E.2's "zero change" is now the *only* story |
| R1-C4 | **Broker-absent startup undefined** — "ToolHost boots it" assumed; P1a deletes `EnsureToolGatewayRunning` (AOI's only self-heal); the broker-down alarm channel is ToolHost :5100 — same failure domain (ops) | New **§3.5.3b degraded-startup contract**: non-blocking `Connect` + background retry; subscriptions registered locally, replayed on connect; **operator-visible degraded banner + AOI-side log alarm** (never via ToolHost); journal disk cap alarmed AOI-side; `EnsureBusRunning` recovery step (attempt ToolHost service start, then alert) replaces the deleted self-heal; per-phase behavior: P1 = degrade loudly, P4+ = **refuse Production entry** without bus |
| R1-C5 | **CMM relocation blocked twice** — `ExportMapConfirmation` blocks on an operator Yes/No (incompatible with Ttl'd request/reply); wafer maps (int-per-die, 100k+ dies) exceed the 1 MB frame cap; SPC.exe hosts a second receiver the plan ignored (feasibility + ops-8 sequencing) | **Per-operation split replaces wholesale relocation:** (1) *now* — gateway **gRPC proxy** in front of :50055 (closes the external surface early, no bus dependency, by-value maps keep working, modal replies keep working); (2) notifications (`ImportStarted/Completed`, alerts) → bus topics when convenient; (3) bulk map ops stay on the proxied gRPC hop permanently or move to data-plane pointer handoff **only** via negotiated CMM contract change; (4) `ExportMapConfirmation` stays gRPC (unbounded operator reply) — explicitly exempted from request/reply; (5) SPC.exe's `ComCmmReceiverServer` added to the disposition inventory |
| R1-C6 | **CONS census failures** — WaferLoader (5 external consumers incl. native C++) and BufferStationManager (SecsGemObjects) are NOT sole-consumer; "~9–10 processes deleted" overstated (feasibility) | Both rows moved **CONS → SVC-or-KEEP** (shared with GEM/TAC stack — their transport can only change with that stack's consent; parked in KEEP with census evidence until the SVC lane reaches them). §3.4 gain corrected to **~5–7 processes**. RobotUI confirmed flagship (census clean) with its real costs stated: second STA message pump + its own MachineSrv/EFEM/WafersDB/JobProvider COM connections move into AOI_Main; connector already exposes parallel .NET events (half the pattern pre-exists) |
| R1-C7 | **JobProvider is the wrong pilot** — object-graph interface (SdrServer/S21Server roots, out-params, stateful locks) and massive fan-in: SecsGem .NET clients, NetTAC, TAC.Net, ProductionGui, RobotUI, **native C++** (`ProcessProgramManager.cpp:84-88`) — a "pilot" that re-transports the whole GEM/TAC job path at once; `GetJobProvider()` doesn't exist (real seam: `JobProviderConnector.Instance`) (feasibility) | Pilot re-defined: **the census selects the pilot** — criteria: fan-in = AOI-only, flat interface, no live-object params. JobProvider explicitly **disqualified as pilot**; it migrates late, via a **compatibility connector** (COM-visible net48 façade wrapping the gRPC client, so non-AOI consumers are untouched). Flow A2 corrected to the `Instance` seam |
| R1-C8 | **"Four parallel tracks" is one release train** — three tracks edit `MainContextModule.cs`/csproj/installer simultaneously; state space explodes at the shared gate (ops) | §5 rewritten: four *planning* lanes, **max two concurrent code streams** (B-infra + exactly one AOI-heavy stream per release), executed as the **wave plan** (R1-C10) |
| R1-C9 | **No fleet configuration management** — >10⁵ theoretical flag states across 100+ tools; flip-flag ownership is an unowned proposal (ops) | New P0 deliverable: **≤5 signed canonical profiles** (arbitrary flag combos refused at startup), **config fingerprint** in every log header + published on `tool.telemetry`, Fleet dashboard before the first production flip. Test matrix bound to profiles + single-flag rollback neighbors |
| R1-C10 | **Rollback is a flag only inside the dual-run window** — CONS step 5 / B-P1b physically delete the rollback target (ops) | **Rollback-validity matrix** added (flag / redeploy / reinstall per edge); **N-release retention rule** (retirement only after the last fleet tool has run the new path for a full cycle); dormant paths exercised in CI |

### MAJOR (consolidated)

| # | Finding | Resolution |
|---|---|---|
| R1-M1 | `scan.operations` publisher contradiction (§3.2 ScenarioManager shim vs A.1 AOI-side); no E.2 row for a Scenario shim (consistency) | **AOI-side is the publisher** (BusAdapter republishes from existing COM callbacks); ScenarioManager stays KEEP for events until its own adoption program; §3.2 + §2.1 + E.2 reconciled |
| R1-M2 | CONS threading claim false pre-P2; Track C "needs no fabric" contradicted by Flow A3 (consistency + ops) | **C-standalone defined**: plain `UiMarshaller` class (no bus dependency; BusAdapter later composes it); §3.5.4 threading contract split into pre-fabric / post-P2; A3 gains the pre-fabric variant note |
| R1-M3 | Startup state bootstrap missing — class B has no retained delivery; GUI starts stale (consistency) | Startup step added: after subscription registration, **initial state fetch** — via existing COM until P3, via a request/reply state snapshot after P3. (Bus-level retained-last-value noted as an alternative for the bus backlog) |
| R1-M4 | §3.2 shows three service processes vs B.1's one-host decision; Automation missing; Maintenance streaming annotation contradicts bus-lane routing (consistency) | §3.2 redrawn: **one `Camtek.ToolServices` host** box incl. Automation; Maintenance events annotation → bus (`tool.telemetry`) |
| R1-M5 | ADC + CMM gRPC client links vanish from target diagrams; DAO/grabbing missing from §3.3 (consistency) | Added to §3.1–§3.3 KEEP lane; §3.3 gains DAO + grabbing boxes — the target diagram no longer looks cleaner than the design is |
| R1-M6 | `Connect()` seam over-claimed — shared connector assemblies (multi-process, multi-language: proxy must stay COM-visible net48 and flips every consumer at once); object graphs + live-object params (InspectionMng, CamtekUtils, RobotUI.Initialize) can't hide behind a seam; COM idioms at call sites (`Marshal.ReleaseComObject` throws on non-COM proxy) (feasibility) | B.2 rule 1 rewritten with the **three limits** + per-edge COM-idiom sweep added to the SVC gate; services with object-graph/live-object interfaces flagged for interface redesign, not seam swap |
| R1-M7 | :50055 security win sequenced years out (ops) | Solved by R1-C5's gateway proxy — the external surface closes in **Wave 2**, independent of B-P4 |
| R1-M8 | Class-A journal on gateway-disabled tools = unbounded disk growth (no subscriber ever acks) (ops) | **Bus contract addition**: class-A publish with zero registered durable subscribers → immediate ack + counter (no journal retention); broker contract test added. Applied to the bus design doc |
| R1-M9 | BusAdapter location drift across docs (fabric/bus say "frmProduction BusAdapter") (consistency) | Supersession notes added to fabric + bus docs: BusAdapter is a MainContext-owned plain class per this design's §3.5.2 |
| R1-M10 | Coordination burden unowned; bus/ToolHost owner unnamed; calendar dishonesty; blocking censuses not gating tracks; §0.4 approvals buried (ops) | §5/§6 additions: named-owner as pre-P0 entry criterion; program-span statement (10–20 release cycles at full scope — the strongest argument for the wave plan); A-1/A-2/A-3 censuses added as explicit track entry criteria; dependency approvals moved into §6 with owner + decision date |

### Minor (fixed opportunistically)
Duty count 5→6 (A.2); journal ownership unified (BusAdapter publish façade owns it; §3.3/A.3 redrawn); §5 census reference §2.1+§2.2; `Fire*` count stated as ~25 methods / ~40 call sites; MachineSrv future events → Track F re-exam list; `scan.operations` R-R split → `scan.operations.requests` topic; BsiHR + `IWaferNavigatorCB`/`IGuiServiceCallback` + CMM-server-identity flag rows added; Flow A3 hop count honesty (broker hop still exists — supervised and non-blocking); A-1 gate added to Track B P2 entry.

---

## Round 1 — What survived unchanged (positive findings)

- **Disposition completeness** — every counterpart of the analysis inventory is dispositioned (all 6 native servers, ~15 singletons, both CMM directions, both MMFs, all 5 inbound channels); ports reconcile across all docs.
- **Four-lane ADR** — endorsed by all three reviewers ("sound and worth ratifying" — ops).
- **RobotUI as CONS flagship** — census verified clean; connector half-implements the pattern already.
- **`ToolStateReactions` extraction** — verified realistic (~60 lines, no direct control manipulation; cost = abstracting ~8 MainContext operations).
- **The minimum-viable-program insight (ops)** — ToolHost + P1a/P1b + spool fixes + C-standalone deliver ~80% of the claimed benefits with 2 teams and no external lockstep; adopted as the §5 wave plan.

## Round 2 — Verification (2026-07-17)

All 20 round-1 resolutions checked against the design + companion docs. Result: **14 VERIFIED, 4 PARTIAL, 0 missing** — the program-shape fixes (degraded startup, CMM contain-then-split, wave plan, profiles, rollback matrix) and all bus-contract changes landed cleanly. The PARTIALs were **fix-propagation debt**: 10 spots (2 diagrams + 8 prose/table cells) still carried the pre-fix story.

| # | Round-2 finding | Severity | Resolution applied |
|---|---|---|---|
| R2-1 | §3.2 + §3.5.2 diagrams still listed WaferLoader/BufferStation as *consolidated* (contradicting their census-failed KEEP status) | **CRITICAL** | Both boxes corrected to "RobotUI · WaferLevel (census) · module helpers" |
| R2-2 | D.3 said the FalconWrapper bridge "republishes internally to `gui.commands`" — re-opening the command-ACL hole R1-C2 closed | **CRITICAL** | D.3 rewritten: in-process dispatch, no bus publish, ACL holds |
| R2-3 | B.3 still titled "pilot service (JobProvider)" and used the non-existent `GetJobProvider()` seam | MAJOR | Retitled to the generic SVC pattern flow; `Connector.Instance` seam |
| R2-4 | FalconWrapper bridge decision still written as *open* in E.2 ("only if hub-side is chosen"), §2.1, and D.2 | MAJOR | All three now state the decided AOI-side bridge; "FalconWrapper is never a bus client" |
| R2-5 | §6 A-5 overclaimed "hosted-server exposure deleted by the CMM relocation" | MAJOR | Corrected: contained at Wave 2, deleted only at the per-operation split |
| R2-6..10 | Minors: §2.5 "Relocated" verb; fabric Flow S2 participant name; JobProvider top billing in §3.1/§3.2; Maintenance streaming annotations (B.1, E.3); B.4 proxy ordering | MINOR | All applied |

Post-fix grep sweep: zero hits for `cmm.requests`, `dds.status`, `GetJobProvider`, "republishes internally", "Relocated to gateway", "pilot service (JobProvider)"; WaferLoader/BufferStation appear only in the current-architecture diagram (correct) and the §3.3 KEEP box (correct).

## Round 3 — Final Verification (2026-07-17)

**All 10 round-2 fixes VERIFIED (10/10).** Fresh whole-document contradiction sweep:

- **Lane placement** — every §2 assignment matches every diagram (WaferLoader/BufferStation appear only where correct: current-architecture and KEEP contexts).
- **Command ACL** — only the GEM shim and the gateway CommandPublisher publish `*.commands`; AOI only subscribes; all in-proc dispatch paths hold.
- **Topic registry** — every topic name used resolves to the bus registry or its namespace table; `machine.*` names match the registry examples and are marked future/gated on A-1; zero stale pre-fix strings.
- **Counts/phases/ports** — "~5–7 processes", "~25 `Fire*` / ~40 sites", and all ports (:5000, :5005−, :5007, :5060, :5100, :50055 contained→−) reconcile everywhere including E.6.

Three trivial cosmetic residuals were reported (E.2 phase label P2–P3 vs P2–P4; ServiceClients example ordering; Flow S1 journal edge eliding the publish façade) — **all three applied post-verdict**.

---

# FINAL VERDICT: **READY for ADR ratification**

The design ([aoi-main-complete-communication-design.md](../01-proposal/aoi-main-complete-communication-design.md)) is internally consistent and consistent with the fabric and bus companion docs. All round-1 CRITICAL/MAJOR findings (10 + 10) and all round-2 propagation findings (2 CRITICAL, 3 MAJOR, 5 MINOR) are resolved and independently verified.

**Standing conditions attached to ratification** (not document defects — program preconditions the reviews established):
1. Wave-0 censuses execute before their dependent steps (A-1 multi-PC → gates B-P2+ and the `machine.*` future topics; A-3 sole-consumer → gates each CONS absorption; S-pilot selection census).
2. A-2 (ScanManager host reconciliation) resolves before B-P2 planning.
3. The named bus/ToolHost owner exists before P0 starts (§5 rule 4).
4. The §0.4 dependency approvals (A-11) are decided at P0 exit.
5. Comparator qualification evidence (A-12) exists before P1a entry.

Review trail: 3 independent round-1 reviewers (consistency / repo-feasibility / ops-program) → 20 major-or-worse findings resolved → round-2 verifier (10 propagation findings) → resolved → round-3 verifier (clean sweep, READY).
