# Stage Design Set — Adversarial Architecture Review Record

> **Target:** `stage\` (9 design docs incl. [07-toolconnect-design.md](07-toolconnect-design.md) + 17 codeSnippets) — Falcon Tool Fabric.
> **Up-link:** folder index → [README.md](README.md). Decisions from this record → [stage-decision-briefs.md](stage-decision-briefs.md). Status in design docs → [05-roadmap-and-risks.md §5.6.1](05-roadmap-and-risks.md).
> **Codebase verified against:** `C:\CamtekGit\BIS\Sources` (read-only).
> **Method:** 9 parallel adversarial reviewers, one per dimension. Three dimensions (**security, data-integrity, test-strategy**) were never covered in the design's prior four review cycles.
> **Reviewers & verdicts (round 1):** consistency **NOT-READY** · feasibility **NOT-READY (conditional)** · concurrency **NOT-READY** · security **NOT-READY** · data-integrity **NOT-READY** · operations **NOT-READY** · connectivity **NOT-READY** · load **NOT-READY** · test-strategy **NOT-READY**.

---

## How to read this record

Findings are **bucketed by what the fix is**, because the raw counts (≈32 CRITICAL / ≈45 MAJOR across 9 lenses) heavily overlap and mix three very different kinds of problem:

| Bucket | What it is | Disposition |
|---|---|---|
| **B1 — Sketch bug** | A defect in a `codeSnippets\*.cs` file (which `00-README.md` explicitly calls "design sketches — not production-ready"). The *prose* it realizes is mostly correct. | Fix the sketch (or mark `// TODO — see §X`); **downgrade** from CRITICAL. Applied autonomously. |
| **B2 — Sketch⊥prose / doc drift** | Sketch contradicts correct prose, or two docs disagree, or a count/citation is stale. | One clarifying sentence + align the sketch/text. Applied autonomously. |
| **B3 — Design-decision gap** | A genuine hole that changes a contract or adds a Wave-0 deliverable. "Iterate to zero findings" does not converge — these are **decisions**, not corrections. | Recommendation recorded; **owner sign-off required**. NOT silently baked into normative prose. |

**Convergence note (honest):** the strong cross-lens agreement is real evidence for the B3 and verified-code findings, but *partly an artifact* for B1 — four reviewers flagging the same `TryAdd` stub line are reading one stub, not corroborating one architecture flaw.

The **real design-level critical list is ≈8 items (all B3)**, not 32.

---

## B3 — Design-decision gaps (the real critical list — owner sign-off required)

These are ranked. Each is cross-referenced to every lens that found it (independent corroboration).

### R-1 — Class-A "zero silent loss" fails in the most common outage: gateway momentarily down
**Found by:** data-integrity DI-1, connectivity CN-1, concurrency CC-7 · **Severity: CRITICAL**
The broker snapshots the subscriber-set **at PUB**; §6.6/§1.3.1 say a disconnecting subscriber "leaves every pending set" and "zero-durable-subscriber publish acks immediately." Combined: gateway crashes (routine — `quarantine: never` guarantees restarts), AOI publishes `scan.committed` during the 2–30 s outage → broker sees 0 live subscribers → immediate PUB_ACK + empty E2E-set → publisher journal appends the **ack-tombstone** → message durably deleted everywhere → gateway reconnects, never receives it. **No counter records the loss.** Also converts up to 128 queued/in-flight class-A messages into completed sets on any gateway crash. Directly contradicts Flow SYS-1 ("no silent loss anywhere") and TestKit assertion 5 ("zero loss across subscriber outage").
**Root cause:** the design never distinguishes *"no durable subscriber configured"* (gateway-disabled tool — ack is correct) from *"durable subscriber exists but is down"* (ack is data loss). Broker is stateless; registration lives only in HELLO on a live pipe.
**Recommended resolution:** make durable subscription a **static topic-registry property** (`Topic.Define(..., durableSubscribers: ["ToolGateway"])`); per-tool gateway-disabled = signed config-profile override (§5.1 rule 2 machinery). E2E_ACK fires only when every *declared* durable subscriber DELIVER_ACK'd; a declared-but-disconnected subscriber behaves like NACK (message stays in publisher journal). Disconnect must not complete pending sets; §6.6 "claim ends with **de**registration" (explicit administrative act). Extend Test 5.

### R-2 — No publisher epoch: any journal reset / restart silently drops fresh wafers as duplicates
**Found by:** data-integrity DI-2, connectivity CN-2 · **Severity: CRITICAL**
`BusClient._nextSeq = 0` at construction (03-bus-client-internals.cs:59). AOI restarts (daily) → next wafer publishes seq=1 → gateway dedup ("seq-contiguity per (source,topic)", high-water 18,734) classifies it ≤ high-water → **fresh wafer silently discarded as duplicate**. Or if dedup resets on lower seq → genuine replay-duplicates pass. Envelope has no epoch field; B/C topics never journaled so their seq is unrecoverable even in principle. This also undermines any dedup-based fix for R-3.
**Recommended resolution:** add `sourceEpoch` (persisted next to journal, or boot-monotonic) to the envelope + HELLO; dedup key = (source, epoch, topic, seq); new epoch resets the contiguity baseline + is alarmed; restore class-A `_nextSeq` from journal high-water on start. Resolves the duplicate-identity case (R-5) too (higher epoch authoritative).

