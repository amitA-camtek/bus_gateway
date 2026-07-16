# A3 Fused Design — Consolidated Adversarial Review

> Three independent reviews of [a3-fused-bus-gateway-design.md](a3-fused-bus-gateway-design.md): technical feasibility (verified against `C:\CamtekGit`), design consistency/gaps, and production-operations. Findings deduplicated and ranked.
> Date: 2026-07-16.

---

## Verdict

**The fused thesis survives — the plan's edges do not.** All three reviewers independently confirmed the core architecture (one bus fabric, gateway as bus citizen, ToolLink deleted, existing gateway machinery reused): the deleted-risk claims (A1-R3/R4) are genuine, the gateway BL layer is verifiably transport-agnostic and ready for a BusSource, and ToolManager's fan-out is a mechanical 3-site dual-publish.

**But the design is NOT ready for stakeholder review.** Ops verdict: *conditional approval for P0/P1 only* — no phase past P1 until the durability contract is written and torture-tested by delivery *count*. Consistency verdict: the roadmap as written bakes in a customer-visible Fleet telemetry regression. Feasibility verdict: two of the design's central components are mischaracterized (one doesn't exist in the shipped build; one is missing from every diagram).

---

## Part 1 — Facts about TODAY's system the review corrected

These affect **all** the companion docs, not just the fused design.

