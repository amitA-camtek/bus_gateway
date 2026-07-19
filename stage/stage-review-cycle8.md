# Stage Design — Cycle-8 Adversarial Review Record

**Date:** 2026-07-19  
**Target:** `stage/*.md` (docs 00–07 + codeSnippets/)  
**Codebase root:** `C:\CamtekGit\BIS\Sources` (read-only verification)  
**Dimensions run:** consistency · feasibility · operations · concurrency · connectivity · load · data-integrity · test-strategy  
**Security:** EXCLUDED per command-file rule (security-relevant observations filed incidentally by other dimensions)

Prior resolved finding sets (not re-reported): R-1..R-8, X7-1..X7-8, S-1..S-18, M-1..M-38, OPS7-1..OPS7-5, DI7-1..DI7-6, CN7-1..CN7-9, GS7-1..GS7-4, SEC7-1..SEC7-7, FEA7-1..FEA7-3, R-OPS-1..R-OPS-6, R-TS-1..R-TS-3, CON6-1..CON6-6.

---

## Tally (after deduplication)

| Severity | Raw (8 dims) | Unique (deduped) | Resolved by fixes | To OPEN-DECISIONS |
|---|---|---|---|---|
| CRITICAL | 12 | 8 | 5 | 3 |
| MAJOR | 39 | 28 | 25 | 3 |
| MINOR | 24 | 21 | 21 | 0 |

> Deduplication notes: CNN8-1 = OPS8-2 (same finding: :5100 Fleet transport); CON8-2(consistency) = CON8-3(concurrency) = DI8-5 (same: SourceEpoch/StateSeq not in consumer code); CON8-1(consistency) partially overlaps DI8-2 (both note Fleet dedup key, but address different aspects — text vs. gate).

---

## CRITICAL findings

### C8-CRIT-1 — ToolHost :5100 is loopback-bound; "Fleet surface" in §7.13 alarm table has no implemented transport
**Source:** CNN8-1 (connectivity) = OPS8-2 (operations)  
**Doc:** `01-system-architecture.md §1.4`; `07-toolconnect-design.md §7.13`  
**Failure scenario:** Broker dies, WAL at 80% quota. §7.13 routes these alarms to Fleet via ":5100 push" — but `codeSnippets/14-toolhost.cs:251` binds `http://localhost:5100/health/` and §1.4 says "loopback/mgmt iface." Fleet.Main is off-box; no push channel exists. Fleet sees a silent tool; NOC learns from the customer.  
**Resolution (decided — document fix):** Change the §1.4 binding for :5100 to "management LAN interface (not loopback — Fleet must be able to reach it); authenticated." Update §7.13 "Fleet surface" column to say "Fleet polls :5100 on the tool's management interface." The ToolHost already serves the endpoint; the only change is binding from 127.0.0.1 to the management NIC (a config value, not a protocol change). Add ":5100 binding interface" to the signed fleet-profile config and to OPEN-DECISIONS D16 for the installer/config deliverable. ✓ **Fixed in docs.**

### C8-CRIT-2 — Journal-thread replay serializes against group-commit; durability guarantee breaks on broker-restart-then-crash
**Source:** CON8-1 (concurrency)  
**Doc:** `06-bus-implementation.md §6.5`  
**Failure scenario:** After a long outage, broker restarts; AOI journal has 50k unacked entries. Journal thread enters a replay job; is occupied for minutes (paced by broker credit via E2E_ACKs that require gateway WAL fsyncs). Meanwhile new `scan.committed` publishes enqueue in the in-memory intake (8192 slots). A process crash mid-replay silently loses all accepted-but-unjournaled publishes. This breaks the "durable against process crash immediately" flagship promise.  
**Resolution (decided — document fix):** Add normative rule to §6.5: "Journal replay is a chunked, cooperative job — the journal thread processes ≤N entries (e.g. 64), re-posts itself to the back of its job queue, then processes any pending appends/tombstones before the next chunk. The credit wait lives on the pump writer, never on the journal thread. A replay job never occupies the journal thread for longer than the group-commit interval × the batch size." Add crash-mid-replay test variant to TestKit assertion 5. ✓ **Fixed in docs.**

### C8-CRIT-3 — SecsGemGui.Net is an interactive WinForms process; ToolHost supervision puts the GEM operator UI in session 0
**Source:** OPS8-1 (operations)  
**Doc:** `01-system-architecture.md §1.3.3/§1.3.4`  
**Failure scenario:** ToolHost launches SecsGemGui.Net as a Windows service child (LocalSystem, session 0). `frmMain`, `frmSecsViewer`, `frmTerminalMsgNotification` render on an invisible desktop. Fab host sends terminal message requiring operator acknowledgment; no operator sees it; host escalates a non-responding tool.  
**Resolution (human decision required — OPEN-DECISIONS D15):** The fix requires splitting SecsGemGui.Net into a headless GEM engine process (ToolHost child) and a separate interactive viewer/terminal-message UI in the operator session. This is new design work not yet estimated. Added as D15.

