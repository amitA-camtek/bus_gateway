# Stage Design — Adversarial Review Cycle 9

**Date:** 2026-07-19  
**Scope:** `stage/*.md` + `stage/codeSnippets/*.cs`  
**Trigger:** Targeted consistency + concurrency pass following cycle-8 verification  
**Dimensions run:** consistency, concurrency, connectivity, data-integrity, test-strategy, feasibility (targeted)  
**Verdict:** ✅ **DESIGN READY** (after resolutions below)

---

## Round 1 — Findings

### CRITICAL

#### C9-1 — Typed R-R API: `Serve<T>` returns untyped `Task<Reply>` — `tool.state.replay` cannot use it
- **Reviewer:** consistency / concurrency
- **Location:** `stage/06-bus-implementation.md §6.2`, `stage/codeSnippets/02-bus-client-api.cs`
- **Scenario:** `TransitionRingServer` registers a handler for `tool.state.replay` that must return `StateTransition[]`. The existing `Serve<T>` overload returns `Task<Reply>` with no typed payload. There is no mechanism to attach a payload to the reply; the GEM shim's `RequestAsync` call receives an opaque `Reply` with no `StateTransition[]` data. The feature is silently broken.
- **Resolution (FIXED):** Added `Task<TRes> RequestAsync<TReq,TRes>(Topic, TReq, TimeSpan, CancellationToken)` and `ISubscription Serve<TReq,TRes>(Topic, Func<BusMessage<TReq>,Task<TRes>>)` typed overloads to `IBus` in §6.2 and snippet 02. Existing untyped overloads retained for callers that only need the `Reply` status. `TransitionRingServer` now uses `Serve<long, StateTransition[]>`.

---

### MAJOR

#### C9-2 — G-8 crash-point description is self-contradictory
- **Reviewer:** consistency / data-integrity
- **Location:** `stage/07-toolconnect-design.md §7.12`, scenario G-8
- **Scenario:** G-8 described the fifth crash point as "after-DELIVER_ACK/before-WAL-flush." §7.4 rule R-4 states flush PRECEDES ACK — so this window cannot exist. The oracle also said "re-deliver unconsumed entries" which is wrong (the WAL entry already has its ACK, not a re-deliver). A test author following G-8 would test an impossible crash window and miss the real one (after-ACK, before routing legs are created).
- **Resolution (FIXED):** Rewritten to: "fifth crash point — after-ACK/before-routing (`Received` state: entry durable, zero legs)." Oracle corrected to: "startup routing pass creates legs for all `Received` entries; normal delivery then proceeds."

#### C9-3 — GEM shim bus-up probe uses `RequestAsync(Topics.ToolState, …)` not `PingAsync`
- **Reviewer:** concurrency / connectivity
- **Location:** `stage/codeSnippets/13-gem-shim.cs`, `HandshakeThenEnableAsync`
- **Scenario:** Using `RequestAsync(Topics.ToolState, …)` as the bus-up probe requires a live `tool.state` publisher, not just a live broker. The shim could be stuck in degraded even when the broker is healthy, if `svc-ToolManager` starts later. Also, the topic ACL allows only `ToolManager` to publish — the shim cannot legally send a request as a subscriber. Wrong probe.
- **Resolution (FIXED):** Changed to `_bus.PingAsync(TimeSpan.FromSeconds(2))` (round-trip to broker only). `PingAsync(TimeSpan)` added to `IBus` interface in §6.2 and snippet 02.

#### C9-4 — CEIDs emitted inside the shim lock — parks the SECS reader
- **Reviewer:** concurrency
- **Location:** `stage/01-system-architecture.md §1.3.4`, `stage/codeSnippets/13-gem-shim.cs`
- **Scenario:** `ApplyDegradedContract` held `_shimLock` and then called `_secsCallbacks.SendCollectionEvent(BusDegraded)` under the lock. SECS callbacks are cross-process COM; if the COM server is slow or blocked, the reader thread parks inside the lock, stalling all incoming S-messages.
- **Resolution (FIXED):** §1.3.4 prose updated to dispatch-queue pattern: ShimState decision made under `_shimLock` on the reader thread; CEID side-effects queued to a single-threaded dispatcher that executes off the reader thread. Snippet 13 updated accordingly — SECS callbacks invoked OUTSIDE the lock.

