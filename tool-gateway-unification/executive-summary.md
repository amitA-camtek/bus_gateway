# Executive Summary — Unifying the Falcon Tool Gateway

> **For:** Engineering Management · Lead Architects
> **Subject:** Unifying ToolManagement (COM control/command) and ToolGateway (gRPC reporting) into one "tool gateway" — in the current architecture, without a message bus.
> **Scope:** deliberately smaller and nearer-term than the fabric redesign. Can ship independently and becomes a clean stepping-stone toward it.
> **Status:** both candidate designs adversarially reviewed (four reviewers each, grounded in `C:\CamtekGit\BIS\Sources`), both revised post-review. **Design READY — recommended path is clear.**
> **Full design chain:** [00-problem-and-current-state.md](00-problem-and-current-state.md) · [01-alternatives.md](01-alternatives.md) · [02-recommendation.md](02-recommendation.md) · [03-alt1-complete-design.md](03-alt1-complete-design.md) · [04-alt1-review.md](04-alt1-review.md) · [05-alt3-complete-design.md](05-alt3-complete-design.md) · [06-alt3-review.md](06-alt3-review.md).

---

## 1. The problem — two disconnected external-facing components

The Falcon tool currently has two separate external-facing planes with no direct link between them:

| Plane | Component | Technology | Who feeds it | Lifecycle |
|-------|-----------|------------|--------------|-----------|
| **Control / command** | ToolManager (COM out-of-proc singleton) | .NET FW 4.8, ROT | Factory host via SECS/GEM + Cimetrix driver | Starts with the tool, lives as long as the tool runs |
| **Reporting / egress** | ToolGateway (.NET 7 Kestrel app) | net7-windows, ASP.NET Core | AOI_Main `frmScanTab` via gRPC :5005 | **Launched as an AOI child process** — dies when the operator closes the GUI |

ToolManager and ToolGateway have **no direct link today.** They share a tool PC and both talk to the outside world, but on completely separate planes. The consequences:

- **Two external surfaces, two mental models.** "How does the tool talk to the world?" has two unrelated answers. Adding a new external integration (MES, analytics, new host capability) requires touching a different world depending on which direction data flows.
- **Reporting stops when the GUI closes.** ToolGateway is an opt-in AOI child process — it dies when the operator application closes, which is precisely the moment fleet visibility matters most (maintenance windows, overnight runs).
- **Silent data loss today.** The spool overflow silently overwrites old events (no counter, no alarm). There is no runtime drain loop — the always-on service that U1 creates would expose this bug at scale. Five live defects were found during this investigation (see §4 below).
- **Unauthenticated network surface.** ToolGateway listens on `0.0.0.0:5005` with gRPC reflection enabled — an externally-reachable unauthenticated door inside the operator's application, opened by an installer firewall rule.

---

## 2. The honest goal — "two doors, one brain"

A natural reading of "unify to one gateway" suggests collapsing everything into a single component. The design rejected that interpretation for a principled reason: the factory host's GEM wire is **fab-qualified** and must not be re-routed, re-hosted, or destabilized. No design that respects that constraint can route the factory host through the gateway.

The honest goal is therefore **"two doors, one brain"**: the factory host keeps the GEM door (unchanged wire, no re-qualification); everything else — Fleet, TSMC, MES, read-only status, future non-host integrations — connects to **one supervised, hardened gateway**. The two doors are owned by different components, but the non-host surface is unified: one place to extend, one lifecycle to operate.

---

## 3. The two designed options

Three alternatives were designed and scored against six success criteria. Alternative 2 (co-hosted merge — put the state machine and the TSMC native DLL in the same process) was **rejected**: it couples the tool's most safety-critical function to its most fragile one, breaks the crash-domain isolation criterion, and is difficult to reverse. The two viable options are:

### Alternative 1 — Unified Gateway Facade (recommended now)

ToolGateway becomes a **hardened, independently supervised Windows service** — not an AOI child. It becomes the tool's **single non-host external surface** for reporting and read-only status, while the control plane (ToolManager, ProductionManager, GEM) stays exactly where it is. A separate, least-privilege, read-only `.NET 4.8` shim provides ToolManager status to the gateway without crossing a CLR boundary in the control path.

**Key post-review clarifications (Rev 2):**
- **No external command relay.** The original design included a `CommandIntake` endpoint for inbound non-host commands. The review removed it: a control entry point cannot be safely authorized inside a network-facing facade without becoming a privilege-escalation surface.
- **Read-only coupling only.** The control-plane shim is strictly read-only and least-privilege — a security boundary, not a re-hosting.
- **U0 prerequisites are hard gates.** The spool drain bug, the overflow overwrite, Fleet `ToolId=0`, and the `:5005` authentication gap must be fixed **before** the service promotion (they become critical when the service runs 24/7 instead of dying on GUI close).