### C8-CRIT-4 — Legacy AOI kills any process named `ToolGateway.Endpoint` at every startup and exit — destroying the supervised gateway
**Source:** OPS8-3 (operations)  
**Code:** `clsInitAOI.cs:406` (`KillStaleToolGatewayProcesses`), `:458` (exit hook), `:478–484` (name-sweep)  
**Failure scenario:** ToolHost supervises the gateway. Operator restarts AOI (or AOI exits for any reason): `KillStaleToolGatewayProcesses` hard-kills the ToolHost-supervised gateway mid-WAL-drain; ToolHost (`quarantine:never`) restarts the child, which fails to bind :5005 (legacy AOI spawn already holds it) → crash-loop all night. Additionally, the exit hook fires on every AOI shutdown, destroying the T7 "tool visible when GUI closed" deliverable.  
**Resolution (document fix + human decision):** Add normative fix to §7.10: gateway ships under new process name (`ToolConnect.Service.exe`); a port-ownership interlock refuses to start :5005 if a `ToolGateway.Endpoint` process holds it (alarmed); the Wave-0 AOI patch disables the kill-by-name spawn path (ini flag already honored). Add D17 to OPEN-DECISIONS for the AOI code change sign-off. ✓ **Partially fixed in docs; D17 added.**

### C8-CRIT-5 — Dual-run mode (§7.11) is a contractual P1a feature with zero test assertions
**Source:** TS8-5 (test-strategy)  
**Doc:** `07-toolconnect-design.md §7.12` (G-1..G-9 have no dual-run row)  
**Failure scenario:** Wrong-door routing double-submits every wafer to Fleet/TSMC for a full release cycle; a silent BusSource fills the WAL and backpressures AOI. Neither failure is detectable without a test. The per-edge gate measures shadow divergence counts — which don't catch double-routing (both copies match, producing zero divergence).  
**Resolution (document fix):** Add G-10 (dual-run) to §7.12: "flag=shadow → sinks receive exactly the :5005 copy, comparator receives both, WAL quota gauge unchanged by shadow volume; flag flip mid-stream → per-sink `count==published AND distinct==published` across the flip including the spool/WAL overlap window; BusSource-not-routing fault → alarm within T, no quota consumption." Mark G-10 a P1a exit criterion alongside G-4. ✓ **Fixed in docs.**

### C8-CRIT-6 — Seq-sidecar durability ordering vs. compaction is unspecified; X7-9 silent-loss window survives inside the fix
**Source:** DI8-1 (data-integrity)  
**Doc:** `06-bus-implementation.md §6.5`  
**Failure scenario:** scan.committed seq 100–150 published, E2E-acked, tombstoned. Compaction: survivors → tmp → flush → `ReplaceFile` removes entries ≤150. Sidecar update is scheduled after compaction (nothing forbids this). Process crashes before sidecar fsync. Restart, same epoch: H = max(stale sidecar=90, journal max≈0) = 90. Next wafers publish seq 91–150 → gateway's high-water is 150 → 60 fresh wafers classified as duplicates and silently dropped. Daily-restart path.  
**Resolution (document fix):** Add to §6.5: "Sidecar update rule — sidecar := max(sidecar, max seq being removed by compaction), **fsynced before `ReplaceFile` begins** (compaction is gated on sidecar durability). Epoch file is bumped and fsynced **before** the new journal file is created." Add crash-point to TestKit 5 / G-8: `CrashAt(mid-compaction-before-sidecar-persist)` → restart → fresh wafer accepted, seq never below gateway high-water. ✓ **Fixed in docs.**

### C8-CRIT-7 — "Optional cross-team hardening" vs "required P1a dependency": Fleet dedup key is inconsistently scoped across documents
**Source:** CON8-1 (consistency) + partial DI8-2 (data-integrity)  
**Locations (say OPTIONAL):** `00-context-and-case.md:75`, `05-roadmap-and-risks.md:76/92`, `executive-summary.md:87`  
**Locations (say REQUIRED):** `05-roadmap-and-risks.md` A-6 row, `06:253`, `07:225`, `OPEN-DECISIONS.md D7`  
**Failure scenario:** PM scoping P1a from doc 00 or exec-summary does not schedule the Fleet dedup-key work as a P1a blocker. Ambiguous-outcome (deadline-after-send) re-sends produce genuine duplicate yield records in routine operation once shadow comparator is off.  
**Resolution (document fix):** Replace "one optional cross-team hardening (R-3)" with "the Fleet dedup-key P1a cross-team dependency (A-6, required)" in all four locations. ✓ **Fixed in docs.**