#### C9-5 — ToolHost child launch failure is unhandled
- **Reviewer:** concurrency / operations
- **Location:** `stage/01-system-architecture.md §1.3.3`, `stage/codeSnippets/14-toolhost.cs`
- **Scenario:** `StartChildAsync` calls `proc.Start()` with no try/catch. If the exe path is wrong, the executable is missing, or the OS refuses the launch, `Start()` throws and the exception propagates uncaught through `StartAllAsync` or `HandleChildExitAsync`, terminating the entire supervision loop — silently killing supervision of all remaining children even for `quarantine: never` children.
- **Resolution (FIXED):**  
  - §1.3.3 prose: added normative rule "Spawn failure is a restart-class failure — caught, counted in same sliding window, backed off, alarmed; supervision loop terminates only on stop token. Initial-spawn semantics: quarantine-never children → fail-fast; leaf children → degrade-and-alarm."  
  - Snippet 14 `StartChildAsync`: try/catch around `proc.Start()` throws `ChildSpawnException`. `ChildSpawnException` class added.  
  - `HandleChildExitAsync`: restart calls wrapped in try/catch — spawn failure counted in same window, backed off, alarmed.  
  - `StartAllAsync`: initial spawn wrapped in try/catch — quarantine-never → re-throw (fail-fast); leaf → degrade-and-alarm, skip supervisor loop.

#### C9-6 — `SourceEpoch` missing from `ToolStatePayload` and `ToolStateEvent` type definitions
- **Reviewer:** consistency / data-integrity
- **Location:** `stage/codeSnippets/01-bus-contracts.cs`, `stage/codeSnippets/05-bus-adapter.cs`
- **Scenario:** All prose and logic in §6.2, §6.6, §2.4 references `payload.SourceEpoch` and `e.SourceEpoch`. Neither the `ToolStatePayload` DTO (snippet 01) nor the `ToolStateEvent` class (snippet 05) declared the field. The property access would be a compile error. Every consumer depending on epoch-based dedup (C8-CRIT-8 fix) would silently regress.
- **Resolution (FIXED):** `public long SourceEpoch { get; set; }` added to both `ToolStatePayload` (snippet 01) and `ToolStateEvent` (snippet 05) with full commentary.

#### C9-7 — `TryPostAndWait` still using `using` on `ManualResetEventSlim`
- **Reviewer:** concurrency
- **Location:** `stage/codeSnippets/06-ui-marshaller.cs`
- **Scenario:** The `using` block on `ManualResetEventSlim` disposes the event when the `catch (OperationCanceledException)` path exits, but the posted UI-thread delegate may not have run yet. When the UI thread later executes and calls `done.Set()`, `ObjectDisposedException` is thrown on the Windows message pump — a crash that bypasses all exception handling in production.
- **Status:** Review record C8-MAJ-5 marked this complete, but snippet 06 was not updated.
- **Resolution (FIXED):** Removed `using`; applied exactly-one-side-disposes pattern: caller disposes on the normal waited path; on the abandoned (OperationCanceledException) path, the event is intentionally NOT disposed — the GC collects it safely after the queued delegate runs `Set()`.

#### C9-8 — Dead-letter CLI: doc 05 and doc 07 specify two contradictory designs
- **Reviewer:** consistency
- **Location:** `stage/05-roadmap-and-risks.md:35` vs `stage/07-toolconnect-design.md §7.10`
- **Scenario:** Doc 05 said `ToolConnect.exe --deadletter list|inspect|reinject <entryId>` with semantics "re-inject = a new WAL entry, same messageId." Doc 07 §7.10 says `toolconnect-admin dead-letter list/re-inject/delete` with semantics "re-inject = transition Poisoned leg to Pending (not a new WAL entry)." Two different executables, two different verbs, two contradictory re-inject semantics. A P1a implementer following doc 05 would build the wrong design.
- **Resolution (FIXED):** Doc 05 line 35 rewritten to: "the dead-letter CLI is a P1a deliverable — [07 §7.10](07-toolconnect-design.md) is the normative spec (CLI verb: `toolconnect-admin dead-letter list/re-inject/delete`; re-inject transitions the Poisoned leg to Pending — NOT a new WAL entry)."