**Delivers:** single non-host surface · GUI-independent lifetime · spool drains on restart · :5005 hardened. **Effort:** S–M. **Fab re-qualification:** none.

### Alternative 3 — Unified Tool Gateway Service (the target)

One supervisor owns both the tool's coordination and all non-host external I/O. The control unit (ToolManager internals) is carried **in-process, unchanged** — the GEM wire stays intact — and the one buildable internal API is the *reporting direction only* (the control process publishes tool-state events to the egress service: bounded, deadlined, fire-and-forget). Native TSMC is isolated out-of-process.

**Key post-review clarification (Rev 2):** the control unit **cannot** be a Session-0 boot service. Its ROT registration is provably per-session (`SingletonHolder.cs:24`), and production code paths pop operator modal dialogs. The control unit is therefore a **supervised interactive-session process** (not killed by AOI, but not a Session-0 service either). The egress gateway is the GUI-independent service. This split is honest about what the code actually allows.

**Delivers:** genuinely unified supervision · clean internal API · the exact component shape the later bus architecture needs. **Effort:** M–L. **Feasibility turns on two U0 spikes** (interactive-session ROT bind, Cimetrix under a restricted account).

---

## 4. Live defects in today's shipped code (file regardless of this decision)

These were found during the code investigation and are independent of the unification program:

| # | Bug | Location |
|---|-----|----------|
| **LB1** | Spool poison loop — no retry cap/age limit; a deterministic-fail message cycles forever | `ToolGateway.BL\EventMessages\FailedMessagesHandler.cs:98–153` |
| **LB2** | `ToolApiPublisher` blocks the scan thread — `Thread.Sleep(1000)` + process spawn, no gRPC deadline | `system\CamtekSystem\PubSub\ToolApi\ToolApiPublisher.cs` |
| **LB3** | **Fleet ToolId identity collapse** — `int.TryParse("BH01") → 0`; every alphanumeric tool registers as ToolId 0 (fleet-wide collision) | `ToolGateway.Endpoint\Services\FleetMainServerClientImpl.cs:115–125` |
| **LB4** | **`NonBlockingUITask` timeout bug** — `.Milliseconds` (0–999 component) instead of `.TotalMilliseconds`; any whole-second timeout ⇒ 0 ms ⇒ spurious cancellation | `system\CamtekSystem\AsyncTask\NonBlockingUITask.cs:24,41` |
| **LB5** | No spool drain loop exists — restore only at start, capped 10,000, `TryWrite` re-spools ~9k of 10k | `ToolGateway.BL\Sinks\SinkDispatcher.cs` + `FailedMessagesHandler.cs:49–52,108–109` |

LB3 and LB4 are recommended for immediate ADO filing — both affect production behavior on every fleet tool today.

---

## 5. Recommendation and phased path

**Adopt Alternative 1 now. Treat Alternative 3 as the funded target.**

| Phase | What happens | Risk | Gate |
|-------|-------------|------|------|
| **U0 — Prerequisites** | Fix LB1/LB2/LB5 (spool), verify/fix LB3 (ToolId), harden `:5005` (TLS/auth, reflection off), make AOI process-sweep service-exclusive, name an owner | none | Hard prerequisite — gates U1 |
| **U1 — Hardened service** | ToolGateway promoted to a supervised Windows service; GUI-independent; spool survives restarts | low | Flag rollback for one release |
| **U2 — Single non-host surface (Alt 1 complete)** | Gateway declared the one non-host integration point; read-only `ToolStatusShim` added; new consumers attach here | low–med | Flag / remove shim |
| **U3 — Reporting API (Alt 3 groundwork)** | Control process publishes tool-state events to egress — bounded, deadlined, fire-and-forget; internals unchanged | med | Dual-path behind flag |
| **U4 — Unified ownership (Alt 3 complete)** | One supervisor owns control process + egress service; native TSMC isolated out-of-process | med | Atomic reconfiguration |

U0–U2 ship as Alternative 1 — independently valuable, no bus required. U3–U4 are the Alt 3 promotion, taken when a full service consolidation is funded and an owner exists. **If Alt 3 is committed from the start,** consider going straight to it (still phased internally as U0 → U3 → U4) and skipping U2's read-only shim, which Alt 3 supersedes.

---

## 6. Relationship to the bus program

This unification is **not** a competitor to the fabric redesign — it is a safe, current-architecture step that reduces the eventual bus migration's risk. Alternative 3's unified service is precisely the "ToolConnect gateway" shape the bus architecture already assumes as its external door. If the bus program is funded, this work is absorbed. If it is not, the tool still gets a single, well-supervised gateway.

**Either way, the U0 prerequisites (spool fixes, ToolId, :5005 hardening) are valuable immediately and independently.**