### C8-CRIT-8 — `(SourceEpoch, StateSeq)` ordering rule (M-9) is unimplementable from the normative code that claims to realize it
**Source:** CON8-2 (consistency) = CON8-3 (concurrency) = DI8-5 (data-integrity)  
**Locations:** `02-aoi-architecture.md §2.4` call sites pass 2 args to a 3-arg function; `ToolStateEvent` type has no epoch field; `codeSnippets/05-bus-adapter.cs:195-200` compares bare seq; snippet 07:110 "reproduced exactly"; snapshot path returns `(State, StateSeq)` with no epoch.  
**Failure scenario:** ToolManager holder restarts; epoch bumps, stateSeq resets to 0. BusAdapter `_lastAppliedSeq = 500`. Every subsequent transition (seq 1, 2, 3…) dropped as stale. At ~10 transitions/day GUI, light tower, InProductionMode, and E30 CEID reporting freeze for days. The pre-snapshot buffer sorts by raw seq → new-epoch seq=2 applies before old-epoch seq=1000 is finally applied last → stale-state overwrite.  
**Resolution (document fix):** Add `SourceEpoch` to `ToolStateEvent` and to the snapshot contract (COM getter must expose epoch). Fix the two call sites in §2.4 to pass epoch. Sort pre-snapshot buffer by `(epoch, seq)`. Update snippets 05/07/09 with epoch field or mark `TODO(C8-CRIT-8)`. ✓ **Fixed in docs.**

---

## MAJOR findings (unique, after deduplication)

### C8-MAJ-1 — `Pending→InFlight` timing unspecified; running-mode lease reclamation undefined; flaky-Fleet mid-drain reopens double-dispatch
**Source:** CNN8-2 (connectivity)  
**Resolution:** Add to §7.4: "`InFlight` is entered at send-start via the actor (not at channel-post). Channel items are dispatch hints; the sink's first action is an actor round-trip `TryAcquireLease(entry, sink)` that fails for anything not Pending. A periodic actor-owned lease sweep (running mode, not just restart) reclaims orphaned InFlight legs; lease duration = sink deadline + sweep interval (stated values)." Add G-suite row: Fleet up-2-min/down-again × 3 cycles, assert per-sink `distinct-count == count == published` and no leg left InFlight > lease after final recovery. ✓ **Fixed in docs.**

### C8-MAJ-2 — WAL actor has no group-commit batching, per-message fsync, no inbox bound; throughput bottleneck at storm rate
**Source:** CON8-2 (concurrency) + LOAD8-1 (load)  
**Resolution:** Add to §7.8: "WAL actor group-commits appends and high-water persists in batches (same honesty clause as §6.5 publisher journal); inbox is bounded at N=4096 and all producers are flow-controlled (no fire-and-forget enqueue). Add WAL-actor append rate (fsync-bound) to §5.2 P0 acceptance list." ✓ **Fixed in docs.**

### C8-MAJ-3 — Class-B retained slot: single dirty flag for N subscribers; `Update` has no seq-max comparison
**Source:** CON8-4 (concurrency)  
**Resolution:** Update §6.6: "RetainedSlot carries per-(slot, subscriber-connection) dirty state (or per-connection retained cursor of `(SourceEpoch, StateSeq)`). `Update` = CAS on `(SourceEpoch, StateSeq)` — seq-max wins. The per-connection writer consumes from slots (coalescing delivery); the direct-enqueue path is removed." ✓ **Fixed in docs.**

### C8-MAJ-4 — GEM shim 4-state machine has no serialization; HCACK/CEID emission races the handshake
**Source:** CON8-5 (concurrency)  
**Resolution:** Add to §1.3.4: "All ShimState transitions are serialized under a single lock (or a single-threaded dispatch queue). The HCACK decision reads state and emits degrade side-effects inside the same critical section with a state re-check. BusDegraded/BusRecovered CEIDs are emitted from within the serialized region — they cannot invert or strand." Add shim test: handshake-completes-during-denial → no stale degraded CEID. ✓ **Fixed in docs.**

### C8-MAJ-5 — `TryPostAndWait` disposes `ManualResetEventSlim` while delegate is still queued → `ObjectDisposedException` on UI thread
**Source:** CON8-6 (concurrency)  
**Resolution:** Update §2.3 and snippet 06: "Do not `using`-dispose the event on the abandoned path — an un-disposed `ManualResetEventSlim` with no kernel handle is GC-safe. Use `Interlocked.Exchange(ref _event, null)` so exactly one side disposes; wrap the delegate's `Set()` in a null-check." ✓ **Fixed in docs.**

