# Falcon AOI — Architecture Review & ToolGateway Consolidation Investigation

> Senior architecture review of the Falcon AOI system and investigation into removing/merging ToolGateway.
> Sources: [frmProduction.md](../05-reference/frmProduction.md), [falcon-aoi-architecture-reference.md](../05-reference/falcon-aoi-architecture-reference.md)
> Date: 2026-07-16
>
> **⚠ Corrections found by later adversarial review ([a3-fused-design-review.md](../02-reviews/a3-fused-design-review.md))** — this doc is kept as the historical record; read it with these fixes: (1) the live SECS/GEM stack is **C# `SecsGemObjects` in the `SecsGemGui.Net` process over the native Cimetrix `SECSGemDriver`** — the C++ `SecsGemClient` is legacy (not in `Falcon_2022.sln`); (2) the COM event hub lives in **`FalconWrapper.exe`** (out-of-proc, 5 client subscribers); (3) frmScanTab's real publish hooks are ~:1888-1902 and :10162 (not :7301); (4) the "<5 ms fire-and-forget, never blocks the scan thread" claim (finding S1 below) is **not true today** — `ToolApiPublisher` can sleep 1s + spawn a process on the scan thread and has no gRPC deadline, and its failed-message file is never read back (review T3/T4).

---

## 1. Architecture Summary

Semiconductor wafer AOI platform composed of several OS processes:

```
Factory Host (SECS/GEM)
        │ COM (SecsGemClient C++)
        ▼
Falcon.Net AOI  (.NET FW 4.8, WinForms)
  ├── frmProduction (invisible controller — 4 COM wrappers)
  ├── ToolManager (COM singleton — tool state machine)
  │     └── ProductionManager → EFEM / Automation hardware
  └── frmScanTab → ToolApiPublisher.PushEvent()
        │ gRPC :5005 (fire-and-forget, <5 ms)
        ▼
ToolGateway  (.NET 7 Windows Service)
  ├── FleetSink → Fleet.Main (gRPC :5050)
  └── TsmcSink  → TsmcZipBuilder → TsmcSdkClient (P/Invoke native DLL) → TSMC Cloud

DataServer  (.NET 8, gRPC :5050) — 13 service modules exposing inspection data
```

Key facts:

- **`frmProduction`** is an invisible WinForms controller (VB6→C# port), the GUI-side bridge to four COM back-ends. Accessed globally via `MainContext.Instance.Forms.frmProduction`.
- **Tool state machine** (`ToolManager`, COM singleton): `NotInitialized → Initialization → Engineering ↔ EngineeringToProduction → Production`. `ChangeToolState` has exactly 3 callers (`clsInitAOI.cs` startup; `frmJobTab.cs:948,981` Start Production).
- **Two event paths with different timing semantics:**
  - *Early* — COM `Fire*` bus (`FireWaferScanResultsAreReady`) fires while results are still being copied. Consumers: SECS/GEM, ProductionManager.
  - *Late* — gRPC push from `frmScanTab.OnReportScanResults` (frmScanTab.cs:7301), fires only after `CopyScanResults()` completes and files are at their stable path. Consumer: ToolGateway.
- **ToolGateway bypasses `frmProduction` entirely** — it is not a subscriber on the COM event bus.

---

## 2. Architecture Review — Findings

### Strengths

| # | Finding |
|---|---------|
| S1 | **ToolGateway is correctly decoupled** — separate process, fire-and-forget call (<5 ms), never blocks the scan thread. Sink pattern (`EventRouter` → `FleetSink`/`TsmcSink`) means new downstream consumers touch nothing in Falcon.Net. Textbook strangler-fig. |
| S2 | **Trigger point chosen deliberately** — firing after `CopyScanResults()` (stable path) rather than on the early COM event avoids a half-copied-file race. |
| S3 | **State machine is centralized and small** — one COM singleton owns tool state; only 3 `ChangeToolState` callers. `ProductionManager` never moves motors directly (delegates via `IAutomationManager`/`IEfemServices`). |
| S4 | **Remote-control blast radius contained** — `ExternalControlCbUiWrapper` forwards only 2 commands to `frmProduction` (`GuiStartManualScan`, `GuiExportMap`); everything else handled in the wrapper. |

### Concerns

| # | Finding | Severity | Cost to fix |
|---|---------|----------|-------------|
| C1 | **Early-vs-stable event ambiguity is implicit.** Nothing in code enforces which bus a new consumer should use; a developer wiring a data consumer to the COM bus will read half-copied files intermittently. Rule lives only in docs/tribal knowledge. Fix: rename events (`…Announced` vs `…Committed`) or gate file access behind a committed-results API. | High | Low |
| C2 | **`frmScanTab` is the architectural hotspot.** 10,000+ lines, ~40 `Fire*` call sites, both ToolGateway hooks (7301, 10155). Every new integration edits it (feature 75049 did). Fix: formalize the existing `OnReportScanResults → ScanResultsReady.Publish()` seam into an `IScanResultsPipeline` that consumers subscribe to. | High | Medium |
| C3 | **CB callback bus failure semantics uncharacterized.** `CallbackHandler`/`I*CB` fan-out across COM process boundaries — unknown behavior when a subscriber throws/hangs; a stalled state callback desyncs GUI from tool state. Fix: fault-injection tests. | Medium-High | Low |
| C4 | **`frmProduction` is a God-broker** — 4 unrelated responsibilities in ~1,137 lines, service-locator access, Form lifecycle on STA thread ⇒ near-zero testability, temporal-coupling startup order. Fix: extract wrapper groups behind interfaces; keep the form as a thin composition shell. | Medium | Medium |
| C5 | **.NET 7 on ToolGateway is out of support** — should converge to .NET 8 (same as DataServer). | Medium | Low |
| C6 | **Single-instance assumptions** (COM singletons, fixed ports 5005/5050, INI config) make side-by-side instances and simulation-based CI structurally hard. | Known/accepted | High |

**Bottom line:** a legacy industrial architecture being evolved the right way — capability added at clean seams rather than rewriting the COM core. Biggest liability is not the old technology but that the most important correctness rule (C1) is implicit and the most-modified file (C2) is the least structured. Prioritize C1, then C2.

---

## 3. Investigation — Remove ToolGateway / Merge into ToolManager?

**Question:** Can ToolGateway be removed or combined with another component? Is ToolManager the right merge target?

**Answer: do not merge into ToolManager.** If consolidation is required, fold the sinks into the .NET 8 service side (DataServer/Fleet host). Otherwise keep ToolGateway as is.

### 3.1 What ToolGateway buys (must be re-provided by any merge)

1. **Crash isolation** — `TsmcSdkClient` P/Invokes a native DLL; a native AV kills its process. Today that process is a disposable gateway.
2. **Non-blocking scan thread** — queue, zip building, retries, and network waits live out-of-process.
3. **Runtime freedom** — .NET 7 with modern `Grpc.Net.Client`/async; Falcon.Net and ToolManager are .NET FW 4.8/COM where the modern gRPC stack doesn't run (deprecated `Grpc.Core` fallback only).
4. **Independent lifecycle** — restart/upgrade without stopping the tool; upload queue survives AOI restarts.

### 3.2 Option comparison

| Option | Verdict | Rationale |
|---|---|---|
| **A. Keep ToolGateway** | **Recommended default** | All four properties preserved; cost is one extra Windows service |
| **B. Host ToolGateway's services inside the DataServer host (.NET 8)** | Possible, but weaker than first assessed — see §3.6 | Same runtime, one fewer Windows service; but Fleet.Main is a *remote central server*, so the local spooling intermediary is still needed — nothing is architecturally simplified, only co-hosted |
| **C. Merge into Falcon.Net (AOI process)** | Possible but expensive | .NET FW gRPC client pain; native DLL crash takes down operator GUI mid-production; persistent queue must be rebuilt in-process |
| **D. Merge into ToolManager** | **Strongly advise against** | See §3.3 |

### 3.3 Why ToolManager is the wrong host

- **It's the tool state machine** — the COM singleton deciding whether the tool is in Production. A hang/crash in an upload path (native DLL, network stall, giant zip) stops wafer production. Couples the least-critical function (cloud reporting) to the most-critical one.
- **Wrong data path** — ToolManager doesn't see scan results. Its event world is the *early* COM bus; merging there either re-plumbs the data path or reintroduces the half-copied-file race the current design avoids.
- **Wrong tech stack** — COM apartment threading + .NET FW 4.8 vs. async gRPC/channels; every piece of ToolGateway would be rework, not relocation.
- **Wrong lifecycle** — ToolManager runs when the tool runs; durable upload queues across restarts were never in its design.

### 3.4 Removal checklist (any target)

1. **Inventory port 5005 consumers** — confirm `ToolApiPublisher` is the only publisher and FleetSink + TsmcSink the only routes. Note: Falcon.Net has two TSMC trigger modules (`Modules\Tsmc\ScanResultsReady.cs` and `Modules\Tsmc\ScanComplete.cs`).
2. ~~Verify what Fleet.Main is~~ **VERIFIED (repo):** Fleet.Main is a **remote central fleet server** (`http://10.5.1.106:5050` in `ToolGateway.Endpoint\appsettings.json`), *not* DataServer. Tools register with it via `RegisterTool` (int ToolId, machine name). DataServer's :5050 is a separate on-tool host with zero Fleet references. The port collision in the docs was a coincidence.
3. **Preserve the fire-and-forget contract** — keep the `IPublisher` / `PublisherFactory` (INI-driven) seam so the Falcon.Net change is a config/factory edit, not a frmScanTab edit.
4. **Re-home the queue with persistence** — today's service queue buffers across outages and AOI restarts; the new host needs equivalent durability, or event loss on restart must be explicitly accepted.
5. **Sandbox the native DLL** — keep `TsmcClientShim.dll` in a child process, or at minimum isolate its failure mode from the host's core function.
6. **Ops changes** — retire the Windows service from the WiX installer, migrate config, close port 5005 in firewall rules, update `DeployUI` scripts.
7. **Failure-mode tests before cutover** — network down mid-upload, TSMC DLL crash, oversized zip, AOI restart with queued events, Fleet endpoint unreachable.

### 3.5 Recommendation

**Keep ToolGateway (Option A).** The repo investigation (§3.6) strengthened this: ToolGateway is the per-tool *edge agent* for a remote central server — it spools events across network outages, is the only component in this chain with a real automated test suite, and merging it into DataServer (which has no tests) would be a quality downgrade for zero architectural simplification.

### 3.6 Repo Investigation Findings (verified in `C:\CamtekGit`)

| Finding | Evidence |
|---|---|
| ToolGateway source location | `BIS\Sources\Utilities\ToolGateway\` — projects: `ToolGateway.BL`, `ToolGateway.Endpoint`, `ToolGateway.Tests`, `TsmcMockServer` |
| **Fleet.Main is a remote central server, not DataServer** | `ToolGateway.Endpoint\appsettings.json`: `"MainServerAddress": "http://10.5.1.106:5050"` — a network address, not localhost. DataServer has zero Fleet references |
| Fleet is a fleet-management system | `FleetMainServerClientImpl.cs`: `RegisterToolAsync` registers the tool (int ToolId e.g. "BH01", machine name); Fleet.Main overwrites the ip from the peer address on `PushEvent` |
| Fleet client is proto-first, DataServer is code-first | Fleet uses `fleetmain.proto` + `Google.Protobuf` + `Grpc.Net.Client`; DataServer uses code-first protobuf-net — different gRPC styles (can coexist in one host, but nothing is shared) |
| **Spooling/durability is real** | `FleetMainServerClientImpl.cs:56` — "message will be spooled" on RPC failure; `FailedMessagesHandlerTests` covers it |
| **ToolGateway has a test suite; DataServer has none** | `ToolGateway.Tests\`: EventRouter, FleetSink, TsmcSink, TsmcSinkAuditLog, FailedMessagesHandler, EventProcessor, SinkDispatcher, ConfigurationBinding, NonFunctional tests. DataServer CLAUDE.md: "There are no automated tests in this solution" |
| Two TSMC trigger modules in Falcon.Net | `apps\Falcon.Net\Modules\Tsmc\ScanResultsReady.cs` and `ScanComplete.cs` |

**Implication for Option B:** since Fleet.Main is remote, a local store-and-forward intermediary on the tool PC is required regardless — removing ToolGateway doesn't remove the responsibility, it only relocates it. Option B is therefore pure *co-hosting* (one fewer Windows service), not simplification, and it moves tested code into an untested host. Only worth doing if service-count reduction is an explicit operational requirement.

---

## 4. Redesign Investigation — "Reduce the Number of Windows Services on the Tool"

> Repo-wide inventory of every Windows service / long-running process on a tool PC, and a redesign proposal. Verified in `C:\CamtekGit` (three parallel investigations, 2026-07-16).

### 4.1 Decisive finding: ToolGateway is NOT a Windows service

Despite `UseWindowsService()` in `ToolGateway.Endpoint\Program.cs:19`, there is **no installer, no `sc create`, no WiX ServiceInstall** for it anywhere in the repo. In production it is:

- Deployed by `dotnet publish` to `C:\bis\bin\x64\ToolGateway\` (`DeployUI\helpers\msbuild.actions.ps1:191-200`)
- **Launched as a child process of AOI_Main** (`apps\Falcon.Net\Classes\clsInitAOI.cs:380-495`) — opt-in via `system.ini` `general/ToolGatewayEnabled=1`
- Bound to a **job object** so it dies automatically when AOI_Main exits; restarted with 5s backoff, max 3 attempts
- `UseWindowsService()` is a no-op when not launched by the SCM — the same exe works as a console child

**Conclusion: ToolGateway contributes ZERO to the Windows-service count today.** The requirement "reduce services, in the context of ToolGateway" is already satisfied by its current design — and its child-process supervision pattern is in fact the template the rest of the tool should copy.

### 4.2 Actual per-tool process inventory (verified)

| Component | Kind | TFM | Ports | Installed/launched by |
|---|---|---|---|---|
| **Camtek.DataServer** | **Windows service** (auto-start, restart-on-failure) | net8.0 | gRPC :5050 (any IP) | WiX MSI (`Product.wxs:128`) |
| **Camtek.RMSToolService** | **Windows service** (delayed-auto) | net6.0 | :5020 | RMS Installer via `sc create` |
| **FAR/EDC "Service1"** | **Windows service** — pure supervisor for a Python gRPC server | .NET FW 4.6.1 | Python child on :1235 | InstallUtil/sc |
| ToolGateway | Child process of AOI_Main (job object) | net7.0-windows | gRPC :5005, REST :5006 | `clsInitAOI.cs` |
| EBI.EbiServer (+ImageProc etc.) | Child console processes, single-instance mutex | — | :1234 (localhost) | App fleet |
| CamtekTray | Tray app with gRPC notification service | — | — | User session |
| AOI_Main + COM servers (ToolManager, SecsGemClient…) | Desktop app + COM out-of-proc | net FW 4.8 / C++ | — | Operator |

Central-server-side (NOT on the tool): `Camtek.RMSServer` (:5001), Fleet.Main (10.5.1.106:5050). BSIHR's two services (`ServiceControl` :1234, `DatabaseServer` :4578) belong to the next-gen BSIHR product line — scope depends on platform.

**True Windows-service count on a Falcon tool PC: 3** (DataServer, RMSToolService, FAR supervisor) — none of them ToolGateway.

### 4.3 Redesign options

| Option | Service count | Assessment |
|---|---|---|
| **R0. Status quo** | 3 | ToolGateway already optimal for the stated goal |
| **R1. Move ToolGateway into the DataServer service** | 3 (unchanged!) | Doesn't reduce count — DataServer exists anyway. Only real gain: ToolGateway would outlive AOI_Main (spool drains / fleet telemetry while AOI is closed). Costs: tested code into untested host; TSMC native DLL (`TsmcClientShim.dll` P/Invoke) crash takes down all 13 data modules |
| **R2. Tool Host Supervisor (recommended)** | **3 → 1** | One new `Camtek.ToolHost` Windows service supervising the existing exes as job-object child processes — generalizing the pattern already proven 3× in this repo (AOI_Main→ToolGateway, FAR service→Python, BSIHR ServiceControl→MainServer loader) |
| **R3. Full modular-monolith merge** | 3 → 1 | All modules in one .NET 8 process. Max reduction, max coupling: one crash/update restarts everything; native DLL + EOL frameworks in one process; team release cadences coupled. Not recommended |

### 4.4 Recommended design — R2 "Camtek.ToolHost" supervisor

One .NET 8 Windows service that:

1. **Supervises child processes** via job objects + restart policy: `Camtek.DataServer.GrpcHost.exe`, `Camtek.RMS.Service4Tool.exe`, the FAR Python server (absorbing the FAR supervisor service, whose *only job* is supervision), and optionally `ToolGateway.Endpoint.exe`.
2. **Aggregates health** — DataServer, ToolGateway (`/health`), RMS expose endpoints; ToolHost polls and exposes one tool-level health surface.
3. **Owns nothing else** — no business logic. Existing exes, ports, and clients (MDC → :5050) are untouched; the change is *service registration*, not code.

Result: **3 Windows services → 1**, while *keeping* process/crash isolation (unlike R3). Native-DLL risk stays contained in the ToolGateway child. Deployment simplifies to one ServiceInstall; per-component updates remain independent (ToolHost restarts one child).

Migration order (each step independently shippable):
1. **Absorb FAR supervisor** (lowest risk — it's already just a supervisor; retire a .NET FW 4.6.1 service) → 3→2
2. **Re-register DataServer** as ToolHost child instead of own service (WiX change only) → 2→1... plus RMSToolService the same way → 1 total
3. **Optional:** move ToolGateway from AOI_Main child → ToolHost child *if* fleet needs telemetry while AOI is closed (today spooled Fleet events don't drain when the AOI app is shut — arguably the moment the fleet server most wants tool status)
4. **Prerequisite hygiene regardless:** net6/net7 are EOL — RMS Service4Tool and ToolGateway should move to .NET 8 LTS

### 4.5 ToolGateway-specific verdict under the service-reduction goal

- Merging ToolGateway anywhere **does not reduce the service count** — it was never a service.
- Its child-process + job-object + spool design is the *pattern to standardize*, not the problem to eliminate.
- The only ToolGateway design question the investigation surfaced: **whose child should it be?** AOI_Main-child (today) means no fleet telemetry when the AOI is closed; ToolHost-child would fix that. Decide based on whether Fleet.Main needs tool-down visibility.

---

## 5. frmProduction Integration — Impact per Option

> `frmProduction` is deliberately **outside** the ToolGateway data path today. The measure of each option is whether it preserves that separation or drags `frmProduction` back in.

### 5.1 Baseline — where frmProduction stands

Two parallel paths leave the AOI when a wafer finishes:

```
frmScanTab
 ├── frmProduction.FireWaferScanResultsAreReady()   ← EARLY: COM bus (SECS/GEM, ProductionManager)
 │        [results still being copied]
 └── OnReportScanResults (frmScanTab.cs:7301)        ← LATE: results at stable path
          → ToolApiPublisher.PushEvent (gRPC :5005)  → ToolGateway
```

`frmProduction` owns the **early** path only; ToolGateway never touches it. The only other connection is coincidental: `clsInitAOI.cs` both drives `frmProduction`'s startup (`FalconIsStartingUp` → `Init` → `ChangeToolState(Engineering)`) *and*, in separate code in the same file, launches ToolGateway (`EnsureToolGatewayRunning`, ~line 380). Same file, unrelated logic.

### 5.2 Impact per option

| Option | frmProduction impact | Detail |
|---|---|---|
| **A. Keep ToolGateway** | **None** | Zero interaction; the two paths stay parallel. This separation is the invariant worth protecting. |
| **B. Merge into DataServer** | **None** | Publisher call lives in `frmScanTab` behind the `IPublisher` abstraction; only the target address changes. frmProduction's COM bus continues unchanged. |
| **C. Merge into Falcon.Net** | **Indirect — shared fate + temptation** | (1) Sinks and `TsmcClientShim.dll` now run inside the process hosting frmProduction — a native crash kills the whole GUI mid-production, including the tool-state bridge (`ToolStateChanged` handling lost at the worst moment). (2) Standing temptation: with sinks in-process, the "easy" wiring for future events is the `Fire*` bridge — i.e., subscribing to frmProduction's *early* event, reintroducing the half-copied-file race. (3) Threading: frmProduction lives on an STA dispatcher thread; sinks must stay strictly off it or `Fire*` dispatch (`NonBlockingUITask`-based) contends with upload work. |
| **D. Merge into ToolManager** | **Structural — the mechanism behind "wrong data path"** | ToolManager doesn't see scan results; the only wafer-results signal in its world is what frmProduction fires on the COM bus — the *early* event. Feeding ToolGateway's data needs (stable paths, upload payloads) into ToolManager requires either extending frmProduction's `Fire*` bridge into a data conduit (a fifth responsibility on an already-flagged God-broker, on the wrong-timing bus) or building a new AOI→ToolManager channel duplicating the gRPC path. Either way frmProduction grows and the early/late race returns. |
| **R2. Camtek.ToolHost (Phases 0–2)** | **None** | Phases 0–2 only re-register services living outside the AOI. |
| **R2 Phase 3 / standalone service** | **None to frmProduction; one edit adjacent to it** | The single AOI-side change is deleting `EnsureToolGatewayRunning` from `clsInitAOI.cs` — adjacent to, but independent of, the frmProduction startup calls. Boot-order nuance: ToolGateway now runs at boot, hours before frmProduction exists; nothing cares — `ToolApiPublisher` dials `localhost:5005` per call, ToolGateway idles until events arrive. The `FireWaferScanResultsAreReady` flow through frmProduction is byte-for-byte identical. |

### 5.3 Takeaway

The pattern matches the review's top finding (C1): the early-COM-event vs. late-stable-path distinction is the system's most important implicit rule. **Every acceptable option leaves frmProduction on its side of that line; the rejected option (D) is rejected largely *because* it would erase the line.**