#### C9-9 — Doc 05 still says `:5100 health push`
- **Reviewer:** consistency
- **Location:** `stage/05-roadmap-and-risks.md:34`
- **Scenario:** C8-CRIT-1 established that Fleet polls `:5100` (off-box); `:5100` must bind to the management LAN interface, not loopback. Doc 05 line 34 still said "health push (survives broker death)" — implying ToolHost pushes to Fleet. A P1a implementer following this would build a push mechanism that never works with Fleet's pull-based health model.
- **Resolution (FIXED):** Rewritten to "reach Fleet via the Fleet-polled `:5100` health surface (Fleet polls off-box on the management LAN — C8-CRIT-1/D16)."

---

### MINOR (all resolved)

| ID | Location | Issue | Resolution |
|----|----------|--------|------------|
| M9-1 | §6.8 prose | `:5100` and `:5060` described together as "loopback-bound (or a defined mgmt interface)" — `:5100` must be management LAN | Separated: `:5100` is management LAN (Fleet polls; D16); `:5060` is loopback |
| M9-2 | `stage/06-bus-implementation.md §6.5 assertions` (2 occurrences) | "gateway-crash-with-128-queued" is ambiguous now that class-A queue is 256 | Changed to "gateway-crash-with-queued-entries" |
| M9-3 | `codeSnippets/14-toolhost.cs` manifest | `ToolConnect.exe` → normative name is `ToolConnect.Service.exe` (§7.10) | Fixed |
| M9-4 | `codeSnippets/14-toolhost.cs OnStop` | `StopAllAsync()` called with no args but signature requires `(TimeSpan, CancellationToken)` | Fixed: `StopAllAsync(TimeSpan.FromSeconds(30), CancellationToken.None)` |
| M9-5 | `codeSnippets/13-gem-shim.cs` | No `SetRemoteGranted(bool)` entry point — `_remoteGranted` can never be set to `true` | Added `public void SetRemoteGranted(bool granted)` under `_shimLock`; REMOTE only granted when bus is proven up |
| M9-6 | `codeSnippets/13-gem-shim.cs RecoverFromDegraded` | `BusRecovered` CEID never emitted on recovery | Added `_secsCallbacks.SendCollectionEvent(GemCollectionEvent.BusRecovered)` outside lock |

---

## Round 2 — Verification (Cycle-10 independent pass)

After applying all 9 resolutions above, an independent verification pass confirmed that all C9-1..C9-9 and M9-1..M9-6 fixes landed. The pass also found 5 new issues introduced by the cycle-9 edits:

### New findings from Round-2 verification

#### V10-1 (MAJOR — fixed): §1.3.4 C9-4 prose contradicts class diagram note
- **Prose** said "single-threaded dispatch queue … no lock held across SECS calls."
- **Class diagram note** said "one lock."
- **Snippet 13** implemented the lock approach (callbacks outside lock, on calling thread).
- **Resolution:** Prose updated to accurately describe the lock-with-callbacks-outside pattern; class diagram note updated to "All ShimState transitions serialized under `_shimLock`; SECS callbacks always invoked OUTSIDE the lock." The implementation invariant is preserved: reader thread is never parked inside a lock during a COM call.

#### V10-2 (MAJOR — fixed): `RecoverFromDegraded()` dead code — `BusRecovered` CEID never emitted
- `HandshakeThenEnableAsync()` called `SetControlState()` directly, bypassing `RecoverFromDegraded()`, so `BusRecovered` CEID was never emitted on bus recovery.
- **Resolution:** `HandshakeThenEnableAsync` now calls `RecoverFromDegraded()` instead of the direct `SetControlState` call. Fixed `// GS-5` annotation (wrong test) to `// (C9-4/M9-6)`.