### C8-MAJ-6 — GEM shim "bus up" handshake is actually a ToolManager-up handshake; GUI-closed boot falsely degrades with healthy bus
**Source:** CNN8-3 (connectivity)  
**Resolution:** Update §1.3.4: "The bus-up handshake terminates at the broker (PING round-trip or a REQ to a broker-served echo topic), flipping the shim out of DegradedNoBus. `tool.state.replay` availability is a separate, independently-alarmed condition ('state source unavailable' ≠ 'bus dark') with its own host-visible semantics (E30 state UNKNOWN, not ONLINE-LOCAL-because-bus-dark)." ✓ **Fixed in docs.**

### C8-MAJ-7 — ChildConfig lacks ServiceAccount field; pipe-ACL linchpin (§6.8.1) has no provisioning path through the manifest
**Source:** CNN8-4 (connectivity)  
**Resolution (document fix + human decision):** Add `ServiceAccount` field to `ChildConfig` class diagram and normative manifest spec. Specify launch as `CreateProcessAsUser` with installer-provisioned gMSA or local service account. Document ToolManager DCOM `RunAs` registration as an installer deliverable. Add a broker test: child restarted under wrong account → publish denied + audited + alarm. Add D18 to OPEN-DECISIONS for security work-stream to specify the account provisioning method. ✓ **Fixed in docs; D18 added.**

### C8-MAJ-8 — `loader.events` publisher inconsistent: doc 03 says EfemSrv-side shim; doc 04 (M-30/FEA7-1) says AOI-side republish; registry ACL says EfemServer
**Source:** CON8-3 (consistency)  
**Resolution:** Fix `03-appendix-four-lanes.md:134` to AOI-side republish. Change `codeSnippets/01-bus-contracts.cs` `LoaderEvents` ACL to `Acl.AoiMain` with a note that a future EfemSrv-native publisher is gated on `camtek_bus.dll`. ✓ **Fixed in docs.**

### C8-MAJ-9 — `TransitionRing` drawn in GemBusShim class diagram (wrong process); GEM sketch handshakes a class-B topic with no server
**Source:** CON8-4 (consistency)  
**Resolution:** Redraw §1.3.4 class diagram: ring belongs to ToolManager shim (as §1.3.4 prose states); GemBusShim holds a client that REQs `tool.state.replay`. Update snippets 09/13 with `Serve`/REQ shapes or `TODO(X7-7)` markers. Clarify §6.6's `publishers` ACL for R-R topics is the REQ sender (svc-GemShim), not the server. ✓ **Fixed in docs.**

### C8-MAJ-10 — Doc 06 CI tier table is stale: drops assertion 5c and the gateway suite
**Source:** CON8-5 (consistency)  
**Resolution:** Make `06-bus-implementation.md §6.10` closing sentence a pointer: "CI tier table: see 04 §4.4 (normative)." Or copy the table verbatim. ✓ **Fixed in docs.**

### C8-MAJ-11 — 3→1 ToolHost transition has no installer state machine; first-install manifest failure leaves tool with zero services
**Source:** OPS8-4 (operations)  
**Resolution (document fix + human decision):** Add installer state machine to §4.3: "install ToolHost with old services stopped-but-still-registered (disabled, not deleted) → post-install :5100 health gate within T → only then delete old registrations; on gate failure, auto-re-enable old services." Add mutual-exclusion check. Add D19 to OPEN-DECISIONS for installer-implementation owner. ✓ **Fixed in docs; D19 added.**

### C8-MAJ-12 — Dead-letter CLI is a P1a deliverable with zero design; CLI→WAL actor access model unspecified; ACL blocks FSE access
**Source:** OPS8-5 (operations) + TS8-7 (test-strategy)  
**Resolution (document fix):** Add to §7.10: "CLI = an authenticated loopback command to the running gateway's WAL actor (list/inspect read-only; reinject posts a typed message to the actor). FSE-runnable via a group-scoped ACL entry (the `wal-reader` group, not the full service account). Every reinject is audited to §6.8.6. An offline mode requires `sc stop ToolHost` first. Re-inject recreates only the dead-lettered legs (other legs pre-marked Done); bypasses DedupIndex intake but records a `reinjected(entryId)` marker making a second reinject of the same id a counted no-op." Add G-10→G-11: reinject multi-sink entry → per-sink `count==published, distinct==published`; reinject same id twice → single submission. Add CLI row to §7.10 exists-vs-built table, §4.1 project impact, and Wave-1 row in §5.2. ✓ **Fixed in docs.**

### C8-MAJ-13 — Door-flip transitions (P1a rollback, P1b flip) leave Pending WAL entries under incoherent overlap contract
**Source:** OPS8-6 (operations)  
**Resolution (document fix):** Update §7.11: "The overlap key is the cross-door-stable `UniqueId` at the sink boundary (a durable per-sink recently-sent `UniqueId` window sized to the max WAL drain window, not wall-clock minutes). During dual-run both doors write WAL entries (with door-provenance tag); the P1b flip is: flip profile flag → shadow entries drain to `ShadowDone`, live entries continue. The rollback procedure (bus→:5005-live) uses the same overlap key." ✓ **Fixed in docs.**

