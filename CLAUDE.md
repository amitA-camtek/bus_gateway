# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this directory is

`c:\Users\me_admin\Desktop\New folder\bus_gateway` is an **architecture reference workspace** for the Camtek Falcon AOI project. It contains design documents and code sketches, not production source code. The actual monorepo is at `C:\CamtekGit\` — start there for any code changes.

**Start with [LEGEND.md](LEGEND.md)** — the document index, folder structure, and normative-vs-historical rules.

---

## Folder map

| Folder | Role |
|--------|------|
| `stage\` | **Primary reference** — the current, self-contained, DESIGN READY package (5 adversarial cycles, all findings resolved). Start here. |
| `tool-gateway-unification\` | Near-term design: unify ToolManager + ToolGateway without the bus. Independent of `stage\`; can ship first. |
| `01-proposal\` | Authoritative full-detail designs (bus, AOI_Main, ToolHost, fabric) — normative where `stage\` cross-references depth |
| `02-reviews\` | Audit trail / review records (read-only history) |
| `03-inputs\` | Independent source analyses used as input evidence |
| `04-history\` | Superseded designs kept as the decision record — **do not implement from these** |
| `05-reference\` | Pre-existing docs describing today's system (with corrections notes) |

---

## Primary design set — `stage\`

The `stage\` folder is the entry point for any implementation or architectural discussion. It is fully self-contained (no links point outside it) and structured top-down with no duplication.

| # | Document | What it covers |
|---|----------|----------------|
| — | [stage/executive-summary.md](stage/executive-summary.md) | Leadership entry point: business case, design confidence (5 cycles), wave plan summary, recommendation |
| 0 | [stage/00-context-and-case.md](stage/00-context-and-case.md) | Today's architecture (verified baseline), the seven pain points, business case |
| 1 | [stage/01-system-architecture.md](stage/01-system-architecture.md) | System diagrams; complete design of each system-level new component (bus broker, ToolConnect gateway, ToolHost, GEM shim) |
| 2 | [stage/02-aoi-architecture.md](stage/02-aoi-architecture.md) | AOI_Main internals, all new AOI-level components with code snippets, and the **link-disposition table** (§2.9 — every ~21 links → lane) |
| 3 | [stage/03-appendix-four-lanes.md](stage/03-appendix-four-lanes.md) | Four migration lanes: BUS / SVC / CONS / KEEP — per-lane complete design, flows, key code snippet |
| 4 | [stage/04-impact-analysis.md](stage/04-impact-analysis.md) | Projects created, modified, and retired — per `C:\CamtekGit\BIS\Sources\…` project, per phase |
| 5 | [stage/05-roadmap-and-risks.md](stage/05-roadmap-and-risks.md) | Wave plan, per-edge gates, governance, rollback, **five live bugs** in shipped code |
| 6 | [stage/06-bus-implementation.md](stage/06-bus-implementation.md) | Bus build spec: API, envelope, wire protocol, journal, broker, security, load model, 14-group test kit |
| 7 | [stage/07-toolconnect-design.md](stage/07-toolconnect-design.md) | ToolConnect gateway build spec: WAL state machine, class design, :5007 command pipeline, CMM proxy, failure matrix |

**Code sketches** are in [stage/codeSnippets/](stage/codeSnippets/) (16 files, design-level C# — not production-ready). The `00-README.md` there lists known sketch defects (S-1..S-18); design docs are normative where they diverge from the sketches.

**Review record and open decisions:** [stage/stage-review.md](stage/stage-review.md) (5th-cycle findings + round-2 "DESIGN READY" verdict), [stage/stage-decision-briefs.md](stage/stage-decision-briefs.md) (every fork decided).

---

## Near-term design — `tool-gateway-unification\`

Smaller, independent scope: unify the tool's two external-facing components without the message bus. Can ship before the fabric migration.

| Document | Contents |
|----------|----------|
| [executive-summary.md](tool-gateway-unification/executive-summary.md) | Leadership entry point: the problem, the two designed options, live bugs, recommended path |
| [00-problem-and-current-state.md](tool-gateway-unification/00-problem-and-current-state.md) | What "unify" means, today's two components (verified), six success criteria |
| [01-alternatives.md](tool-gateway-unification/01-alternatives.md) | Three designs — facade / co-hosted merge / unified service — with pros, cons, effort |
| [02-recommendation.md](tool-gateway-unification/02-recommendation.md) | Recommended path: Alt 1 now → Alt 3 as the funded target |
| [03-alt1-complete-design.md](tool-gateway-unification/03-alt1-complete-design.md) | **Alt 1 (Rev 2)** — "two doors" architecture, three sub-designs, phases U0–U2 |
| [04-alt1-review.md](tool-gateway-unification/04-alt1-review.md) | Adversarial review of Alt 1; resolutions that produced Rev 2; shipped-code bugs |
| [05-alt3-complete-design.md](tool-gateway-unification/05-alt3-complete-design.md) | **Alt 3 (Rev 2)** — one supervisor with split hosting (GUI-independent egress + supervised interactive-session control), phases U0–U3 |
| [06-alt3-review.md](tool-gateway-unification/06-alt3-review.md) | Adversarial review of Alt 3; findings + resolutions |

---

## Real code — where to look

All production source lives under `C:\CamtekGit\BIS\Sources\`. Key files referenced by the documents here:

| Concern | Path (relative to `BIS\Sources\`) |
|---------|----------------------------------|
| `frmProduction` controller | `apps\Falcon.Net\Forms\frmProduction.cs` |
| ToolManager COM singleton | `ToolManagement\ToolManager\Server\ToolManager.cs` |
| ProductionManager engine | `ToolManagement\ToolManager\ProductionManager\ProductionManager.cs` |
| COM event bus | `ToolManagement\ToolManager\Server\ToolEvents.cs` |
| ToolManagerUiWrapper | `apps\Falcon.Net\CommonUtils\ComServerWrappers\ToolManagerUiWrapper.cs` |
| ExternalControlCbUiWrapper | `apps\Falcon.Net\CommonUtils\ComServerWrappers\ExternalControlCbUiWrapper.cs` |
| Start-Production trigger | `apps\Falcon.Net\Forms\frmJobTab.cs:941–982` |
| ToolGateway hook points | `apps\Falcon.Net\Forms\frmScanTab.cs` ~:1888–1902 and :10162 |
| gRPC publisher | `system\CamtekSystem\PubSub\ToolApi\ToolApiPublisher.cs:88` |
| SECS/GEM host mapping (live) | `ToolManagement\SecsGemObjects\Clients\RemoteControllers\RemoteControl.cs` |
| COM event hub host | `ToolManagement\FalconWrapper\` — `FalconWrapper.exe` (out-of-proc ATL EXE) |
| TSMC bridge | `apps\Falcon.Net\Modules\Tsmc\ScanResultsReady.cs` |

The broader monorepo CLAUDE.md (build commands, C# standards, git conventions) is at `C:\CamtekGit\CLAUDE.md`.

---

## Key non-obvious facts

- **`frmProduction` is invisible.** Despite the `frm` prefix it has no UI — VB6→C# port using "form-as-code-module + COM host" pattern. Access via `MainContext.Instance.Forms.frmProduction`.
- **ToolGateway bypasses `frmProduction`.** The gRPC push to ToolGateway (:5005) originates in `frmScanTab` (~:1888–1902, :10162), *after* results are copied to their stable path. `frmProduction.FireWaferScanResultsAreReady` fires earlier on the COM bus and ToolGateway does not subscribe to that bus. The push is not reliably fire-and-forget — `ToolApiPublisher` can block the scan thread when the gateway is down (`Thread.Sleep(1000)` + process spawn, no gRPC deadline).
- **`ChangeToolState` has exactly three callers:** `clsInitAOI.cs` (Engineering on startup) and `frmJobTab.cs` (Production — two code paths, lines 948 and 981).
- **COM event direction is two-way.** `frmProduction` pushes ~25 `Fire*` events *down* into `mFalconFireEvents`; COM callbacks from ToolManager fire *up* via `OnToolStateChanged`. Different wrappers, different interfaces.
- **`ExternalControlCbUiWrapper` only forwards two commands to `frmProduction`:** `GuiStartManualScan` and `GuiExportMap`. All other factory-host commands are handled inside the wrapper.
- **Tool state enum:** `NotInitialized → Initialization → Engineering ↔ EngineeringToProduction → Production`. Failure during `EngineeringToProduction` reverts to `Engineering`.
- **Five live defects in shipped code** were found during this program (including a fleet-wide identity collision — every alphanumeric tool registers with Fleet as ToolId 0). See [stage/05-roadmap-and-risks.md §5.5](stage/05-roadmap-and-risks.md) and [stage/codeSnippets/16-live-bug-fixes.cs](stage/codeSnippets/16-live-bug-fixes.cs).
- **Code sketch idiom split:** `net48` files use C# 7.3 (no records, no switch expressions, explicit `default(T)`); `net8` files use C# 12 (records, nullable refs, primary constructors).

---

## Document conventions

- **Normative vs historical:** when two documents cover the same topic, the `01-proposal\` (or `stage\`) doc is normative. Historical docs keep their original text under a dated ⚠ banner — never silently rewritten.
- **Finding IDs:** `C*/M*/T*` = first review · `R1-*/R2-*` = AOI review rounds · `CC*/CM*` = concurrency review criticals/majors · `LB*` = live bugs in shipped code · `A-*` = AOI design risk register rows · `S-*` = 5th-cycle sketch defects.
- **Phase vocabulary:** `P0–P5` = fabric roadmap phases · `Waves 0–2 + Deferred` = the funded execution plan · `BUS/SVC/CONS/KEEP` = the four migration lane tracks · `U0–U3` = the tool-gateway-unification phases.
- **Consistency rule:** the `stage\` and `01-proposal\` sets were verified mutually consistent. If you edit any normative document, sweep companions for the same fact.