#### V10-3 (MINOR — fixed): Double-counting spawn failure in `HandleChildExitAsync` QuarantineNever catch
- Two `RecordAndCountLastHour()` calls recorded the same failure twice, inflating alarm severity at 2× the intended rate.
- **Resolution:** Single `var spawnCount = window.RecordAndCountLastHour()` call; `Alarm(child, spawnCount)`.

#### V10-4 (MINOR — fixed): `ChildProcess(config, null).IsRunning` would throw `NullReferenceException`
- `IsRunning` called `Process.HasExited` without a null guard; placeholder `ChildProcess` objects created with `null` process.
- **Resolution:** `IsRunning` now `Process != null && !Process.HasExited`.

#### V10-5 (MINOR — fixed): Doc 01 §1.3.1 class-A queue mermaid still showed "bounded 128" (stale — should be 256)
- **Resolution:** Updated `QA` node to "Class-A queues (bounded 256)."

After applying all 5 round-2 fixes, an adversarial check confirmed no new contradictions were introduced.

---

## Final Verdict — Post Round-2 Fixes

- **C9-1:** Typed `RequestAsync<TReq,TRes>` / `Serve<TReq,TRes>` / `PingAsync` present in both §6.2 and snippet 02. `TransitionRingServer` uses typed form. ✅
- **C9-2:** G-8 crash point correctly reads "after-ACK/before-routing (Received state, no legs)"; oracle is "startup routing pass creates legs." ✅
- **C9-3:** `HandshakeThenEnableAsync` uses `_bus.PingAsync(TimeSpan.FromSeconds(2))`. ✅
- **C9-4:** §1.3.4 lock-with-callbacks-outside pattern documented; class diagram note updated; snippet 13 SECS callbacks invoked outside lock. ✅ (V10-1 fixed)
- **C9-5:** `StartChildAsync` try/catch → `ChildSpawnException`; `HandleChildExitAsync` and `StartAllAsync` handle spawn failures per fail-fast/degrade policy; §1.3.3 normative rule added. ✅ (V10-3/V10-4 fixed)
- **C9-6:** `SourceEpoch` field present in both `ToolStatePayload` (snippet 01) and `ToolStateEvent` (snippet 05). ✅
- **C9-7:** `TryPostAndWait` in snippet 06 uses exactly-one-side-disposes, no `using`. ✅
- **C9-8:** Doc 05 dead-letter entry points to §7.10 as normative; contradictory re-inject semantics removed. ✅
- **C9-9:** Doc 05 alarm-routing entry says "Fleet-polled `:5100` health surface." ✅
- **All minors:** Confirmed landed. V10-2 (`BusRecovered` dead code) and V10-5 (queue 128→256 in doc 01) also fixed. ✅

---

## Final Verdict

**DESIGN READY** — Nine adversarial review cycles + one targeted verification pass completed. All CRITICAL and MAJOR findings resolved. Seven outstanding human-decision items remain in [OPEN-DECISIONS.md](OPEN-DECISIONS.md) (D15–D21); none block implementation start.

| Cycle | Findings | CRITICALs | MAJORs | Status |
|-------|----------|-----------|--------|--------|
| 1–5 | [stage-review.md](stage-review.md) | 0 remaining | 0 remaining | READY |
| 6–7 | [stage-review-cycle7.md](stage-review-cycle7.md) | 0 remaining | 0 remaining | READY |
| 8 | [stage-review-cycle8.md](stage-review-cycle8.md) | 8 found / 8 fixed | 28 found / 28 fixed | READY |
| 9 | this document | 1 found / 1 fixed | 8 found / 8 fixed | READY |

**Standing conditions (human sign-off required):**

| ID | Decision |
|----|----------|
| D15 | Session-0 interactive-UI split required before Wave 1 GEM entry |
| D16 | `:5100` NIC binding — management LAN interface identity |
| D17 | AOI kill-path disable during ordered ToolHost stop |
| D18 | ToolHost service account identity |
| D19 | ToolHost installer state machine |
| D20 | WAL state-machine rollback compat between WAL versions |
| D21 | ToolHost blast-radius policy (quarantine-never child failure scope) |