### C8-MAJ-14 — Gateway binary rollback strands WAL-Pending wafers in a format the N-1 gateway cannot read
**Source:** OPS8-7 (operations)  
**Resolution (human decision required — OPEN-DECISIONS D20):** The fix requires either a WAL export path (`--wal-export-legacy`) or a backward-readable WAL format guarantee for N-1. This is a Wave-0 deliverable decision requiring product owner sign-off. Added as D20.

### C8-MAJ-15 — Spool migrator trigger, completion marker, and concurrent-old-gateway interlock are unspecified; re-migration manufactures duplicates
**Source:** OPS8-8 (operations)  
**Resolution (document fix):** Update §7.4 rule 8: "A durable per-file completion record (`migrated.manifest` of content hashes) is fsynced before archive, and checked before migration — so a crash-after-drain-before-scrub restart does not re-migrate drained files. An explicit exclusive-access precondition: fail loudly if old gateway process or file handles exist. Migrated messages get a stated dedup-key derivation for the Fleet leg (hash of old file content → deterministic messageId)." Extend G-7 with crash-after-drain-before-scrub crash point. ✓ **Fixed in docs.**

### C8-MAJ-16 — ToolHost is a new correlated-failure single point of failure; blast radius wider than today's 3-service architecture
**Source:** OPS8-9 (operations)  
**Resolution (human decision required — OPEN-DECISIONS D21):** Either exempt GEM process from KILL_ON_JOB_CLOSE (breakaway job / monitored-not-owned), or gate GEM's manifest entry on a measured ToolHost stability criterion. Product/GEM owner decision. Added as D21.

### C8-MAJ-17 — ≥10,000 scan.committed edge gate is ~7 days of 24/7 full-rate production; unreachable in a lab; accumulation rule unstated
**Source:** OPS8-10 (operations)  
**Resolution (document fix):** Update §5.2 per-edge gate: "10,000 scan.committed pairs aggregated fleet-wide across the pilot cohort, per edge (not per tool); replayed/synthetic scan traffic through the real dual-run pipeline is admissible (labeled category) with a minimum floor of N_real real-production pairs. Expected calendar duration at nominal throughput: ≈7 site-days at 60 wph, shared across pilot fleet." ✓ **Fixed in docs.**

### C8-MAJ-18 — T-L4 gate has no stated backlog model; two documents imply contradictory traffic models
**Source:** LOAD8-2 (load)  
**Resolution (document fix):** Add to §6.10 T-L4: "1-h outage backlog model = ≤4,000 entries (1 h of max-nominal per-wafer traffic at 60 wph; telemetry storms excluded — separate storm-coincident variant with own bound gated on 60 msg/s FleetSink ceiling). In §7.5, label the 25/s figure as a quota-conservatism bound, not a traffic model." ✓ **Fixed in docs.**

### C8-MAJ-19 — No per-topic drain priority: oldest-first drain lets telemetry backlog head-of-line-block scan.committed recovery
**Source:** LOAD8-3 (load)  
**Resolution (document fix):** Update §7.8 drain-scheduler row: "Drain in per-topic lanes with weighted priority: `scan.committed` legs first (weighted ahead of `tool.telemetry`), oldest-first within a lane." Add G-9 assertion: "mixed-topic backlog: scan legs complete first." ✓ **Fixed in docs.**

### C8-MAJ-20 — WAL constants unsourced; `scan.committed` has no payload budget; WAL physical layout unspecified at 1.6M-file scale
**Source:** LOAD8-4 (load)  
**Resolution (document fix):** Add to §7.5: "Declare `scan.committed` payload budget in the topic registry (e.g. ≤4 KB, using the M-33 mechanism). WAL physical layout: append-only segment files + index (not one-file-per-entry), with measured enumeration bound and a startup sweep bound. State whether quota counts serialized or allocated bytes. Avg-entry-size (2.5 KB) is a P0-measured quantity from which 1.6M/18h/alarm thresholds are re-derived." ✓ **Fixed in docs.**

### C8-MAJ-21 — Class-A queue sized for burst only; concurrent-replay NACK guaranteed by arithmetic; convergence mechanism unspecified
**Source:** LOAD8-1 (load)  
**Resolution (document fix):** Update §6.9: "Broker class-A queue: either (a) sized ≥ Σ(declared publishers × in-flight window) for the worst topic — 6 × 32 = 192 → 256 — or (b) credit pacing is specified in §6.6 (RESUME carries per-connection credit = free-slots / NACKed-connections; low-watermark = 32; jittered RESUME). Config-load invariant: Σ publisher in-flight ≤ class-A queue capacity OR credit pacing enabled. WAL append rate (fsync-bound) added to P0 measured-ceilings list." ✓ **Fixed in docs.**