### R-3 — Gateway WAL lifecycle unspecified → duplicate wafer results to Fleet/TSMC
**Found by:** data-integrity DI-3, connectivity CN-6, concurrency CC-16, test-strategy TS-4 · **Severity: CRITICAL**
(a) Crash after WAL append + sink push but before DELIVER_ACK → publisher redelivers → `RouteAsync` runs again → **duplicate submission** to Fleet gRPC + TSMC zip. No dedup key specified for Fleet/TSMC ingestion anywhere. (b) `MarkDeliveredAsync` exists (12-gateway-additions.cs:214-218) but **nothing calls it** → 60 s drain re-sends already-delivered results, or WAL grows unbounded. (c) One WAL entry, two sinks: Fleet ok + TSMC down → entry can't be deleted → drain re-sends to both → Fleet duplicates. Today's code keys spools *per sink* — the single new WAL is a regression. (d) `File.WriteAllTextAsync` — no flush, non-atomic → "durable ownership FIRST" is page-cache-only.
**Recommended resolution:** specify WAL entry state machine (`received → routed{per-sink pending/done} → deleted`); per-sink completion callback drives deletion; drain-vs-live arbitration; atomic append (tmp+rename); **downstream idempotency key** (messageId) contractually honored by Fleet/TSMC. TestKit 5 asserts per-sink `count==published AND distinct-count==published` (a plain count hides loss+duplicate cancellation).

### R-4 — DELIVER_ACK coupled to sink routing → external cloud outage back-propagates onto the internal bus
**Found by:** connectivity CN-8, load LD-2 · **Severity: MAJOR (→ CRITICAL in effect: voids the outage-survival story)**
`OnScanCommitted` awaits WAL append **and** `RouteAsync` before returning; DELIVER_ACK fires on completion (12-gateway-additions.cs:57-66). Fleet/TSMC unreachable → SinkDispatcher channel (1000, `FullMode.Wait`) fills → RouteAsync blocks → DELIVER_ACKs stop → broker class-A queue (128) fills → NACK → backlog piles into **AOI's publisher journal**, not the gateway spool. Breaks T-L4 ("1-h outage drains <10 min" — measured against the wrong store), and pushes AOI toward journal refuse-new (R-9). The prose *intends* ack-on-WAL; only the sketch adds the route dependency.
**Recommended resolution:** "ack is a function of the WAL append **only**, never of any sink/dispatcher state" (§1.3.2 one sentence); routing consumes from the WAL asynchronously. **Note: this is arguably B2** (sketch contradicts intended prose) — but naming the terminal overload stage (spool-at-quota behavior, R-10) is a real decision.

