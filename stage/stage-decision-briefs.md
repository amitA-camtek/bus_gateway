# Stage Design Set — B3 Decision Briefs

> **STATUS: recommendations ACCEPTED and written into the design (design READY).** Every "Recommendation: A" below was taken; where a resolution had leaned on an external input the design was strengthened so it no longer does (gateway-side idempotency R-3, GEM transition ring R-6, decided mTLS R-7, code-verified R-8). This document is retained as the *decision record* — the rationale and the options considered. Live status per gap: [05 §5.6.1](05-roadmap-and-risks.md); verdict: [stage-review.md](stage-review.md).

> **Purpose:** turn the 5th-cycle review's design-decision gaps ([stage-review.md](stage-review.md) R-1..R-8 + OPS/TS) into ratifiable decisions. One brief per gap: problem → options with trade-offs → recommendation → owner. This is a **decision agenda**, not new prose for the normative docs — a chosen option becomes normative text in a follow-up.
> **How to use:** in the owner meeting, each brief resolves to *Accept-recommendation* / *Choose-alternative* / *Spike-first*. The five P1a-blocking briefs (R-1, R-2, R-3, R-7, R-8) gate the ADR-adoption decision in [doc 0](00-context-and-case.md).

**Legend:** ⛔ blocks the named phase · 🔬 needs a spike/measurement before the contract can be written · 📝 ratify-and-write (convergent) · 🏢 business/work-stream decision.

---

## R-1 — Durable-subscriber protocol 📝 ⛔ P1a

**Owner:** Fabric owner · **Blocks:** P1a (this is the "zero silent loss" guarantee)

**Problem.** The broker snapshots the subscriber set *at PUB* and treats "no live subscriber" identically to "durable subscriber is temporarily down." On a routine gateway restart, a `scan.committed` published during the 2–30 s outage is E2E-acked against an empty set → the publisher journal tombstones it → the message is gone, uncounted. This makes the headline guarantee false in the single most common outage.

**Options.**
| # | Approach | Trade-off |
|---|---|---|
| A *(rec)* | **Declared durable subscribers in the topic registry.** `scan.committed` names `ToolGateway` as a required durable subscriber; a per-tool "gateway-disabled" case is a signed config-profile override. E2E_ACK fires only when every *declared* durable subscriber has DELIVER_ACK'd; a declared-but-disconnected one behaves like NACK (message waits in the publisher journal). Disconnect ≠ deregistration. | Adds a registry concept + a broker rule; the gateway-disabled path must be a config override, not the default. Small, static, testable. |
| B | **Persist subscriber registrations in the broker.** Broker becomes stateful about who is a durable subscriber. | Contradicts the broker's "no persistence" design principle; adds a broker durability surface to own and recover. |
| C | **Accept the loss window, alarm it.** Rescope the claim to "zero *undetected* loss" and rely on the power-loss reconciliation (R-... / DI-5) to re-publish. | Cheapest, but concedes the headline guarantee; only defensible if paired with a real reconciliation mechanism, which doesn't exist yet. |

**Recommendation: A.** It keeps the broker stateless (the durable set is static config, not runtime state), closes the gateway-restart hole, and the "disabled vs down" distinction becomes explicit. Extend TestKit assertion 5 with "gateway down at PUB time" + "gateway crash with 128 queued."

---

## R-2 — Publisher `sourceEpoch` 📝 ⛔ P1a