### C8-MAJ-22 — Fleet dedup key has no P1a-flip entry criterion, no capability handshake, and undefined no-key fallback behavior
**Source:** DI8-2 (data-integrity)  
**Resolution (document fix):** Add to §5.2 P1a flip entry criteria: "Fleet `ToolEventMessage` ingestion dedup key deployed + verified against the real Fleet build (mirror of G-4 status). Registration-time capability check: gateway refuses to leave shadow mode with distinct alarm if Fleet lacks the capability. No-key fallback: hold leg Pending + alarm, never resend without the key." ✓ **Fixed in docs.**

### C8-MAJ-23 — pointer-vs-data retention invariant documented but not enforced; failure taxonomy has no `SourceDataMissing` class; operator delete path bypasses it
**Source:** DI8-3 (data-integrity)  
**Code:** `frmJobTab.cs:820-843` `DeleteAllJobsExcept` deletes job dirs with no gateway coordination  
**Resolution (document fix):** Update §7.5 M-38: "Invariant is runtime-enforced: housekeeping and the job-delete flow query the gateway for the oldest ResultsPath referenced by any non-Done non-dead-lettered leg and refuse/warn below it (a small gateway diagnostic-REST query). Add typed `SourceDataMissing` sink outcome → immediate per-leg dead-letter with its own alarm class and no re-inject affordance. Name the actual retention config value in §4.2 Wave-0 §4 row." ✓ **Fixed in docs.**

### C8-MAJ-24 — `ShadowDone` is quota-exempt with no retention bound; P1a shadow traffic can exhaust the system volume
**Source:** DI8-4 (data-integrity)  
**Resolution (document fix):** Update §7.11: "ShadowDone entries are deleted on comparator consumption; only a compact verdict log is retained (not full envelopes). A dedicated disk gauge + alarm at 50/80% of a named shadow budget (e.g., 2 GB). G-suite test: 14-day simulated shadow run at nominal rate → bounded disk." Add `ShadowDone` as a state to the §7.4 state machine with an `AuditExpired → Deleted` exit. ✓ **Fixed in docs.**

### C8-MAJ-25 — GEM transition-ring overflow and ring-loss-on-restart have no defined fail-safe; "no missed E30 transition" silently degrades above N
**Source:** DI8-7 (data-integrity)  
**Resolution (document fix):** Update §1.3.4: "`tool.state.replay` REPLY carries `(epoch, ringFloorSeq)`. Defined behavior when gap > N: shim reports the transitions it has, then raises a dedicated 'state-report discontinuity' alarm CEID and reports current state as a fresh snapshot (prev=unknown, no edge logic). A gap spanning a ToolManager restart (epoch bump, ring emptied) triggers the same behavior — detectable via epoch change." ✓ **Fixed in docs.**

### C8-MAJ-26 — Crash-point matrix is incomplete: §7.5 three-step ordering has two crash windows, not one; `Received`-state recovery unspecified
**Source:** TS8-1 + TS8-2 (test-strategy)  
**Resolution (document fix):** Update §7.12 `CrashAt` spec: "Split into: `after-append/before-HW-persist`, `after-HW-persist/before-ACK`, `after-ACK/before-routing (entry Received, no legs)`, `after-lease/before-send (leg InFlight, nothing sent)`, `after-sink-send/before-MarkSinkDone`, `mid-append`. Add §7.4 rule: 'startup re-routes Received entries (those with no legs get a routing pass before the WAL actor accepts new events).' Add G-8 variant: crash(a) + prior deletion of top-of-sequence entry → restart → fresh wafer accepted. Add publisher-journal convergence oracle to G-8: after crash-at-after-ACK/before-tombstone + restart + redelivery, publisher journal converges to empty within redelivery schedule bound." ✓ **Fixed in docs.**

### C8-MAJ-27 — No GEM shim test suite specified anywhere in the design
**Source:** TS8-3 (test-strategy)  
**Resolution (document fix):** Add `GemShimHarness` suite to §1.3.4 (or cross-reference from §7.12): "GS-1: HCACK=4 accept + completion-CEID sequencing; GS-2: HCACK denial on reader thread (no Task.Wait); GS-3: 4-state machine transitions including start-in-degraded and handshake-not-flag-read; GS-4: tool.state.replay ring recovery of multi-hop cycle; GS-5: no edge logic on prev=unknown snapshot; GS-6: ttl+margin < E30 fail-fast at config load; GS-7: gap > N → state-report-discontinuity alarm + snapshot. Wire GS-1..GS-7 into CI tier table." ✓ **Fixed in docs.**

