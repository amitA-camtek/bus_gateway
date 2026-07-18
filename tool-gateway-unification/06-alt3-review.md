# 6 — Adversarial Review of Alternative 3 (Unified Tool Gateway Service)

> Four-reviewer adversarial review of [05-alt3-complete-design.md](05-alt3-complete-design.md), grounded in `C:\CamtekGit\BIS\Sources` (findings cite `file:line`). Dimensions: **feasibility · consistency · operations · security**. Protocol: fan-out → consolidate → fix → verify, looping until clean (per `.claude/commands/adversarial-arch-review.md`).
> **Round-1 verdict: NOT-READY (sharpen, not reject).** All four reviewers judged the *strategy* sound and the design unusually honest, but converged on one root fact the Rev-1 design under-framed — **the control unit cannot be a Session-0 boot service** — plus a network→control coupling that was asserted rather than enforced. Rev 2 of `05` resolves these. See §"Round 2" and §"Final verdict".

---

## The convergent root finding

Three of four reviewers independently hit the **same** wall from different angles: promoting the *control unit* into a Session-0, GUI-independent Windows service (Rev-1's primary path) is contradicted by the code on three counts, any one of which is disqualifying:

| Angle | Evidence | Reviewer |
|---|---|---|
| **Per-session ROT** — the singleton registers weak/session-local; a Session-0 instance is invisible to user-session GUI binders → split-brain (each session spawns its own clone) | `SingletonHolder.cs:24` `_rot.Register(ACTIVEOBJECT_WEAK,…)`; only `STRONG=0`/`WEAK=1` defined (`MSDev.cs:10-11`) — never `ROTFLAGS_ALLOWANYCLIENT`; binders at `SingletonUtils.cs:59-63,78-98` | Feasibility F1 |
| **UI-bearing control code** — the "unchanged" internals pop operator modals on **production** paths; invisible/unanswerable in Session 0 → stuck tool | `RemoteControl.cs:717-749` (host-pause `CustomMessageBoxResumeStop`/WinForms Resume-Stop), `ProductionManager.cs:986` (OK/Cancel enter-production), `ToolManager.cs:43,340,492,638-640`, `CimetrixLicenseManager.cs:167-176` | Operations C-1 |
| **Bidirectional ROT** — the control unit itself binds *other* per-session singletons (CamtekUtils), so a Session-0 host also fails **outbound** | `ToolManager.cs:23-25` → `CamtekUtilsConnector.cs:46` `SingletonUtils.GetSingleton(...)` | Feasibility F2 |

**Resolution (Rev 2):** split the hosting honestly. **Egress** (gateway + TSMC worker — no UI, network I/O only) becomes the GUI-independent boot service. **Control** becomes a **supervised interactive-session process** (a ToolHost-style watchdog in the operator session, no longer force-killed by AOI) — supervised, crash-restarted, single-owner, but **not** Session-0 and **not** boot-independent (it is UI-bearing and binds per-session ROT, so it needs the operator session — which is the tool's normal running state anyway). The SCM Session-0 service for control is demoted to a **stretch goal**, contingent on *both* a proven ROT registration rewrite (`ALLOWANYCLIENT`+AppID/RunAs) *and* headless-ifying ~20 control modals — neither of which Alt 3 requires to deliver its value. This is a genuine downgrade of the Rev-1 G2 claim, applied across §5.1/§5.4/§5.9/§5.10/§5.11.

---

## Critical findings

| ID | Reviewer(s) | Finding | Resolution (Rev 2 of `05`) |
|---|---|---|---|
| **A3-C1** | Feasibility F1/F3, Ops C-1 | Control unit cannot be a Session-0 boot service (per-session ROT + production-path modals). Rev-1 framed the *harder* path as default and analyzed Session-0 risk only for native drivers. | **Reframed:** interactive-session supervised process is the **primary** control-hosting path; egress-only is the Session-0 service; SCM control-service is a contingent stretch. G2 downgraded honestly (§5.1 G2, §5.4, §5.11 crit-2 + caveat 1). |
| **A3-C2** | Feasibility F4, Ops C-2 | AOI startup **mass-kills the whole control fleet by name** — `SECSGemDriver`(:658), `SecsGemGUI.Net`(:655), `CMM.Net.Main`(:666), `Connector`(:667), `InspectionMngService`(:668), `ToolManager`(:665) at `clsInitAOI.cs:653-668`. Rev-1 §5.8 removed only `:665`, so even a perfectly-excluded ToolManager gets its qualified wire (`SECSGemDriver`) torn out. `KillProcessByName` is name-based + cross-session. | **§5.8/§5.10 rewritten:** the *entire* `:653-668` block (and the `:478` name-sweep) must become flag-conditional with PID-exclusion for **every** process the unified supervisor now owns — not just `:665`. U2 rollback is an **atomic coordinated reconfiguration** (disable supervisor **and** re-enable AOI kills together), explicitly **not** a runtime flag. |
| **A3-C3** | Security S3-C1, Ops M-3 | The internal coordination→egress API makes the control process a **publisher into the network-facing child it supervises** — a circular, availability-coupled dependency. Command-direction isolation is credited (a gRPC server can't call its client; `EventProcessor.cs:44` routes only to sinks), but the design **asserted** "reporting only" without enforcing it: reusing the shipped blocking publisher (`ToolApiPublisher.cs:88` no deadline; `:124-137,:204-211` `Thread.Sleep(1000)`+`Process.Start` under `_lockObj`) on the control thread lets a wedged/compromised child **stall the GEM-coordination thread** — a safety-relevant control stall. Plus a new TLS/HTTP2 client parser surface in the control process. | **New §5.6 contract:** the coordination publisher **MUST** run on a dedicated bounded queue/thread, hard per-call gRPC deadline, **fire-and-forget**, never on the GEM/`IProductionManagerCB`/`ToolEvents` fan-out thread; it **must not** reuse `ToolApiPublisher`'s blocking pattern; it validates/ignores child responses; the child cannot initiate to the parent. Enforced boundary, not asserted. |
| **A3-C4** | Security S3-C2 | `:5005` intake is **identity-blind**: `PushEvent` (`ToolAPIGrpcServiceImpl.cs:41-53`) accepts arbitrary `Source/Action/Payload` and stamps the **server-side** `_toolIdentity` (`App.cs:33-42`) onto every event. Adding the service as a *second* publisher under one shared client-cert tier means **any** cert-holder (AOI, SUP, or a compromised egress child) can inject events that reach Fleet **attributed as the tool** — forged tool-state/errors/yields. mTLS authenticates the channel, not event provenance. | **§5.8 hardening:** per-publisher cert identity + **per-event-type authorization at the intake** (only the coordination brain's cert may publish ToolState/ControlMode; AOI only Scan; the egress child holds **no** publish cert) — or a distinct authenticated tool-state endpoint separate from the AOI scan intake. Added to U0/U3. |

## Major findings

| ID | Reviewer(s) | Finding | Resolution |
|---|---|---|---|
| **A3-M1** | Feasibility F2 | Cross-session ROT dependency is **bidirectional** — the control unit binds CamtekUtils and siblings per-session (`ToolManager.cs:23-25`, `CamtekUtilsConnector.cs:46`). | U0 spike scope widened to cover **outbound** singleton binds, not just GUI→service inbound (§5.4, §5.9 U0). Reinforces A3-C1 (favors interactive-session). |
| **A3-M2** | Ops M-4 | Supervision under-specified: "always-restart" is not a checkbox (SCM stops after N; a clean-exit `Environment.Exit` is never restarted); crash-loop has no backoff/alarm (a flapping control unit is **worse** than today's cleanly-dead one); child reaping across supervisor restarts undefined (job-object kill-on-close vs orphan — both fail). | §5.5/§5.10: specify restart-rate limiting + backoff + alarm; define deterministic child adoption/reaping across supervisor restarts; state the clean-exit caveat. Honest note: a supervised-restart control unit trades "cleanly dead + visible" for "auto-recovered but can flap" — mitigated by rate-limit + alarm. |
| **A3-M3** | Ops M-5 | **Two** write-only spools, not one: (1) `ToolGateway.BL` `FailedMessagesHandler` (drain only at `SinkDispatcher.cs:89` startup; overflow written `:143`, never read — = LB-A); (2) `ToolApiPublisher`'s **own** spool `c:\Fleet\ToolAPI\FailedMessages\..._Publisher.txt` (`AddFailedMessageToFile:159`) with **no reader anywhere**, on the AOI scan path and fed by the new publisher. Rev-1's "carried U0 spool fix" silently covered only store (1). | §5.8/§5.10: U0 drain fix must reconcile **both** spool stores; the `ToolApiPublisher` store recorded as **LB-D** (new live bug, below). |
| **A3-M4** | Security S3-M1 | `:5007` "minimized projection" is **named, not specified** — `ToolStatusData` (`ToolStatusData.cs:7-19`) ships `FalconVersion/CMMVersion/LoginName/BackupSystemLocation/SN/SE/Environment/ActiveSecsGemStatus`; a negative spec defaults to the full struct. Projection **location** unstated. | §5.8: positive allow-list of exposed fields; **projection happens in SUP** (trusted control side) **before** data crosses to the network-facing egress child, so `LoginName/SN` never enter the network process. |
| **A3-M5** | Security S3-M2 | Cert/credential **PKI entirely unaddressed** — today there are **no secrets** (Fleet plaintext `http://`, :5005 h2c, publisher `Insecure`); the design introduces fleet-wide mTLS on :5005/:5007 + Fleet/TSMC creds with no CA, cert store, rotation, revocation, or per-role identity, and `appsettings.json` remains the config source (plaintext-key risk). Not buildable as specified. | §5.8: name the PKI — per-tool, per-publisher-role client certs from a fab CA, in the **OS cert store** (not `appsettings`), with rotation + revocation. Underpins A3-C4. Flagged as a **human fab-cybersecurity sign-off** standing condition. |
| **A3-M6** | Security S3-M3 | Least-privilege is **conditional** on the unproven Cimetrix-under-service spike; the interactive-session fallback has an **undefined privilege floor** (likely the operator account — broader than a gMSA); and the control process *spawns* the network-facing egress child with bare `Process.Start` (`ToolManager.cs:290-302`) — if the Cimetrix spike forces elevation, the child **inherits an elevated token** → network-facing elevated process, max blast radius. | §5.8/§5.10: state the security floor for **both** hosting branches (dedicated gMSA/virtual account with enumerated rights; a dedicated *restricted* account for the interactive fallback, not the operator); **lower the token on child spawn** regardless of the parent's privilege. |
| **A3-M7** | Security S3-M4 | **No tamper-evident audit** under a distinct identity — audit today is a plain Serilog file written by the service's own identity (`Program.cs:12-16`); Alt 3 co-hosts **control** (state transitions, control-mode) + :5007 queries, so scope is **larger** than Alt 1 (A1-M6), and a service-identity compromise edits its own log. | §5.8/§5.10: append-only off-host sink (syslog/SIEM) under a distinct identity for state transitions, control-mode changes, and status queries. |
| **A3-M8** | Consistency #1 | `02-recommendation.md` scores the **selected target** against the *old* sketch in 5 places (crit-1 ✅, crit-1/crit-3 rationales, line-20 note, §2.3 U3 "clean internal API in front of PM+GEM") — the §2.2 prose pointer does not reach the scoring cells, so each reads as a bare contradiction. | **Edited (not pointer):** crit-1 glyph ✅→⚠️; crit-1/crit-3 rationales reworded to the "two doors at the wire, one brain behind them / control in-process unchanged" framing; §2.3 U3 reworded to the *reporting* coordination↔egress boundary. |
| **A3-M9** | Consistency #2 | `05` **violates its own line-5 Mermaid self-guarantee** — 4 semicolons in sequence messages/notes (L141, L194, L221, L240). | Replaced each `;` with `,`/`.` in the sequence blocks (flowchart-label `;` are safe and left). |
| **A3-M10** | Consistency #3 | `01-alternatives.md` Alt-3 (diagram edge `defined internal API (COM/local)` + pro "clean internal API replaces ad-hoc COM coupling") reads as a bare contradiction of `05` §5.2 with no pointer at the source. | **Normative pointer added** under 01's Alt-3 heading (sketch retained as the pre-correction framing `05` corrects — `05` depends on it staying the sketch). |
| **A3-M11** | Ops M-5 | **No unified process-health surface** for the enlarged process tree (service, egress child, TSMC worker, tool client); `:5007` serves tool *state*, not process liveness; logs are per-process. A field engineer can't tell from one place which of 4+ processes died. | §5.5/§5.8: add a supervisor-owned process-liveness/health surface (which child, last exit code, restart count) distinct from tool-state. |

## Minor findings (applied)

| ID | Reviewer | Finding | Resolution |
|---|---|---|---|
| A3-m1 | Feasibility F6 | TFM label imprecise — ToolManager/SecsGemObjects import `Camtek.CSharp.Common.Properties.props` (`v4.8`; a sibling copy shows `v4.6.1`); "net48" is directional. | Footnoted as ".NET Framework 4.x (v4.8 via shared props)"; the net-Framework↔net7 cross-CLR point is unaffected. |
| A3-m2 | Feasibility F5 (positive) | TSMC P/Invoke is **cleaner** than stated — isolated in `TsmcSdkClient.cs:135-163` behind `ITsmcUploadClient`, DI-injected into `TsmcSink.cs:33-40`; out-of-proc = an IPC-backed `ITsmcUploadClient`, `TsmcSink` unchanged. | §5.5 cites the real seam (`TsmcSdkClient`/`ITsmcUploadClient`) as the injection point. |
| A3-m3 | Consistency | L4 "see §5.1" misdirects (supersede detail is in §5.3/§5.6). | Fixed to "see §5.3/§5.6". |
| A3-m4 | Consistency | U-label collision — `05` U0–U3 reuse `02`'s U-labels for different phases. | One-clause note in §5.9 that `05`'s U1–U3 are its own resequenced numbering. |
| A3-m5 | Consistency | LB attribution imprecision — "missing runtime spool drain" is A1-C4, not itself an LB. | §5.10 wording "…A1-C4/C5, LB-A/B/C". |
| A3-m6 | Security min1 | gRPC reflection (`Program.cs:51`) + Swagger (`:46-47`) unconditionally on (no `IsDevelopment()` gate). | Restated as an enforced U0 gate (= LB-C). |
| A3-m7 | Security min2 | `EventsController` unauth REST `POST /api/events/push` (`EventsController.cs:40`) injects into the same sink pipeline — a second identity-blind door. | §5.8: confirm `MapControllers` is not reachable on the 0.0.0.0 binding; :5006→loopback. |
| A3-m8 | Ops m-6 | Per-tool config mechanism hand-wavy. | Noted as a config-templating work item (carried from Alt 1, unchanged severity). |

## Live bugs in shipped code (independent of this design)

Same code as Alt 1 — **referenced as already-known, not re-filed**; see [04-alt1-review.md §"Live bugs"](04-alt1-review.md):
- **LB-A (CRITICAL)** — spool overflow overwrite → unbounded silent loss (`FailedMessagesHandler.cs:142-143`) + poison replay.
- **LB-B (MAJOR)** — Fleet `ToolId=0` for alphanumeric names (`FleetMainServerClientImpl.cs:117`).
- **LB-C (MAJOR, security)** — :5005 unauthenticated `0.0.0.0` h2c + gRPC reflection + Swagger (`appsettings.json:43`, `Program.cs:46-51`), and the deadline-less blocking publisher (`ToolApiPublisher.cs:88`, `:124-137`).

**New this review:**
- **LB-D (MAJOR)** — `ToolApiPublisher` writes its **own** failed-message spool `c:\Fleet\ToolAPI\FailedMessages\..._Publisher.txt` (`ToolApiPublisher.cs:159`) that **nothing ever reads** → silent loss on the AOI scan path, independent of the `ToolGateway.BL` spool. (May overlap prior `ToolApiPublisher` notes; recorded here for this plan's completeness.)

---

## Round 2 — Verification

A verification agent (grounded in `C:\CamtekGit`) re-checked every A3-Cx/Mx against the revised docs and hunted for contradictions the fixes introduced. Result: **all 4 CRITICALs + 11 MAJORs LANDED substantively** (none PARTIAL/MISSING):
- A3-C1: `05` leads with the interactive-session supervised control process as primary; the Session-0 SCM control-service is a tagged "stretch"; G2 downgraded consistently in §5.1/§5.3/§5.4/§5.9/§5.11.
- A3-C2: §5.8/§5.10/§5.9-U2 target the whole `clsInitAOI.cs:653-668` block (incl. `SECSGemDriver:658`) + `:478` sweep with PID-exclusion; U2 rollback stated as an atomic reconfiguration, not a runtime flag.
- A3-C3: §5.6 carries the enforced bounded-queue/own-thread/deadlined/fire-and-forget publish contract, explicitly "not `ToolApiPublisher`'s pattern."
- A3-C4/M4/M5/M6/M7: §5.8 hardening rows present (per-event-type authz, allow-list projected in the control process, PKI, token-lowering on spawn, off-host audit).
- A3-M8/M9/M10/M11: `02`'s five cells edited, `05` sequence-block semicolons removed (Mermaid re-scanned — all 4 sequence diagrams clean, only permitted flowchart-label `;` remain), `01` pointer added, process-health surface in §5.5.

**One real miss the verifier caught (now fixed):** `README.md` (doc-table row 5 + TL;DR) still carried the pre-Rev-2 "one supervised net48 service owning control / cross-session-ROT" framing — corrected to the split-hosting frame (net7 egress service + interactive-session net48 control process). Two minor wording leftovers in `05` §5.1/§5.2 ("one supervised service", "the service host is net48") were also tightened. No new logic contradictions; `02`↔`05` agree on criteria 1/2/3 and U3.

## FINAL VERDICT: **READY** *(as a target design, subject to standing conditions)*

The fused thesis survives and is stronger for the review: **one supervised owner for the tool's external surface + coordination, with an honestly split hosting** (egress = GUI-independent service; control = supervised interactive-session process), out-of-proc native TSMC, and a *reporting-only, enforced-non-blocking* internal API. The review **narrowed** Alt 3 exactly as it did Alt 1 — cutting the Session-0-control overreach and the "clean API in front of the wire" fiction, and elevating the coupling/authz/PKI controls to prerequisites.

**Standing conditions (only a human or an experiment can close these):**
1. **U0 spike — control hosting:** prove interactive-session supervised hosting binds all ROT clients (inbound **and** outbound, A3-M1); the Session-0 SCM control-service remains blocked until *both* an `ALLOWANYCLIENT`+AppID ROT rewrite **and** modal headless-ification are proven (A3-C1).
2. **U0 spike — Cimetrix/motion under a restricted account** on real hardware (A3-M6); defines the privilege floor for both hosting branches.
3. **Fab-cybersecurity sign-off** on the PKI (A3-M5), the per-event-type :5005 authz (A3-C4), and the off-host audit (A3-M7).
4. **GEM record-replay** proving host-visible behaviour/timing unchanged across the control-lifecycle change (A3-C1/C2) — noting it **cannot** catch the modal-in-Session-0 fault, which the reframe removes instead.
5. **Name an owner** and confirm the control-path appetite this design assumes (§5.1).

**Standing note:** U1 (egress-child supervision + out-of-proc TSMC worker) is low-risk and security-neutral and can proceed ahead of the U2/U3 control work, per all four reviewers.