| # | Correction | Evidence |
|---|---|---|
| T1 | **The live SECS/GEM stack is not the C++ `SecsGemClient`.** `SecsGemClient.vcxproj` (and `E30RemoteControl.cpp:46`, cited across all our docs) is **not in `Falcon_2022.sln`** — only in Robostar.sln; `SecsGemEquipmentSend.cs` comments mark the C++ logic "Not used". The live stack: **`SecsGemObjects` (C#, net48 — E30/E87/E116/E40/E94 logic) + `SecsGemGui.Net` (net48 tool-client process) over native Cimetrix `SECSGemDriver`** (the true untouched-wire boundary). Host→ToolManager commands go through `SecsGemObjects\Clients\RemoteControllers\RemoteControl.cs` | Feasibility F2 |
| T2 | **`FalconWrapper.exe` is missing from every diagram.** The COM event hub (`CFalconEvents`/`CScanManager`/`CAutoCycleManager`) is an **out-of-proc ATL EXE**, not "in-process COM inside AOI_Main". Its cross-process subscribers: SecsGemGui.Net, NetTAC, TAC.Net, TopiClient.Net, ProductionGui.NET — each one a migration edge the roadmap doesn't budget | Feasibility F5 |
| T3 | **Today's "<5 ms fire-and-forget" is folklore.** Every `ToolApiPublisher` publish calls `EnsureChannel()`→`StartProcessIfNotRunning()` (process scan per publish; `Thread.Sleep(1000)` + spawn **on the calling scan thread** if gateway down; **no gRPC deadline** — a hung gateway blocks indefinitely) | Ops F3, `ToolApiPublisher.cs:103-137, 204-211` |
| T4 | **Today's gateway-down loss is permanent.** Failed publishes append to `c:\Fleet\ToolAPI\FailedMessages\...Publisher.txt` — **no code ever reads it back**. Manual-replay-only dead-letter | Ops F1, `ToolApiPublisher.cs:159-173` |
| T5 | **Today's HCACK = acceptance, not completion.** `frmProduction.GuiStartManualScan` is `async void`; COM caller returns at first `await`; exceptions swallowed; `FireManualScanDone` commented out; UI-thread block = unbounded caller block | Ops F2, `frmProduction.cs:103-126` |
| T6 | **Not all `Fire*` are one-way.** `FireWaferScanResultsAreReady(ref wafer, ref dataCollectionSync)` returns data via ref params **on the hot scan path** (with a latent race at `frmProduction.cs:735`); `FireOperationCompleted(ref res)`, `FireGetOnDemandJobName` (full query). The scan-results edge itself is request/reply | Feasibility F5 |
| T7 | **`ExternalControlCbUiWrapper` is not a 2-command forwarder.** It implements the full `IFalconExternalControlCB` (~15 callbacks) incl. **synchronous state getters** (`GuiCurrentLotId`/`GuiCurrentWaferId`), executes `GuiSet2DOptics` itself, and fires events back through its own second `CFalconEvents` RCW | Feasibility F4 |
| T8 | **Library delivery = binary drops** (`c:\bis\bin` + `c:\bis\bin\x64`, both bitnesses), not NuGet; existing gRPC precedent is legacy `Grpc.Core` (EOL); prior art exists: `CamtekSystem\PubSub\MSMQ` + `IPublisher`/`PublisherFactory` | Feasibility F1 |
| T9 | Existing gateway spool bugs: restore loop has **no retry count/age limit** (deterministic-fail message cycles forever); `*.overflow.txt` (>10k backlog) is **never read** — silently parked | Ops F4, `FailedMessagesHandler.cs:98-153` |
| T10 | Stale refs: frmScanTab publish hooks are at **~:1888-1902 and :10162** (not :7301/:10155); `ToolApiPublisher` self-restart spawns stale name `Fleet.ToolAPI.Endpoint.exe` | Feasibility F5, Ops F1 |

## Part 2 — Critical design flaws (must fix before circulating)

| # | Flaw | Fix |
|---|---|---|
| C1 | **P1 bakes in a Fleet telemetry regression.** Retiring gRPC :5005 at P1 orphans Error/Warning/Lcc/ToolInfo (no topic exists for them; `tool.state` arrives only at P3). BusSource is specced to subscribe a topic that won't exist for two phases | Add a telemetry topic at P1 **or** make P1 a partial swap (`scan.committed` only, `ToolApiPublisher` retained for the rest until P3) |
| C2 | **P1 has no rollback.** It retires the old path in the same phase that introduces the new one — contradicting the design's own shadow-mode gate ("zero divergence over N days" needs an old path to compare against) | Split P1a (dual-run + shadow, N days) / P1b (retire); state the flag-back rollback lever |
| C3 | **The flagship edge (`scan.operations`) has no defined consumer** — §5 says the engine is untouched *and* `IFalconFireEvents` is retired; both can't be true. Reality (T2): consumers are out-of-proc in FalconWrapper.exe + 5 tool-client processes | Add FalconWrapper.exe to the architecture; enumerate its subscribers as edges; decide per-subscriber: migrate vs. permanent dual-publish |
| C4 | **The durability claim is asserted, not derived.** Ack semantics unspecified; broker-resident messages die on every *routine* broker restart with publisher believing delivery succeeded; memory-volatile loss replaces today's disk-durable dead-letter (T4) — *worse* for 3am recoverability. "No worse than today" is false as written — and "today" was never characterized | Write the ack contract; disk-backed publisher journal for `scan.committed` (reuse the spool pattern); bounded per-subscriber broker queues with per-topic overflow policy (coalesce `tool.state`, never-drop `scan.committed`); P0 torture test passes on **end-to-end delivery count**, not restart latency |
| C5 | **Request/reply semantics unspecified where they matter.** Accepted-vs-completed undefined (a manual scan takes minutes vs. 1-10s fab HCACK timeouts); **late execution** hazard: host told "failed", command executes anyway when the UI thread frees — escalation-grade in a fab. Reality check (T5): reply must = *accepted*, matching today | Define reply=accepted; deadline (TTL) in the envelope, **expired commands discarded** at the BusAdapter; timeout derived from per-site E30 config, not hard-coded |
| C6 | **No hard publish latency bound** on the scan path — ironic, since today's is unbounded (T3) and this is the design's chance to fix it | Spec: publish = local queue append ≤1 ms, never connect/send/sleep on caller thread; background pump does I/O; contract-test assertion in the kit |

## Part 3 — Major gaps (fix in the same revision)

| # | Gap | Fix |
|---|---|---|
| M1 | "Race cannot be wired by accident" is overclaimed — cross-topic ordering isn't guaranteed and nothing stops a consumer reading paths from `scan.announced` | **Payload contract**: `scan.announced` carries identifiers/timing only, **no file paths**; add payload-schema subsection |
| M2 | No bus security model — any local process can publish `tool.commands`; REST :5006 becomes an unauthenticated command bypass | Named-pipe/localhost ACL to service accounts; per-topic publish ACLs; :5006 restricted to non-command topics/dev |
| M3 | No schema versioning — while ToolHost explicitly guarantees mixed-version children | `SchemaVersion` in envelope; additive-only + ignore-unknown-fields as contract-kit rules; pick serialization (JSON now was A2's answer) |
| M4 | Poison messages: no dead-letter policy; one bad message can quarantine a subscriber process via ToolHost restart loop (plus today's spool loop, T9) | **F-R4**: delivery-attempt count in envelope; after N → per-topic dead-letter file + alarm; library-level handler catch boundary; fix the existing overflow black hole |
| M5 | Shadow comparator is unqualified production software running on 100+ tools for weeks — unbounded correlation memory, divergence-storm log volume, and async-vs-sync ordering will *legitimately* diverge (gate as written never passes) | **F-R5**: fail-open, out-of-process, bounded memory, sampled logging; gate metric = "unexplained divergence" with written equivalence rules |
| M6 | ToolHost dependency inconsistent: §2.2 label implies ToolHost supervises AOI/COM servers (ToolHost doc says never); P0 requires ToolHost delivered but it's an unapproved proposal; P1 silently answers open question Q4 | Fix label; add "ToolHost Phase 1 shipped" as P0 entry criterion; close Q4 explicitly |
| M7 | §5 "Retired (end state)" is unreachable for most of the fleet (P5 is optional/per-customer) → permanent dual transport is the real steady state; gateway-disabled customers (`ToolGatewayEnabled=0`) unaddressed | Phase-tag the retired table; add "steady state without P5" row; per-customer profile question in §9 |
| M8 | SimMode/VVRMode short-circuits (all 24 `Fire*` today) have no stated home; they'll also flood the shadow comparator with false divergence | Decide: gating lives in the BusAdapter (central); comparator is mode-aware |
| M9 | No observability story: an event now crosses 4 buffers (3 volatile) vs. today's 2 (both leaving disk traces) | Diagnostics spec: per-topic counters (published/acked/delivered/spooled/dead-lettered + `Seq` high-water marks) via ToolHost :5100; wildcard bus-tap recorder; dead-letter topic |
| M10 | §2.3/§3 disagree in 5 places (missing subscribers/publishers, AutoLoader shim absent, modWaferAlignment/frmVerifyTab/CmmReceiver absent from inventory); "A3" name collision with the dropped alternative; MES command port unspecified; firewall/:5005-closure in no phase; net8 task owned by two docs; A1-R2/A1-R5 disposition missing from §8 | Mechanical reconciliation pass |

## Part 4 — What this does to the effort estimate

- **Cheaper than designed:** the GEM shim is plain C# in `SecsGemObjects`/`SecsGemGui.Net` (T1) — no C++/CLI work, no C interop. The C4 durability fix reuses the existing (good) spool pattern.
- **More expensive than designed:** P2 must handle request/reply `Fire*` edges (T6) and the FalconWrapper subscriber population (T2); P4 must migrate ~15 `IFalconExternalControlCB` callbacks incl. synchronous state getters (T7), not 2 commands. §9-Q2's expectation ("little else needs request/reply") is already falsified by the code.
- **Net:** P1 shrinks (good — C1/C2 force it smaller anyway); P2/P4 grow; P5's re-qual anchor moves from "C++ engine" to the Cimetrix driver boundary.

## Part 5 — Recommended next actions

1. Apply the Part 2 fixes + Part 3 M1/M4/M5 to the fused design doc; mechanical pass for M10.
2. Correct the SECS/GEM stack description (T1) and add FalconWrapper.exe (T2) **in all four companion docs** — the baseline diagrams repeat the error.
3. Re-run the P0 spike list: delete nothing (the net48-client verdict HOLDS via T8's binary-drop caveat), add "characterize today's real loss/latency behavior" (T3/T4) as a P0 measurement — the program's before/after claims depend on it.
4. File the two *existing-code* bugs found (T9 spool loop + overflow black hole; T10 stale exe name in `ToolApiPublisher` self-restart, also SystemStopper) as ADO work items independent of any redesign — they're live today.