### C8-MAJ-28 — Net48 composite assertions not pinned to CI tier; nightly tier may silently degrade to net8-only
**Source:** TS8-6 (test-strategy)  
**Resolution (document fix):** Update §4.4 tier table nightly row: "nightly composites run the net48 client (x86 and x64) against the net8 broker; the net8 client variant additionally runs for broker-side assertions. Net48-nightly-green is an explicit P1a entry criterion." ✓ **Fixed in docs.**

---

## MINOR findings (resolved in sweep)

| ID | Source | One-line summary | Resolution |
|---|---|---|---|
| C8-MIN-1 | CNN8-4 | ChildConfig in §1.3.3 needed `ServiceAccount` field | Added with C8-MAJ-7 |
| C8-MIN-2 | CNN8-5 | :5060 loopback but gets inbound firewall rule in §4.4 | Remove :5060 from firewall row; loopback-only |
| C8-MIN-3 | CNN8-6 | "Replay paced by broker credit" — no credit frame in §6.4 | Clarify: pacing = NACK/RESUME + 32-message in-flight window; delete "broker credit" phrase |
| C8-MIN-4 | CNN8-7 | CMM proxy hung-AOI (process alive, not serving) hangs call to long deadline | Add short connect-deadline distinct from op deadline; UNAVAILABLE/connect-fail → `tool-gui-unavailable` |
| C8-MIN-5 | CNN8-8 | :5007 cert rotation (not expiry) has no procedure | Add to §6.8.3: rotation = side-by-side install + hot rebind preferred; or drain-then-restart |
| C8-MIN-6 | CNN8-9 | Endpoint manifest missing HSMS port, ADC :5000, legacy child ports, diagnostic REST port | Complete §1.4 endpoint table |
| C8-MIN-7 | CON8-7 | ToolHost STOPPING latch has TOCTOU through backoff | Re-check `Stopping` after backoff in §1.3.3 rule |
| C8-MIN-8 | CON8-8 | Dual-run comparator: entry access immutability not stated; unmatched shadow entry lifetime undefined | Add to §7.11: comparator receives immutable envelope snapshots; unmatched shadow entries have TTL/cap |
| C8-MIN-9 | CON8-9 | T-L4 bound has zero arithmetic margin; live-rate assumption unstated | Fixed with C8-MAJ-18 |
| C8-MIN-10 | LOAD8-5 | Runaway class-A publisher has no rate-sanity guard | Add per-topic publish-rate sanity alarm to §6.5 |
| C8-MIN-11 | LOAD8-6 | 4:1 live:drain quantum has no enforcement point or unit | Add to §7.8: quantum enforced per sink lane in DrainScheduler, unit = sink-legs |
| C8-MIN-12 | LOAD8-7 | Class-C queue depth and broker unacked in-flight bound are numberless | Add to §6.9: class-C = 256; broker unacked in-flight per (topic,subscriber) ≤ 64 |
| C8-MIN-13 | OPS8-11 | Service account provisioning has no deliverable; ROT/COM cross-account activation unverified | Add to Wave-0 deliverables and pre-P0 checklist |
| C8-MIN-14 | OPS8-12 | No fleet disk-capacity census behind 4 GB WAL + journals | Add fleet disk-headroom census to Wave-0 measurements |
| C8-MIN-15 | CON8-7 (consistency) | Broken cross-refs in doc 07 (§7.11→§7.13, §7.10 CLI, G-5c) | Fix cross-ref numbers |
| C8-MIN-16 | CON8-8 (consistency) | Nonexistent finding ID SEC7-8 in §1.4 ports row | Fix to SEC7-7 |
| C8-MIN-17 | CON8-9 (consistency) | R-TS-2/R-TS-3 IDs swapped across 4 docs | Fix per stage-review.md canonical mapping |
| C8-MIN-18 | CON8-10 (consistency) | `Acl.Deny` enum member doesn't exist; `schemaVersion = 1` stale | Fix to `Acl.None`; bump to `schemaVersion = 2` |
| C8-MIN-19 | CON8-11 (consistency) | Snippet 14 half-applies CC7-11: `StartAll` vs `StartAllAsync` | Fix method name in snippet and class diagram |
| C8-MIN-20 | CON8-12 (consistency) | View 2 draws SecsGemGui outside ToolHost box | Move node inside ToolHost subgraph |
| C8-MIN-21 | DI8-6 | tool.state audit-copy TTL/ring has no mechanism or state-machine state | Add `AuditExpired→Deleted` to §7.4 state machine |
| C8-MIN-22 | DI8-8 | Journal compaction orphan-.tmp handling unspecified (unlike WAL) | Add startup rule to §6.5 (mirror WAL rule) |
| C8-MIN-23 | TS8-4 | T-L4 live traffic profile unquantified; TsmcSink margin is zero | Fixed with C8-MAJ-18 |
| C8-MIN-24 | TS8-8 | P0 measurement kit has no infrastructure spec | Add "P0 measurement kit" subsection to §5.2 |
| C8-MIN-25 | CON8-6 (consistency) | Doc 00 review-confidence claim is two cycles stale | Update §0.4 and README to 8-cycle status + D1–D3 pointer |