**Owner:** Fabric owner · **Blocks:** P1a (also the prerequisite for R-3's dedup fix)

**Problem.** `BusClient._nextSeq` starts at 0 every process start. After an AOI restart (daily) or a journal reset, the next wafer publishes `seq=1`, which the gateway's seq-contiguity dedup (high-water 18,734) discards as a duplicate — silently dropping a *fresh* wafer. There is no incarnation field to distinguish a new run from a replay.

**Options.**
| # | Approach | Trade-off |
|---|---|---|
| A *(rec)* | **Add `sourceEpoch` to the envelope + HELLO** (persisted next to the journal, bumped on any journal re-creation). Dedup key = `(source, epoch, topic, seq)`; a new epoch resets the baseline and is alarmed. Restore class-A `_nextSeq` from journal high-water on start. | One envelope field + a small persisted counter. Already sketched as the anchor in `codeSnippets\01,03`. |
| B | **Persist and restore `_nextSeq` only** (no epoch). | Works for class-A (journal restores the seq) but B/C topics are never journaled → their seq is unrecoverable; a corrupted/deleted journal still collides. Half a fix. |

**Recommendation: A.** Trivial cost, and it's load-bearing for R-3 (the WAL dedup key). Bundle the `TryAdd`-overflow counter fix (S-1) in the same commit. Add "publisher restart mid-stream" to TestKit assertions 2/3.

---

## R-3 — Gateway WAL lifecycle + Fleet/TSMC idempotency 🔬 ⛔ P1a

**Owner:** Gateway owner (+ cross-team: Fleet.Main, TSMC SDK) · **Blocks:** P1a

**Problem.** The WAL has no defined per-sink completion state: `MarkDeliveredAsync` exists but nothing calls it, so the 60 s drain either re-sends delivered results forever or the WAL grows unbounded. A crash after sink-push but before DELIVER_ACK re-runs the route → **duplicate wafer results to Fleet/TSMC**. No downstream dedup key is specified. This trades silent loss for silent *duplication* of the same customer data.

**Why it's a spike, not just a decision.** The idempotency half depends on a **cross-team contract**: will Fleet.Main and the TSMC upload path honor a `messageId` (or wafer-lot key) dedup on ingestion? That must be confirmed with those teams before the gateway design is finalized.

**Options.**
| # | Approach | Trade-off |
|---|---|---|
| A *(rec)* | **WAL entry state machine** (`received → routed{per-sink pending/done} → deleted`) with a per-sink completion callback; atomic append (tmp+rename); **`messageId` idempotency key contractually honored by Fleet + TSMC.** | Requires the cross-team confirmation; per-sink state restores today's per-sink-spool structure (the single WAL was a regression). |
| B | **Keep per-sink spools** (as today), add the drain loop + poison split only. | Less change, but doesn't give a single durable ownership point; the DELIVER_ACK-coupling (R-4) still needs fixing separately. |

**Recommendation: A** — ACCEPTED, and **strengthened past the cross-team dependency**: the gateway now dedups on its own `(source, sourceEpoch, seq)` (persisted high-water recovered from the WAL), so duplicates don't leave the gateway. The Fleet/TSMC `messageId` dedup is therefore **optional defense-in-depth** for the residual crash-between-sink-ack-and-WAL-mark window, **not a P1a blocker** (§6.5 gateway-WAL, §5.6.1 A-6). ~~Until that confirmation lands, R-3 stays open~~ *(superseded)*. TestKit 5 must assert per-sink `count==published AND distinct-count==published` (a plain count hides a loss+duplicate cancellation).

---

## R-4 — Decouple DELIVER_ACK from sink routing 📝 (near-B2) ⛔ P1a

**Owner:** Gateway owner · **Blocks:** P1a

**Problem.** `OnScanCommitted` awaits WAL append **and** `RouteAsync` before acking. When Fleet/TSMC is unreachable, the sink channel fills → routing blocks → DELIVER_ACKs stop → broker queue fills → NACK → the backlog piles into *AOI's publisher journal*. An external cloud outage degrades the internal fabric — the opposite of what the WAL was built for.

**Options.**
| # | Approach | Trade-off |
|---|---|---|
| A *(rec)* | **"Ack is a function of the WAL append only, never of any sink or dispatcher state."** Routing consumes from the WAL asynchronously. Plus: name the terminal overload stage — WAL quota reached → stop DELIVER_ACKing → backpressure to the (alarmed, sized) publisher journal → never drop at the sink hop. | One-sentence contract + the WAL-quota policy decision. Mostly a sketch-vs-intended-prose correction; the *quota policy* is the real decision. |

**Recommendation: A.** This is largely settling that the prose (ack-on-WAL) wins over the sketch (ack-on-route). The genuine decision is the spool-at-quota behavior — choose backpressure-to-journal, not drop-at-sink, so loss is only ever taken at the one alarmed, sized store.

---

## R-5 — Retained class-B survival across restart / dead publisher 📝 ⛔ P3 (and GEM shim)

**Owner:** Fabric owner · **Blocks:** P3, GEM shim degraded-recovery

**Problem.** Retained `tool.state` / `production.carrier` slots live in broker memory; the broker has "no persistence." After a broker restart the slots are empty, and the reconnect algorithm replays only the class-A journal (B/C aren't journaled). So SYS-4's "retained class B re-delivers current state" and the GEM shim's "retained tool.state removes staleness on reconnect" have **no mechanism** — a reconnecting subscriber can wait hours (~10 transitions/day) for truth. Second half: with the publisher (ToolManager) down, the retained value is served with no liveness marker → the shim reports a dead process's state to the fab.

**Options.**
| # | Approach | Trade-off |
|---|---|---|
| A *(rec)* | **Class-B publishers re-publish current value on every (re)connect** (ToolManager owns the state; same `stateSeq`, dedup absorbs it) **+ broker attaches `sourceConnected`/`retainedAtUtc` when serving retained.** | ~3 lines in the ToolManager shim + a broker liveness flag. Keeps the broker stateless. GEM shim treats `sourceConnected==false` beyond T as a degraded input. |
| B | **Broker persists retained slots to a side file.** | Adds a broker persistence surface (contradicts the principle) and a recovery path to own. |

**Recommendation: A.** Cheaper, keeps the broker stateless, and closes the dead-publisher-staleness half that B doesn't. Add TestKit 6b: kill broker → restart → new subscriber gets current `tool.state` within T without a transition occurring.

---

## R-6 — GEM degraded contract + process supervision 🔬 ⛔ P4 (and shim startup)

**Owner:** GEM owner · **Blocks:** P4; needs P0 E30 timing measurements

**Problem.** The fab-facing promise is "HCACK denial, never a timeout." As sketched the shim breaks it three ways: `SecsGemGui.Net` has no ToolHost supervisor/start-order; `_remoteGranted` is unreachable on a clean start (no false→true transition when the broker is already up); and an in-window command blocks the SECS reader ~27 s (the exact timeout it forbids). `tool.state` as class B can also drop an `Engineering→EngineeringToProduction→Engineering` failure cycle's E30 CEIDs.

**Why it's a spike.** The Ttl margin (`ttl + margin < E30`) depends on **P0-measured** GEM pre-/post-hop latencies per site; and whether the host needs every intermediate transition is a per-site E30 question the host team must answer.

**Options.**
| # | Approach | Trade-off |
|---|---|---|
| A *(rec)* | **Explicit 4-state HSMS×bus machine** (start degraded; REMOTE entry only via a completed bus *handshake*, not a health-flag read; event-driven composite health; `RequestAsync` fails fast when disconnected → HCACK); **add `SecsGemGui.Net` to the ToolHost manifest** (startOrder > broker); **bounded last-N `tool.state` transition ring** so the shim always replays intermediate transitions. | The sketch already models this direction; the full state machine is the real work. The ring removes the per-site host question (as accepted). Needs the P0 numbers. |
| B | **Keep two-boolean model, just fix the timer + fail-fast.** | Cheaper, but the 4-state matrix is exactly where the fab-compliance edge cases live; a partial fix leaves the degraded contract under-specified for audit. |

**Recommendation: A** — ACCEPTED, and **strengthened past the per-site host question**: a bounded last-N transition ring (N≈16, §1.3.4) makes the shim replay intermediate transitions itself, so it **always** delivers the E30 CEIDs — no per-site host sign-off on intermediate-transition tolerance is required. The E30 Ttl-margin is a **routine P0 measurement** (same class as the group-commit interval), asserted fail-fast at config load. ~~a host-team answer on intermediate-transition tolerance~~ *(superseded — the ring removes the question)*. The full state machine is P4-gated.

---

## R-7 — Security work-stream (NEW) 🏢 ⛔ P1a

**Owner:** Ofek Harel (security) · **Blocks:** P1a — and this is a *work-stream*, not a single decision

**Problem.** Never reviewed in the prior four cycles. As drafted the fabric *increases* the command attack surface at P1a: the `*.commands` publish ACL keys on a self-asserted `HELLO.sourceName` and is fail-open (`SenderToAcl => Acl.Any`); the ToolHost child manifest is unsigned (`ComputeHash="TODO"`) → manifest tamper = SYSTEM code-exec; `:5007` ships with `IsAuthorized => true`. Grounded facts: today there are no pipe ACLs / no TLS anywhere, `:5005` binds `0.0.0.0`, Fleet `:5050` is cleartext with no credentials.

**Design forks — all now DECIDED (written into §6.8); what stays is implementation, not choice:**
1. **Account architecture — DECIDED:** distinct service accounts (`svc-GemShim`/`svc-ToolManager`/`svc-Gateway` + AOI user) so the OS-authenticated pipe account is the ACL key. *(The linchpin — SEC-1/7/9 resolve from it.)*
2. **Manifest signing — DECIDED:** signed manifest + Authenticode exes, verify-before-launch fail-closed, config-dir ACL.
3. **`:5007` authz — DECIDED: mTLS** (Windows-auth fallback), default-deny, minimum-interface bind, rate-limit/lockout, audit-before-publish. Closes §5.6 item 7.
4. **Data-at-rest — DECIDED:** spool/journal/dead-letter directory ACLs to the service accounts.
5. **Audit sink — DECIDED:** append-only, off-bus, service-account-owned, before-publish.

**Recommendation — ACCEPTED.** The design forks are decided (§6.8); R-7 is a **specified, buildable work-stream**, not an open design question. What remains is **execution, not choice**: name the owner (Ofek Harel — folds into the existing §5.1 rule-4 pre-P0 "named owner" criterion) and implement — certificate/key management + rotation, threat-model sign-off, pen-test. "`:5007` refuses unauthenticated callers" and "distinct service accounts bound to topic ACLs" are **P1a exit criteria**.

---

## R-8 — ToolManager transition-serialization lock 🔬 ⛔ P3

**Owner:** Fabric owner · **Blocks:** P3 · **Needs a code spike**

**Problem (verified against code).** The `stateSeq` guarantee is described as "stamped inside the existing transition-commit lock." Verified: **no such lock exists** — `ToolManager.cs` assigns `_toolState` unlocked (`:782`), `ChangeToolStateInternal` (`:822-877`) has no lock/`Interlocked`, and the state machine has *multiple concurrent writers* (`frmProduction.CheckState:648`, `BufferStation ToolManagementAdapter:87,105`, `ProductionGui frmProductionGuiBL:305`, ProductionManager internal `:229,247`) plus reentrant transitions. P3 is not a "3-line diff."

**Why it's a spike.** Introducing serialization into a live state machine, and running the *synchronous* COM CB fan-out under a new lock, is a deadlock risk that must be investigated in the real code — not decided on paper.

**Spike deliverables (before any P3 estimate stands):**
1. Enumerate *every* writer of tool state (the census above is a starting point, likely incomplete).
2. Choose the mechanism: a single lock vs a single-dispatcher (queue) that serializes transitions off the callers' threads.
3. **Deadlock audit** of the CB fan-out under the chosen mechanism (the fan-out today can re-enter via the STA; a lock held across it can deadlock — see concurrency CC-8).
4. Define the acceptance test (concurrent writers + reentrancy → no lost/reordered `stateSeq`, no deadlock).

**Recommendation.** Fund the spike as a **Wave-0 item**; do not carry P3 as "small/high-semantic" until the spike closes. The reword ("lock to be introduced") is already applied to the docs; the *engineering* is the open item.

---

## Operations & Test decisions (briefer — mostly deliverables + one business call)

| ID | Decision | Type | Owner | Recommendation |
|---|---|---|---|---|
| **R-OPS-1** | Rollback class after P1b retirement; keep `:5005` one release past the last AOI publisher; LB2 fixed in Wave 0 | 📝 | DevOps | Accept — add the "rollback class after retirement" column to §4.3; LB2 is the rollback path, fix it in Wave 0 |
| **R-OPS-2** | Invalid config → boot last-known-good (never no-start); operator runbook + local alarm surface | 📝 | DevOps | Accept — a $2M tool must never be un-startable over a JSON typo; Wave-0 deliverable |
| **R-OPS-3** | ToolHost graceful-stop contract + in-place upgrade runbook + old-spool migrator | 📝 | DevOps | Accept — sketch already added (`StopAllAsync`); the migrator is a P1a deliverable |
| **R-OPS-4** | Fleet dashboard + shadow-comparator: named owner, dark-tool evidence path, N as event-counts | 📝 | DevOps + Fabric | Accept — name the dashboard as an owned deliverable in §4.1 |
| **R-OPS-5** | "Refuse Production entry at P4+" — customer/PM sign-off + supervised override | 🏢 | Program (Guy Kafri) | **Business decision** — a bus fault becoming a fab-down event needs customer consent; design the ONLINE-LOCAL supervised override |
| **R-OPS-6** | Target .NET 10 LTS (not .NET 8, EOL 2026-11); self-contained runtime servicing | 📝 | Fabric | Accept — decide at P0; avoids repeating the net7-EOL mistake |
| **R-TS-1** | Run the 14 TestKit assertions on **net48 × both bitnesses**, not net8-only | 📝 | Testing (Carmel) | Accept — the net48 build is the one AOI loads; multi-target the Tests project |
| **R-TS-2** | Build + **qualify** GEM record-replay (unscheduled) and shadow comparator (a stub); CI tier table | 🔬 | Testing | Accept — two of five gate instruments don't exist; without them the per-edge gate is unenforceable. Wave-0 |
| **R-TS-3** | Shadow-comparator gate on event-counts (not calendar days) + pre-registered taxonomy | 📝 | Testing | Accept — at ~10 tool.state/day a 14-day gate has near-zero power at P3 |

---

## Suggested meeting sequence

1. **R-7 decision #1 (service accounts)** — unblocks the whole security work-stream; everything else in R-7 follows.
2. **R-1, R-2, R-4, R-5** — the ratify-and-write group; ~30 min total, then I write the §6 text.
3. **R-3** — assign the Fleet/TSMC idempotency confirmation (cross-team action item).
4. **R-8, R-6, R-TS-2** — fund the spikes (ToolManager lock, GEM 4-state, test instruments) as Wave-0.
5. **R-OPS-5** — the one business decision; route to Program + customer.

**Only after the five ⛔-P1a briefs (R-1, R-2, R-3, R-7, R-8) close does the [doc 0](00-context-and-case.md) ADR-adoption recommendation become actionable.**