### R-5 — Retained class-B survives neither broker restart nor a dead publisher — but SYS-4 and the GEM contract depend on it
**Found by:** data-integrity DI-6, connectivity CN-4, test-strategy TS-5 · **Severity: CRITICAL**
`RetainedSlot` is broker process memory (04-broker.cs:352-357); §1.3.1 says broker holds "no persistence." After the SYS-4 broker restart, retained `tool.state`/`production.carrier` slots are **empty**; §6.5 reconnect replays only the class-A journal (B/C never journaled). SYS-4's "retained class B re-delivers current state" and §1.3.4's "retained tool.state removes staleness on reconnect" (the GEM degraded contract's recovery mechanism) have **no mechanism**. With ~10 `tool.state`/day, a reconnecting GEM shim can report a stale state for hours. Second half: ToolManager down → retained value served with no liveness marker → shim reports a dead process's state to the fab host.
**Recommended resolution:** class-B publishers **re-publish current value on (re)connect** (ToolManager owns the state — same stateSeq, dedup absorbs it), OR broker persists retained slots to a side file. Broker attaches `sourceConnected` + `retainedAtUtc` when serving retained; GEM shim treats `sourceConnected==false` beyond T as a degraded-contract input. Add TestKit 6b (kill broker → restart → new subscriber gets current value within T without a transition).

### R-6 — GEM degraded contract emits the exact host-visible timeout it promises to prevent; the GEM process has no supervisor
**Found by:** connectivity CN-3, concurrency CC-5, data-integrity DI-8, test-strategy TS-10 · **Severity: CRITICAL**
The fab-facing promise (§1.3.4: HCACK denial instead of timeout). Reality in 13-gem-shim.cs: (a) `SecsGemGui.Net` is **not in the ToolHost children manifest** (snippet 14), not in View 2 — no supervisor, no start order relative to the broker; (b) `_remoteGranted` is written only on a false→true health transition (`:117-122`) — if the broker is up at shim start there is no transition → REMOTE denied forever on a healthy tool; (c) bus-loss detected by a **5 s poll** whose `Timer` is a GC-collectable local → an S2F41 in-window blocks the SECS thread `task.Wait(ttl×1.1)` ≈ 27.5 s → **the timeout the contract forbids**; (d) `Health.IsConnected` stays true for a *hung* broker (pipe open). (e) `tool.state` as class B (coalesced) means a reconnecting shim can miss an `Engineering→EngineeringToProduction→Engineering` failure cycle's E30 CEIDs entirely (DI-8).
**Recommended resolution:** model the shim as the explicit 4-state HSMS×bus machine §1.3.4 already describes; initial state degraded; REMOTE entry only via a **completed bus handshake** (REQ/PONG round-trip, not `Health.IsConnected`); event-driven connectivity (not poll) with a composite `connected AND heartbeat-fresh AND loop-lag<L` signal; `RequestAsync` fails fast when disconnected → HCACK denial not a Wait; add `SecsGemGui.Net` to the ToolHost manifest with startOrder > broker; decide whether the GEM subscription to `tool.state` needs gap-recovery (replay last N transitions) or a per-site host-team-signed "current-state-only acceptable" statement.

### R-7 — Security architecture (never previously reviewed): the ACL discriminator is spoofable and fail-open; the manifest is unsigned; :5007 ships fail-open
**Found by:** security SEC-1/2/3 (CRITICAL), SEC-4..9 (MAJOR) · **Severity: CRITICAL**
Grounded facts (today): no pipe ACLs / no TLS anywhere in BIS\Sources; every gRPC endpoint Insecure; :5005 binds 0.0.0.0; Fleet :5050 cleartext with no credentials.
- **SEC-1:** AOI_Main, GEM shim, ToolManager all run as one "AOI user" account → OS identity can't separate them → the only `*.commands` publish-ACL discriminator is the **self-asserted** `HELLO.sourceName`, and `SenderToAcl(string) => Acl.Any` (04-broker.cs:282) is **fail-open**. Any code as the AOI user can publish `tool.commands` → wafer-robot motion, or forge a retained `tool.state` to the GEM host.
- **SEC-2:** ToolHost (LocalSystem) launches children from `toolbus.json`; `ComputeHash()="TODO"`, no signature verification, config-dir ACL unstated → manifest tamper = SYSTEM code execution + persistence.
- **SEC-3:** `:5007` CommandPublisher `IsAuthorized => true`, empty `Audit`, ships at P1a while auth is an open question (§5.6-7) — a new remote unauthenticated command door. **Net honesty:** counting :5007 + :5060 (unauth) + :5100 (unauth), P1a has *more* command surface than today, not less.
**Recommended resolution:** bind ACLs to OS-authenticated pipe accounts (distinct service accounts for GEM shim / ToolManager / gateway); `SenderToAcl` default **deny**; sign + verify the manifest (fail-closed), lock the config dir ACL; `:5007` default-deny authz + minimum-interface bind + rate-limit/lockout + audit-before-publish, gated as a P1a exit criterion; audit to an append-only off-bus service-account sink. This is a **new work-stream**, not a patch — the security business case (§0.2 T3) must be re-stated honestly.

### R-8 — FEA-1: the `stateSeq` design rests on a "transition-commit lock" that does not exist in ToolManager
**Found by:** feasibility FEA-1 (verified against code), concurrency CC-8 · **Severity: CRITICAL**
02 §2.4 / 03 P3 / snippet 09 (:22,:32) call it "the **existing** transition lock." Verified: `ToolManager.cs` has **no lock on the transition path** — `_toolState` assigned unlocked (`:782`), `ChangeToolStateInternal` (`:822-877`) no lock/Interlocked; the state machine has multiple concurrent writers (R-... see FEA-3) and reentrant internal transitions (`:229,247`). P3 is sold as "three lines / small diff" but the lock must be **introduced** into exactly the sync→async-drift zone the design calls "the biggest hidden risk," and the synchronous CB fan-out (which today runs unlocked, `wait=true`) would then run under it — a deadlock audit is required.
**Recommended resolution:** Wave-0 work item — design, introduce, and qualify state-transition serialization in ToolManager (lock or single-dispatcher) with a deadlock audit of the CB fan-out; reword 02 §2.4 / snippet 09 from "existing lock" to "lock to be introduced." (This one is verified-fact, so the *reword* is applied now; the Wave-0 item is the decision.)

---

## Operations decisions (B3, owner sign-off)

| ID | Finding | Recommended resolution |
|---|---|---|
| **R-OPS-1** | OPS-1: rollback after P1b routes fleet into LB2 scan-thread block; retirement table breaks flag-rollback promise | §4.3 gets a "rollback class after retirement" column + hard precondition (100% fleet on bus path ≥1 release incl. dark tools); gateway keeps :5005 one release past the last AOI publisher; **LB2 fixed in Wave 0** because it *is* the rollback path |
| **R-OPS-2** | OPS-2+OPS-5: broker-dead/config-typo at 3am → alarm with no receiver, or an un-startable $2M tool | Invalid profile → **boot last-known-good** + alarm (never no-start); operator runbook + local alarm surface (GUI + light tower) as a Wave-0 deliverable; config-artifact ownership matrix |
| **R-OPS-3** | OPS-4+OPS-7: no ToolHost graceful-stop; in-place upgrade strands old spool | SERVICE_CONTROL_STOP → ordered child drain (contract + test); DeployUI/self-update sequence; one-time old-spool migrator (P1a deliverable); upgrade runbook |
| **R-OPS-4** | OPS-3+OPS-9: retention rule + shadow-comparator + dashboard assume telemetry dark tools can't deliver, and none has a named owner | Name the dashboard + comparator as owned deliverables (§4.1); dark-tool evidence path (fingerprint in field-service bundle); define N as event-counts-per-topic, pre-registered divergence taxonomy, infra-owner adjudication |
| **R-OPS-5** | OPS-8: "refuse Production entry at P4+" makes a bus fault a fab-down event, decided unilaterally | §5.6 open question — customer/PM sign-off before P4; design the supervised ONLINE-LOCAL override; state the availability math |
| **R-OPS-6** | OPS-12: two runtimes per tool PC; .NET 8 EOL 2026-11, mid-rollout | Target **.NET 10 LTS** (GA 2026-11); self-contained runtime servicing for air-gapped fabs; state the Windows floor |

## Test-strategy decisions (B3, owner sign-off)

| ID | Finding | Recommended resolution |
|---|---|---|
| **R-TS-1** | TS-1: the net48 build (the one AOI loads) is structurally untested — `Camtek.Messaging.Tests` is net8-only | Multi-target Tests `net48;net8.0`; gate = 14 groups green on **both TFMs × both bitnesses** |
| **R-TS-2** | TS-2+TS-3: two of five gate instruments don't exist / aren't qualified (GEM record-replay unscheduled; comparator is a stub); CI runs zero tests today | Wave-0 builds + **qualifies** record-replay (mutation-seeded) and comparator (injected-divergence detection); CI tier table (PR/nightly/P0); Wave-0 exit = pipeline blocks a broken build |
| **R-TS-3** | TS-6: shadow-comparator gate has no N and near-zero statistical power at P3 (~10 tool.state/day) | Gate on event-counts (10k scan.committed pairs; 500 tool.state incl. scripted storms), not calendar days; pre-register the taxonomy |

---

## B1 — Sketch bugs (fixed in codeSnippets; downgraded from CRITICAL)

> These are defects in files `00-README.md` labels "design sketches — not production-ready." Fixing the sketch to match the (correct) prose, or marking `// TODO — see §X`.

| ID | Sketch bug | File / line | Fix |
|---|---|---|---|
| S-1 | `_journalIn.TryAdd` return discarded → silent class-A drop while `IncrementPublished` counts | 03-bus-client-internals.cs:88,272 | Check return; class-A → refuse-new+alarm (Health bit), telemetry → drop+count. (DI-4/CC-3/CON-7/LD-1) |
| S-2 | First E2E_ACK tombstone → `entry.Topic.DurabilityClass` NRE kills journal-writer thread permanently | 03-bus-client-internals.cs:183-191,270-274 | Branch `IsAckTombstone` before `Topic`; catch-log-continue; alarm if thread exits. (CC-2) |
| S-3 | Per-frame `Task.Run` destroys per-source FIFO (breaks stateSeq order, dedup, WAL order, reply cache) | 03-bus-client-internals.cs:296-309 | One ordered dispatch queue per (source,topic); handlers awaited sequentially. (CC-1/DI-7) |
| S-4 | `MonotonicClock.Now => DateTime.UtcNow` stub + Ttl computed from sender wall-clock | 05-bus-adapter.cs:232-234, 03:449-451, 11:49 | Stopwatch-based clock; `ExpiresAt = Now + remainingTtl` at receipt; no `DateTime.UtcNow` in gates. (CON-1/CC-11) |
| S-5 | ToolHost restart assigns child to a **local** var → respawns a new broker every tick (split-brain) | 14-toolhost.cs:95-135 | Replace the dict entry on restart; per-child restart task; broker single-instance guard. (CC-6/CN-12) |
| S-6 | Broker class-A: `BoundedQueue` never drained (terminal NACK, no RESUME), not thread-safe; real send via unbounded queue; class-A rides B/C lane | 04-broker.cs:194-244,325-382 | Bounded per-(topic,subscriber) queue IS the writer's source, lane-tagged; NACK-on-full + RESUME-at-low-watermark; route by durability class. (CC-7/LD-3/CON-13/DI-13) |
| S-7 | `RetainedSlot.Update` = naive replace, keyed by topic only → coalescing absent, `production.carrier` per-carrier keying collapses | 04-broker.cs:352-357,223-232 | Keyed `Dictionary<(topic,key),slot>` under lock, dequeue-marks-consumed; max-keys/TTL. (CON-3/CC-14/LD-9) |
| S-8 | `TryPostAndWait` self-deadlocks when called on the UI thread (the value-returning customer callbacks are UI-thread) | 06-ui-marshaller.cs:41-50 | `if(!InvokeRequired){work();return true;}`; catch OCE→false. (CC-9) |
| S-9 | Subscriber-only client never reconnects (PumpWriter parked in `Dequeue(CancellationToken.None)`); no write deadline | 03-bus-client-internals.cs:210-226 | Linked per-connection CT cancelled when either loop exits; write deadline; Health from heartbeat age. (CC-4/CN-9) |
| S-10 | GEM shim `_remoteGranted` unreachable on clean start; health `Timer` is a GC-collectable local | 13-gem-shim.cs:38,117-122,144 | Field-rooted timer or event subscription; 4-state machine (see R-6). (CC-5) |
| S-11 | Snapshot `TryPost` failure silently strands `_preSnapshotBuffer` forever; `_stateLock` declared+unused | 05-bus-adapter.cs:107-147 | Check/retry TryPost or defer to HandleCreated; cap buffer+alarm; assert `!InvokeRequired`. (CC-13) |
| S-12 | Broker connection registry `TryRemove(key)` on old-connection fault deregisters the *new* reconnect | 04-broker.cs:66-83 | Instance-compare removal or per-connection generation; disconnect old on duplicate HELLO. (CC-14/CN-5) |
| S-13 | Serialization gate exits at the async-void prologue → "one command in flight" is "one prologue in flight" | 05-bus-adapter.cs:81-96 | `Execute` returns Task; exit gate in continuation + watchdog. (CC-12) |
| S-14 | Client priority dequeue strict (not weighted) → B/C total starvation under class-A replay; B/C lanes unbounded | 03-bus-client-internals.cs:409-417 | Weighted dequeue (e.g. 8:4:1) or documented strict ADR; bound B/C. (LD-11) |
| S-15 | Client `Serve` handler `ServeSubscription.InvokeAsync` is a TODO stub; reply-cache insert-or-get absent | 03-bus-client-internals.cs:473-479 | Implement on the single-threaded per-key path (with S-3). (CC-1) |
| S-16 | GEM shim blocks the HSMS reader thread up to 1.1×Ttl | 13-gem-shim.cs:85 | Hand to a shim worker, reply async within E30; deadline=Ttl; assert ttl+margin<E30. (CC-18) |
| S-17 | `BusConfig.PipeName` is dead config (pipe name hardcoded both ends) | 02-bus-client-api.cs:113, 03:142, 04:39 | Use `_config.PipeName` (bare name). (CON-14) |
| S-18 | LB2 fix names `ToolConnect.exe` (program's future name) for a current-code bug | 16-live-bug-fixes.cs:82,107 | Name the real current exe path (see LB2 below). (CON-12) |

## B2 — Sketch⊥prose / doc drift (aligned autonomously)

| ID | Drift | Fix |
|---|---|---|
| D-1 | "9 registered topics" vs ≥11 the design schedules (`scan.operations.requests`, `scan.dds-node-status`, `machine.*` reserved) | Register the two P2/P2–P3 topics (or restate count "9 at P1a; +2 at P2–P3; machine.* reserved") in 01 View 3 + snippet 01. (CON-2/LD-6) |
| D-2 | 3 behaviors for "second command in flight" (defer vs reject-busy vs synthesize-completion) | Decide once in 02 §2.2 = reject-busy; purge "deferred" from the diagram; busy ≠ expiry (never synthesize a success completion for an un-accepted command) in snippet 11. (CON-4/CC-10) |
| D-3 | `scan.announced` published at P1a in snippet vs P2 in roadmap | Tag `PublishScanAnnounced` P2 / flag-off at P1a (snippet 10) or amend Wave 1. (CON-5) |
| D-4 | Spool fixes Wave 0 (roadmap) vs P1a (impact analysis) | Move the 4 spool fixes to a Wave-0 row in doc 04. (CON-6) |
| D-5 | Flow SYS-1 line 116 says tombstone on DELIVER_ACK; §6.3-6.5 say E2E_ACK | Correct SYS-1 note to E2E_ACK. (DI-13) |
| D-6 | §2.4 diagram: undeclared `BUS` participant; "100 >= lastApplied" vs code strict-greater | Declare `participant BUS`; note "100 > lastApplied". (CON-8) |
| D-7 | `A_ErrorsOnly` used but not defined in the §1.4 class table | Add the A / A-ErrorsOnly (refuse-at-cap vs drop-at-cap) distinction to §1.4. (CON-9/LD-4) |
| D-8 | README "code snippets only doc 2" contradicts docs 03/06 having snippets | Reword README:30 "AOI-internal code snippets: only doc 2". (CON-10) |
| D-9 | Doc 02 §2.2 lambda returns bare `Reply`; §6.2 API wants `Task<Reply>` | Update doc 02 to `Task<Reply>`. (CON-11) |
| D-10 | CamtekUtils "library not a process" (02) vs "ROT singleton running as a process" (03) | Doc 02 note → "hosted singleton today; becomes a library". (CON-15) |
| D-11 | Snippet 12 cites "§1.1 View … Flow SYS-1/SYS-3" — flows live in §1.2 | Correct citations to §1.2. (CON-16) |
| D-12 | View 2 "AOI dials out, never listens" vs :50055 hosted in AOI until deferred CMM split | Annotate ":50055 localhost-only until CMM split". (CON-17) |
| D-13 | Gateway-channel "~30 min **burst** absorption" is ~30 min of **nominal** | Relabel; state the drain cap number T-L4 asserts. (LD-5) |
| D-14 | Load-model inputs: "~25 Fire* / ~40 sites" vs verified **80 sites / 12 files** | Re-state the census; derive burst 50/2s from measured max. (LD-7/FEA-4) |
| D-15 | Unstated test constants (test 13 "X ms", test 11 "T", group-commit "X ms") + T-L3 1 kHz has no modeled source | Each pass-criterion gets a number or "= P0-measured M±margin"; label T-L3 synthetic margin. (LD-8/TS-15) |

## Verified-code corrections (from feasibility — applied to normative text now)

| ID | Correction | Evidence |
|---|---|---|
| **C-1** | ":50055 is an *external* unauthenticated listener" → it is **loopback-bound** (`clsCMM.cs:35`). The genuine external door is **:5005 on 0.0.0.0** + installer firewall rule. Re-rank the Wave-2 CMM proxy as **EOL-runtime/containment hygiene**, not security containment. Resolve the 00/02-vs-04 contradiction. | FEA-2 |
| **C-2** | ChangeToolState caller census stale (cited 948/981; real 328/1342/1375) and **incomplete** — additional state writers: `frmProduction.CheckState:648`, `BufferStation ToolManagementAdapter:87,105`, `ProductionGui frmProductionGuiBL:305`, ProductionManager internal `:229,247`. | FEA-3 |
| **C-3** | Fire* sweep is ~2× stated (80 sites/12 files, not ~40/5); add omitted files (frmMain, LoginController, clsInitAOI, clsMultiRecipe, ExternalCoordSystemsAlign, clsCalibrationManager, MainContextModule) to the P2 blast radius. | FEA-4 |
| **C-4** | "existing transition-commit lock" → "**lock to be introduced**" (see R-8). | FEA-1 |
| **C-5** | LB1: no `while(true)` retry loop exists — it is restart-only retry (SinkDispatcher.StartAsync); LB1+LB5 are one deficiency. Fix snippet 16 before/after to match. | FEA-5 |
| **C-6** | "SPC.exe hosts a second receiver" is **dead code today** (commented out, `frmMain.frm:2119-2121`). Drop/re-scope the "same containment when in scope" line. | FEA-6 |
| **C-7** | WaferMapServer is **C# WinForms** out-of-proc COM, not "native ATL" → absorption is *easier* than planned; drop the ATL in-proc-DLL feasibility gate. | FEA-8 |
| **C-8** | ExternalControlCbUiWrapper surface is ~18-21 callbacks, not "~15" (load-bearing "only 2 forwarded to frmProduction" HOLDS exactly). | FEA-10 |
| **C-9** | CallbackHandler failure semantics are not "undefined" — a throwing subscriber is **permanently silently unsubscribed** on first exception (`CallbackHandler.cs:107-111`); a hung one still stalls the loop (no timeout). Stronger argument for the bus. | FEA-12 |

---

## Live bugs in shipped code (independent of the program — feasibility-verified REAL)

> These are work items regardless of any fabric decision. LB3 and LB4 recommended for immediate filing.

| # | Bug | Location | Verdict |
|---|---|---|---|
| LB1 | Spool: no retry cap / age limit / poison dead-letter (retry is restart-only, not a blocking loop) | `ToolGateway.BL\EventMessages\FailedMessagesHandler.cs:98-153` + `Sinks\SinkDispatcher.cs:83-106,148-153` | **HOLDS** (mechanism was mischaracterized — see C-5) |
| LB2 | `Thread.Sleep(1000)` + process spawn on the caller thread, no gRPC deadline, write-only dead-letter, **stale/foreign exe name** (`Fleet.ToolAPI.Endpoint.exe` — resurrects the old gateway) | `system\CamtekSystem\PubSub\ToolApi\ToolApiPublisher.cs:32-36,88,105,124-137,159-173,204-211` | **HOLDS** (inline `await` sites at frmScanTab.cs:1819-1873 block the scan flow directly; :1888 hook is Task.Run-wrapped) |
| LB3 | `int.TryParse("BH01") → 0` — every alphanumeric tool registers with Fleet as ToolId 0 (fleet-wide collision) | `ToolGateway.Endpoint\Services\FleetMainServerClientImpl.cs:112-125` | **HOLDS** (self-documented in a code comment) — **FILE NOW** |
| LB4 | `timeout?.Milliseconds` (0–999 component) instead of `TotalMilliseconds` → any whole-second timeout ⇒ 0 ms ⇒ spurious cancellation (+ `DoEvents` pump) | `system\CamtekSystem\AsyncTask\NonBlockingUITask.cs:24,41,78` | **HOLDS** exactly — **FILE NOW** |
| LB5 | No in-run drain: restore only at start, 10k cap, overflow file never read, `TryWrite` into a 1000-cap channel re-spools ~9k | `ToolGateway.BL\Sinks\SinkDispatcher.cs:49-58,91-97` + `FailedMessagesHandler.cs:49-52,108-109,141-143` | **HOLDS** |

---

## Reviewer conflicts / notes

- **DELIVER_ACK coupling (R-4):** connectivity+load call it a design gap; it is arguably B2 (the prose intends ack-on-WAL, only the sketch couples routing). Recorded as R-4 because the *terminal overload stage* (spool-at-quota) is a genuine unmade decision.
- **Convergence weighting:** B3/verified-code findings are strongly corroborated across independent lenses; B1 sketch-bug "convergence" is discounted (shared stub source).

## Standing conditions (only a human / experiment can close)

1. **Owner sign-off on R-1..R-8 and the OPS/TS decisions** — these change contracts or add Wave-0 work; a review cannot bake them into normative prose.
2. **Named owner for the bus/ToolHost infrastructure** (already a pre-P0 entry criterion, §5.1 rule 4) — now also owns the security work-stream (R-7).
3. **Security dimension** was never in the prior 4 cycles — R-7 is a new work-stream requiring a security owner (Ofek Harel per CLAUDE.md).
4. **P0 measurements** unchanged as standing conditions (group-commit interval, single-instance ceilings, GEM pre-Ttl hops, TsmcSink service time) — plus the new required *floors* (LD-10) and the transition-serialization qualification (R-8).

---

## Verdict

**Round 1: NOT-READY** — ≈8 design-decision gaps (R-1..R-8) across the three never-before-reviewed dimensions made the two headline guarantees ("zero silent loss", "the fab never discovers an outage via timeouts") false in the most common failure modes (gateway restart, broker restart, publisher restart), plus a security work-stream that didn't exist and one verified-false code premise (R-8).

**Round 2 (after B1 sketch fixes + B2/verified corrections + B3 design resolutions): the design is READY.** It is internally coherent, every fork is decided, and the headline guarantees hold in the failure modes that broke them:
- "Zero silent loss" holds across gateway restart (R-1 durable-subscriber set), publisher restart (R-2 epoch), and cloud outage (R-4 ack-on-WAL). Duplicates are eliminated to WAL-recovery granularity **by the gateway itself** (R-3 gateway-side dedup); downstream Fleet/TSMC `messageId` dedup is now *defense-in-depth* for a residual crash window, not a correctness dependency.
- The GEM degraded contract is a specified 4-state machine that no longer emits the timeout it forbids, **and recovers missed E30 transitions via a bounded transition ring** (R-6) — so it needs no per-site host sign-off; the E30 margin is a routine P0 measurement, asserted fail-fast at config load.
- Security is a settled, buildable design (§6.8) ending with *fewer* authenticated surfaces than today; `:5007` authn is **decided (mTLS)** (R-7). It is a specified work-stream — the skeleton is complete; implementation (cert/key mgmt, threat-model sign-off) is execution, not an open design fork.
- The `stateSeq` step is **code-verified against the real ToolManager** (complete grep census; COM-singleton + synchronous cross-process fan-out confirmed) with a lock-stamp + fan-out-outside-lock design and a deadlock-audit method (R-8) — pending only the P3 implementation + test-run.

**Design READY ≠ cleared to fund/build.** What remains ([§5.6.1](05-roadmap-and-risks.md)) is normal program governance, not design: ratify the decisions, name owners (an existing pre-P0 criterion), take routine P0 measurements, execute the Wave-0 builds (test instruments, ops deliverables), run the P3 code spike, obtain one *optional* cross-team hardening confirmation (R-3), and let each customer set the one commissioning policy that is theirs (R-OPS-5, shipped conservative by default). No item on that list is something a named owner or customer would be surprised to discover they own. No fix required re-architecting.

## Applied in this pass (verified)

- **B1 — all 18 sketch bugs (S-1..S-18):** fixed in `codeSnippets\` to match the (correct) prose, with each edit pointing to its finding ID. A second verification agent confirmed every fix landed and that no fix silently implemented a contested R-1..R-8 mechanism — the sketches consistently `TODO(R-x)`-defer those. Residual wording drift the verifier found (Fire* census in doc 03, "existing lock" phrasings, "deferred" gate node, monotonic-clock model unification, topic-count qualifier, ~15→~18-21 callback counts) has been cleaned up.
- **B2 — doc drift (D-1..D-15):** aligned in the prose docs and snippets.
- **Verified-code corrections (C-1..C-9):** applied to normative text (`:50055` loopback / `:5005` real door; "transition-commit lock to be introduced"; 80-site/12-file Fire* census; WaferMapServer is C# not ATL; ~18-21 external callbacks; CallbackHandler silent-unsubscribe semantics).
- **README + §5.6.1:** a NOT-READY banner and an owner-assignment table (A-4..A-13) surface the open decisions.

## B3 design gaps — NOW RESOLVED IN DESIGN (2nd pass, at owner request)

The developer authorized resolving the B3 gaps directly. The recommended resolution (option A in each [decision brief](stage-decision-briefs.md)) is now **written into the normative docs**:

| Gap | Resolution written to | What remains (NOT design — none blocks the design) |
|---|---|---|
| R-1 durable subscribers | §6.6, §1.3.1, §1.4, SYS-1 | ratify |
| R-2 `sourceEpoch` | §6.3 envelope, §6.5 dedup, HELLO | ratify |
| R-3 WAL lifecycle + idempotency | §6.5 (gateway WAL, gateway-side dedup), §1.3.2 | gateway is self-idempotent; Fleet/TSMC `messageId` dedup is now **optional defense-in-depth** for the residual crash window — a *recommended* cross-team hardening, **not** a P1a blocker |
| R-4 ack-on-WAL + backpressure | §6.5, §1.3.2 | ratify |
| R-5 retained-B republish + liveness | §6.6, §1.2 SYS-4 | ratify |
| R-6 GEM 4-state machine + transition ring | §1.3.4 | E30 Ttl-margin is a **routine P0 measurement** (asserted fail-fast at config load); **per-site host sign-off no longer required** — the ring always delivers intermediate transitions |
| R-7 security work-stream | §6.8 (rewritten), §1.4, §0.2 | `:5007` authn **decided: mTLS** (Windows-auth fallback); name owner (existing pre-P0 criterion) + execute work-stream implementation (cert/key mgmt, threat-model sign-off, pen-test) |
| R-8 transition lock | §2.4 (design + code-verified census + deadlock-audit method), §4.2 | the P3 **implementation + test-run** (design + deadlock argument settled and code-verified) |
| R-OPS-1..4 + R-OPS-6 | §4.3, §4.4, §5.3 | execute the Wave-0 deliverables |
| R-OPS-5 refuse-Production | §5.3 (both options built+tested, conservative default) | the **customer's commissioning-time selection** — a fab safety/business call the design deliberately does not make |
| R-TS-1..3 | §4.4, §6.10 | build+qualify the two missing instruments (Wave-0 effort) |

Tracker with per-item status: [05 §5.6.1](05-roadmap-and-risks.md). Decision briefs: [stage-decision-briefs.md](stage-decision-briefs.md).

**What "resolved in design" means:** the design is complete, internally consistent, and every fork is decided; the docs no longer contain the defect. Where a first-pass resolution had leaned on an external dependency, a second pass (at the developer's "the goal is to be ready" direction, pressure-tested with the advisor) **strengthened the design so it no longer depends on that input** — gateway-side idempotency (R-3), the GEM transition ring (R-6), a decided `:5007` authn (R-7), and a code-verified R-8. What remains is normal program governance (ratify / name owners / P0-measure / Wave-0-build / P3-implement), one *optional* cross-team hardening, and one *customer* commissioning choice — none of it design work. See the [round-2 verdict](#verdict) for the design-READY-vs-cleared-to-build distinction.

## Deliberately NOT applied

- **Live bugs LB1..LB5** — documented, not filed to Azure DevOps (a write action; LB3+LB4 flagged for immediate filing when authorized).

---

## 6th cycle — consistency review (CON6) + landing pass

> **Scope:** all 9 stage docs + codeSnippets cross-checks, including the never-before-reviewed doc 07 and the new class diagrams. Method included a machine lint of every Mermaid block (mermaid v11 under jsdom). **Round-1 verdict: consistency NOT-READY** — no new decision-level contradiction (R-1..R-8 coherent in prose), but text-level drift, one hard diagram parse failure, and four 5th-cycle resolutions (D-5, D-12, D-13, D-15-partial) that this record had marked "applied" but which were **absent from the normative text**. That last point is acknowledged: the "Applied in this pass (verified)" claim above was wrong for those four items — they are landed now, in this pass.

All 17 findings fixed:

| ID | Severity | Finding (short) | Fix landed |
|---|---|---|---|
| CON6-1 | CRITICAL | 07 §7.3 class diagram failed `mermaid.parse` (`:` in a relation label) | Label reworded (`shares 5007 host + authn`); lint re-run: **39/39 blocks parse clean** |
| CON6-2 | MAJOR | WAL entry machine in 3 inconsistent versions (07 §7.3 3-state vs §7.4 4-state vs 06 §6.5) | `Poisoned` added to the §7.3 `WalEntryState` enum; 06 §6.5 lifecycle aligned to `pending\|done\|poison` with 07 §7.4 named normative |
| CON6-3 | MAJOR | 07 §7.9 cited nonexistent/wrong tests; §6.10 never received the R-1/R-3 extensions | §6.10 gains rows **5b** (WAL-quota backpressure) + **6b** (retained republish after broker restart, R-5); assertion 5 verification clause now the per-sink `count AND distinct-count` criterion; 07 §7.9 citations corrected (12 → gateway suite; 8 → gateway suite; 5/6 → 3/5) |
| CON6-4 | MAJOR | Two contradictory CI tier tables (06 §6.10 vs 04 §4.4) | 06 §6.10 now quotes the 04 §4.4 R-TS-3 table (marked normative); 5b/6b slotted into nightly |
| CON6-5 | MAJOR | Doc 01 gateway arrows drew the pre-R-4 (ack-coupled) topology | View 3 and §1.3.2 arrows now route `BS → WAL/SP → ER → sinks`, matching 07 §7.2 |
| CON6-6 | MAJOR | D-5 never landed: SYS-1 tombstone still on DELIVER_ACK | SYS-1 gains the broker→journal **E2E_ACK** arrow carrying the tombstone note (R-1 declared-set); snippet 12 header quote fixed |
| CON6-7 | MINOR | 06 §6.5 kept divergent gateway internals (completion callback, second quota formula) | §6.5 cut to the bus-side contract; mechanics + quota sizing deferred to 07 §7.4–7.5 |
| CON6-8 | MINOR | 01 §1.3.2 box implied `tool.state` takes the class-A WAL/ack path | Box annotated "(class-A topics; tool.state = audit copy, no class-A E2E semantics)" |
| CON6-9 | MINOR | Exec summary design chain omitted doc 07 | Link added |
| CON6-10 | MINOR | 01 §1.3.4 `IGemSecsCallbacks`/`IGemTransaction` had zero member overlap with snippet 13 | Diagram aligned to the snippet's members (`SetControlState`/`SendCollectionEvent`/…, `CompleteHcack`) |
| CON6-11 | MINOR | 02 §2.1 diagram vs §2.3/§2.4 drift (`_shutdown` type, `RunWhenReady`, buffer factoring) | Diagram: `CancellationToken _shutdown`; `ToolStateOrderingBuffer` folded into `BusAdapter` (matching §2.4 code + snippets 05/07); `RunWhenReady` added to §2.3's normative code |
| CON6-12 | MINOR | D-12 never landed: View 2 ":50055 localhost-only" annotation | Annotation applied to the AOI node |
| CON6-13 | MINOR | D-13/D-15/D-11 leftovers + snippet 09 stale §4.2 quote | 06 §6.9 channel relabeled *nominal* + T-L4 drain-cap stated (also snippet 16); tests 11/13 got "(= P0-measured)"; snippet 12 citations §1.1→§1.2; snippet 09 quote updated to the C-2 all-writers text |
| CON6-14 | MINOR | 07 attributed R-4 and SEC-3 findings to the S-list | Citations corrected to R-4 and SEC-3/R-7 |
| CON6-15 | MINOR | 07 §7.3 `CommandPublisher` had no `tool.commands` method (SYS-3 uses one) | `PublishToolCommandAsync` added |
| CON6-16 | MINOR | Stale 00-README TestKit note; snippet 14 missing `OnStart`/`OnStop`/`VerifySignature` + unmarked SEC-2 artifact; Mermaid render-degraded cosmetics | README points to §6.10's component design; snippet 14 gains the members with `TODO(R-OPS-3)`/`TODO(R-7/SEC-2)` markers; nested generic + stateDiagram `\n` escapes removed |
| CON6-17 | MINOR | "net8" shorthand vs the R-OPS-6 .NET 10 LTS decision | 04 §4.2 / 07 §7.1 / snippet 12 now say "net8-era, ships on .NET 10 LTS per §4.4" |

**Verification:** Mermaid lint re-run after the landing pass — **39/39 blocks parse clean** (was 38/39). Stale-phrase grep (`shares :5007`, `burst absorption`, `View 1/3 Flow`, `3-site`, `TestKit 12`, `sketch-bug list` misattributions) returns no hits in the stage set.

**Post-landing verdict — consistency dimension: READY** (subject to the same governance standing conditions as the round-2 verdict above; no design decision was changed by this pass — every fix was text-level alignment to already-decided resolutions).