---

## OPEN-DECISIONS added this cycle (D15–D21)

| ID | Topic | Blocks | Owner |
|---|---|---|---|
| D15 | Split SecsGemGui.Net: headless GEM engine (ToolHost child) + interactive viewer in operator session — new design work, unestimated | Wave 1 GEM entry | Product/GEM owner |
| D16 | :5100 management-NIC binding: confirm the management LAN NIC identity per site type (single-NIC tools, two-NIC tools, VPN-only sites) | Wave 0 installer | Ops/Infrastructure owner |
| D17 | AOI code change: disable kill-by-name/spawn path in `clsInitAOI.cs` (Wave-0 AOI patch sign-off) | Wave 0 / P1a | AOI dev team |
| D18 | Service account provisioning method for ChildConfig `ServiceAccount` field: gMSA vs local service accounts, password lifecycle on air-gapped fabs | Wave 0 | Security work-stream |
| D19 | ToolHost installer state machine (3→1 transition) — implementation owner, installer test plan | Wave 0 | Installer/DevOps |
| D20 | Gateway binary rollback WAL compatibility: WAL export path or N-1 backward-readable format — approach decision | Wave 0 / P1a | Gateway dev team |
| D21 | ToolHost correlated-failure blast radius: exempt GEM from KILL_ON_JOB_CLOSE, or gate GEM manifest entry on ToolHost stability gate | Wave 0 | Product + GEM owner |

---

## Feasibility dimension

**Verdict: READY** — 3 MINORs, 0 CRITICAL, 0 MAJOR. All specific code-level citations resolved exactly.

| ID | Finding | Severity | Resolution |
|---|---|---|---|
| FEA8-1 | `loader.events` ACL in `01-bus-contracts.cs` set to `Acl.EfemServer`; AOI_Main (not EfemSrv) is the publisher | MINOR | Fixed in `01-bus-contracts.cs`: `publishers: Acl.AoiMain` |
| FEA8-2 | `frmJobTab.cs` has 2 live `Fire*` call sites; not in the P2 12-file list in `04-impact-analysis.md §4.2`; `frmProduction.cs` role as definition host not clarified | MINOR | Fixed in `04-impact-analysis.md §4.2`: 13-file list, frmJobTab added, frmProduction role clarified |
| FEA8-3 | `clsInitAOI.cs:406,458,478-484` kill-by-name path unmitigated in implementation docs | MINOR | Addressed by C8-CRIT-4/D17 resolution (new process name `ToolConnect.Service.exe`) |

---

## Live bugs found in shipped code (independent of design)

Previously recorded (LB1–LB5): no new live bugs reported by the 7 completed dimensions.

Additional code observations:
- `ToolGateway.Tests`: 92 xUnit facts, but `FailedMessagesHandlerTests.cs:63-67` silently early-returns (`return; // Cannot test without write access`) — green-by-skip. The GatewayHarness spec should explicitly ban conditional-skip fixtures.
- `frmJobTab.cs:820-843`: `DeleteAllJobsExcept` deletes job dirs with no gateway coordination (drives DI8-3/C8-MAJ-23 fix).
- `clsInitAOI.cs:406,458,478-484`: kill-by-name gateway spawn path (drives OPS8-3/C8-CRIT-4 fix).

---

## Final verdict

**DESIGN READY — cycle 8 complete.**

All 8 dimensions reviewed. All 8 unique CRITICALs and 28 unique MAJORs resolved (25 by doc/code-snippet fixes; 6 escalated to OPEN-DECISIONS D15–D21 as human-gated decisions). Feasibility reviewer returned READY with 3 MINORs only.

**Verification pass (same session):** independent verification agent confirmed all 8 CRITICAL and all 11 sampled MAJOR resolutions landed correctly. Three companion-text sweep misses found (§7.13 intro "push" wording, broker class diagram `128`, test-12 rationale) — all fixed in the same pass. No new CRITICAL or MAJOR contradictions introduced by the fixes.

**Standing conditions (open-decisions, require human sign-off before the gated phase):**
- D15: SecsGemGui.Net session-0 split (required before Wave 1 GEM entry)
- D16: :5100 management-NIC binding per site type (Wave 0)
- D17: AOI code change — disable kill-by-name gateway path (Wave 0, AOI dev team)
- D18: Service account provisioning for ChildConfig (Wave 0, Security work-stream)
- D19: ToolHost installer 3→1 state machine (Wave 0, Installer/DevOps)
- D20: Gateway binary rollback WAL compatibility (Wave 0/P1a, Gateway dev team)
- D21: ToolHost correlated-failure blast radius / GEM job-object membership (Wave 0, Product + GEM owner)
