# AOI_Main.exe — Communication Analysis & Recommended Alternative

**Target:** `AOI_Main.exe` — the main AOI application, built from **`BIS\Sources\apps\Falcon.Net`** (`AssemblyProduct("AOI_Main.exe")`, .NET Framework 4.8, WinForms/WPF UI + orchestration master).
**Scope:** communication of AOI_Main only. The **gRPC Fleet/ToolAPI link is excluded** per instruction (it exists — the `ToolApiPublisher` variant of the PubSub `Publish` call sites in §1.6 — and is noted only here).
**Date:** 2026-07-17 · **Companions:** `com-bus-replacement-design.md` (alternatives A–G full designs), `com-bus-comparison-and-risks.md` (system-wide comparison & risks).

> Disambiguation: `BIS\Sources\TestAutomationAPI\AOI_Main` is a *different*, small test-automation library that drives AOI_Main.exe from outside (FlaUI UI-automation + FalconWrapper). It appears below only as an inbound counterpart.

---

## 1. Communication inventory

AOI_Main is the **hub process** of the tool. Its dominant pattern: **bidirectional out-of-process COM** — AOI_Main calls a server object and registers a callback sink (`*CB`/`*Callback`) that the server invokes back. Roughly 21 out-of-proc COM counterparts, 2 gRPC links (excluding Fleet), 2 shared-memory links, and several file-level channels.

### 1.1 Out-of-proc COM — native ATL servers (6)

Reached via `*PS.Interop` proxy/stubs, `CLSCTX_LOCAL_SERVER`; wrappers under `Falcon.Net\CommonUtils\ComServerWrappers\`.

| Counterpart process | Dir | Key interfaces | Purpose | Evidence |
|---|---|---|---|---|
| **MachineSrv.exe** | ↔ | `MachineConnector`→`MachineObj`, `IMachineCallback`; sub-objects `IXYTableBase`/`IXYTableCB`, `IChuckNavigatorCallback` | Master motion/HW control, stage, chuck | `MachineWrapper.cs:38-59`, `XYTableWrapper.cs:15-36`, `ChuckNavigatorWrapper.cs:9` |
| **EfemSrv.exe** | ↔ | `IAutoLoader`/`IAutoLoaderCB`; `IDoorCB` (EfemSecsInterfaces) | EFEM robot, carrier mapping, wafer moves, doors | `AutoLoaderUIWrapper.cs:10-70`, `DoorWrapper.cs:8` |
| **ScenarioManager.exe** | ↔ | `IScenarioManagerObj`, `CScanManager`/`IScanManagerInkingCB`, `CSetupManager` | Scan orchestration, setup, inking, **DDS-node ("pizza") status** | `MainContextModule.cs:303,1116,2596`, `ScanManagerWrapper.cs:13-37` |
| **FalconWrapper.exe** | ↔ | `IFalconExternalControl`; **inbound** `IFalconExternalControlCB`; `IFalconFireEvents` | External automation control of the UI (load job, start scan, sorting) | `ExternalControlCbUiWrapper.cs:25-78,284` |
| **SecsGemDriver** | ↔ | `CSECSGemConnector`→`IS12` | SECS-II wafer-map download/upload (S12), bin codes | `Cmm\CmmReceiverApiRequetsHandler.cs:325-333` |
| **WaferMapServer.exe** | → | `IWaferDisplayServerWrapper`, `IWaferMapDisplayServerWrapper` | Wafer-map display server hosting | `Utils\WaferMapConnector.cs:13-35` |

### 1.2 Out-of-proc COM — .NET ROT singletons (~15)

Pattern verified end-to-end: connector assembly loads in-proc, but `Connect()` returns a **COM proxy to the out-of-proc singleton** via `SingletonUtils.GetSingleton` (ROT + `ComSingletonHolder.exe`).

| Counterpart singleton | Dir | Entry point in AOI_Main | Purpose |
|---|---|---|---|
| CamtekUtils | → | `MainContextModule.cs:110` | Shared utilities |
| InspectionMng | → | `MainContextModule.cs:142-159` (`GetInspectionMng`, `GetSPCDB`) | Inspection/result mgmt, SPC DB |
| WafersDatabase | → | `MainContextModule.cs:1382-1383` | Wafers DB access |
| MaintenanceManager | ↔ | `clsCalibrationManager.cs:44` + `IModuleMaintenanceManagerCB` | Maintenance/calibration tasks |
| ToolManager (TAC) | ↔ | `ToolManagerUiWrapper.cs:27-28` | Tool state, SECS/GEM host command plumbing |
| JobProvider (SDR) | → | `frmJobTab.cs:1416,3192-3208` | Job storage/download/upload |
| SystemLogger | → | `frmJobTab.cs:2875,3109` | Context + config-change logging |
| WaferHandlingManager / AutomationManager | → | `Modules\Automation.cs:31-111` | Automation cycle control |
| BufferStationManager | ↔ | `BufferStationManagerUIWrapper.cs:28-35` | Buffer-station handling |
| WaferLevelCassetteManager | ↔ | `WaferLevelManagerUIWrapper.cs:34-41` | Wafer-level cassette mgmt |
| WaferLoader | ↔ | `WaferLoaderUIWrapper.cs:12,42-48` | Wafer loader UI/control |
| RobotUI | ↔ | `RobotUIEventHandlerWrapper.cs:29,1288-1297` (`IRobotUIConnectorCB`) | Robot/sorting UI + rich callbacks |
| BsiModule | ↔ | `Modules\BSIModuleHelper.cs:15-16` | BSI inspection module |
| EbiModule | → | `Modules\EBIModuleHelper.cs:9` | EBI inspection module |
| FrtModule / BsiHR module | → | csproj refs + SmartUI view-models (*pattern inferred, not line-verified*) | FRT / BSI-HR modules |

### 1.3 gRPC (excluding Fleet) — 2 links, both already live in this process

| Counterpart | Dir | Mechanism | Purpose | Evidence |
|---|---|---|---|---|
| **ADC inference service** (`localhost:5000`, configurable) | → | Grpc.Core client (`inference.proto`, `train.proto`) | Defect-classification inference | `clsDefectGrab.cs:88-94,284`; `Camtek.ADC\Definitions.cs:16` |
| **CMM service** | ↔ | Grpc.Core client (`ICmmClient`, cmm* protos) **+ AOI_Main hosts a gRPC server** (`CmmReceiverServer`, `localhost:50055`) | Wafer-map import/export; CMM calls back into AOI_Main (fetch SECS-II maps, operation start/complete) | `Cmm\clsCMM.cs:33-101`; `CmmReceiverApiRequetsHandler.cs:176-328` |

**Load-bearing observation:** AOI_Main already runs Grpc.Core **as both client and server** on .NET Fx 4.8, in production paths — the runtime, vendored binaries, and coding patterns are already inside this exact process.

### 1.4 Shared memory (MMF) — 2 links

| Counterpart | Dir | Mechanism | Evidence |
|---|---|---|---|
| STIL/FSI scanner buffer | ← | `MemoryMappedFile.OpenExisting("Still_data_Buffer")` | `Utils\MMF.cs:82,103-104` |
| DDS result tile pool | ↔ | `TilePool.TilePoolUtils().ReleaseTiles(...)` (MMF/file-backed tiles) | `frmScanTab.cs:1087`, `frmJobTab.cs:833,3307` |

### 1.5 Grabbing — in-proc grab libraries with COM-style callbacks

`IGrabberManager`/`IGrabberManagerCB` (`GrabbedFrame`, `GrabbingStatus`) via Clip/Clip2/CSP/Cts/TDI GrabObjects for defect re-grab and live camera (`clsDefectGrab.cs:19,73-84,316-326`; `CameraWrapper.cs:13-35`). The grabber hardware/processes are reached by those libraries over custom TCP+MMF; from AOI_Main's perspective this is an in-proc library boundary with callbacks.

### 1.6 PubSub — publish-only

`IPublisher.Publish(EventMessage)` for tool/scan lifecycle events (`clsInitAOI.cs:375`, `frmMain.cs:746`, `frmScanTab.cs:1825`). Transport is config-driven via `System.ini [PubSubEvents]` (RabbitMQ / MSMQ / InProc / ToolAPI-gRPC — the last is the **excluded Fleet link**, same call sites). **No Subscribe found** — AOI_Main does not consume the bus.

### 1.7 Database & files

- **Access/Jet `.mdb` via DAO COM interop** (`DBEngine.OpenDatabase`): FalconLog.mdb, spc.mdb (`frmScanTab.cs:126,8874-9400`, `MainContext\SPCDB.cs:92-95`). No direct Oracle/SQL client found — relational access goes through the WafersDatabase/InspectionMng singletons.
- **Shared INI / job-file coordination** with other processes: `ProductionInfo.ini`, `JobStorage.ini`, `GlobalRTP.ini`, `System.ini`, job/recipe tree under `c:\job` (`clsMultiRecipe.cs:1970`, `frmJobTab.cs:1416`, `clsDefectGrab.cs:88`).

### 1.8 Inbound links (who calls INTO AOI_Main)

| Caller | Mechanism |
|---|---|
| FalconWrapper.exe (external automation) | COM callback sink `IFalconExternalControlCB` registered by AOI_Main |
| Every server in §1.1/§1.2 | COM callbacks into registered `*CB` sinks (`IMachineCallback`, `IAutoLoaderCB`, `IDoorCB`, `IXYTableCB`, `IScanManagerInkingCB`, `IRobotUIConnectorCB`, `IModuleMaintenanceManagerCB`, `IGrabberManagerCB`, …) |
| CMM service | **inbound gRPC** to AOI_Main's hosted `CmmReceiverServer` (`localhost:50055`) |
| TestAutomationAPI (`TestAutomationAPI\AOI_Main`) | FlaUI **UI Automation** + `Process.Start("AOI_Main.exe")` + FalconWrapper.NET |
| Fab host (SECS/GEM) | terminates in SecsGemDriver/ToolManager processes; reaches AOI_Main via the ToolManager singleton, not directly |

### 1.9 Counts & flags

**Counterparts per mechanism:** native COM servers 6 · .NET COM singletons ~15 · gRPC 2 (excl. Fleet) · MMF 2 · in-proc grab-lib boundary 1 · PubSub publish 1 · DAO .mdb 1 · shared INI/files 1 · UI-automation inbound 1.

**Flags (verified as unknown, not guessed):** CMM's exact server identity (transport/role evidenced; not asserted to be MDC/DataServer). The STIL `"Still_data_Buffer"` MMF producer (FsiSrv.exe vs StilScanner COM) is unresolved. `IWaferNavigatorCB`/`IGuiServiceCallback` providers are likely in-proc services, not separate processes. Notably, AOI_Main does **not** directly consume SafetyConnector, IOManagerConnector, ImageProcConnector, EtelDriverConnector, or the DDS Remoting endpoints — hardware and farm control are delegated to MachineSrv and ScenarioManager; AOI_Main touches farm *results* only via TilePool MMF.

---

## 2. What AOI_Main's communication profile actually demands

1. **Duplex is the defining requirement.** Virtually every out-of-proc link is bidirectional: command out, callback sink in. ~21 counterparts × callback interfaces. Any replacement that treats server→client push as an afterthought fails this process.
2. **UI/orchestration cadence, not control loops.** AOI_Main issues human-speed commands and consumes event streams; tight motion loops live inside MachineSrv/EfemSrv, behind one COM hop. Latency budget per call: milliseconds are tolerable, though property-get chattiness must be audited.
3. **Almost everything is .NET↔.NET.** ~15 of ~21 COM counterparts are .NET singletons; only 6 are native ATL servers.
4. **Bulk data already bypasses the control plane** (TilePool/STIL MMF) and must continue to.
5. **The process already speaks gRPC in production** — client (ADC, CMM) *and* hosted server (`:50055`) on .NET Fx 4.8 with vendored Grpc.Core. A second RPC dialect would be a *third* protocol in this process (COM + gRPC today).
6. **External contract surfaces exist**: FalconWrapper (customer automation) and TestAutomationAPI (FlaUI) — must keep working through any migration.

---

## 3. Evaluation — the seven alternatives against AOI_Main's profile

Criteria per the review request: communication-pattern fit, maintainability, scalability, complexity (5 = least added), future extensibility. Scores are specific to AOI_Main, not the system-wide scores of the companion documents.

| Criterion | A: gRPC mesh | B: RabbitMQ | C: ZeroMQ | D: Consolidation | E: State fabric | F: Modernized COM | G: JSON-RPC/pipes |
|---|---|---|---|---|---|---|---|
| Communication-pattern fit (duplex callbacks, hub topology) | 4 — streams invert callbacks but proven here | 2 — sync command/reply UI flow fights broker RPC | 3 — patterns exist, all hand-built | 4 — callbacks become plain .NET events for absorbed singletons | 3 — fits the event/state half only | 5 — it *is* the current pattern | 5 — duplex notifications map 1:1 to CB sinks |
| Maintainability | 4 — schema-first contracts, house standard | 3 — broker + two contract languages | 2 — own framework forever | 5 — plain DI, fewer processes | 3 — new paradigm to maintain | 2 — COM expertise dependency persists | 4 — interfaces stay; JSON discipline needed |
| Scalability (more modules, more nodes, off-box growth) | 5 — same contract intra- & inter-machine; AOI is a multi-PC tool | 4 — brokers scale events well | 3 | 2 — one process scales down, not out | 4 — projections scale readers well | 1 — locks Windows/local | 3 — pipes are local-only; off-box needs a second transport |
| Complexity added (5 = least) | 3 — proto extraction for ~21 links, deprecated-runtime bridge | 2 — broker ops on tools | 2 — framework build | 4 — threading audit per absorbed service | 2 — state schemas + producers change | 5 — none | 4 — smallest full-IPC foundation |
| Future extensibility (new consumers, non-Windows, AI services) | 5 — ADC/CMM/DataServer path already gRPC | 3 | 2 | 3 — helps .NET migration, not reach | 4 — replay/simulation compound | 1 | 3 — .NET-centric dialect |
| **Total** | **21** | **14** | **12** | **18** | **16** | **14** | **19** |

---

## 4. Recommendation for AOI_Main

**Most suitable alternative: A — the gRPC service mesh — as AOI_Main's target communication architecture**, applied with D as the preparatory step, for these AOI_Main-specific reasons:

1. **The decisive fact: gRPC is already inside this exact process, both directions, in production.** AOI_Main ships Grpc.Core as a client (ADC inference, CMM) and as a hosted server (`CmmReceiverServer` on `localhost:50055`) on .NET Fx 4.8 with vendored binaries (§1.3). The runtime risk, vendoring question, and team-familiarity question that made the generic system-wide choice a bake-off (A vs G) are already answered *for this process*. Adding StreamJsonRpc here would introduce a third protocol dialect into AOI_Main; converging on gRPC reduces it toward one.
2. **Scalability and extensibility dominate for the hub.** AOI_Main is the process most likely to gain new counterparts (AI/ADC services, DataServer, off-box services on the multi-PC tool). gRPC's contracts work identically intra-machine and cross-machine; named-pipe JSON-RPC would stop at the machine boundary (§3, scalability row).
3. **The callback-heavy pattern is provable on gRPC here** — the CMM link already implements the "counterpart calls back into AOI_Main" shape as an inbound gRPC service, today.
4. **Its known weaknesses are mitigated by the tiered application below** — latency-sensitive links stay behind MachineSrv's COM boundary until those servers are rewritten; chatty interfaces get the call-frequency audit + batching from the companion design (§2.A).

**How to apply it to AOI_Main's actual links (tiered):**

| AOI_Main links | Action |
|---|---|
| .NET singletons where AOI_Main is the sole consumer (candidates: RobotUI, WaferLoader, BufferStation, WaferLevel, module UI helpers — confirm via triage census) | **D first — consolidate in-proc.** Callbacks become plain .NET events; zero wire, zero contracts. Fastest wins, fewer processes |
| Shared .NET singletons (ToolManager, JobProvider, SystemLogger, WafersDB, InspectionMng, AutomationManager, MaintenanceManager, module wrappers with other consumers) | **A — migrate to gRPC services** behind the existing `Connect()` seam; events as server-streams; handle/lease for stateful ones |
| Native servers (MachineSrv, EfemSrv, ScenarioManager, SecsGemDriver, WaferMapServer) | **F now, A later** — supervised-COM life-support; gRPC sidecar/rewrite per the companion design when each server's .NET migration lands |
| FalconWrapper (external customer contract) | **Freeze as permanent COM façade** until contractually renegotiated (risk M11) |
| TilePool / STIL MMF, grabbing libraries | **Keep** — data plane stays on shared memory by design |
| PubSub publish sites | **Keep** — already transport-pluggable; Fleet excluded per scope |
| DAO `.mdb` logging (FalconLog/spc) | Out of bus scope; flag as a separate modernization candidate (DataServer/MDC path) |

**Suggested first steps for AOI_Main specifically:** (1) run the triage census over the ~15 singleton links to split sole-consumer (→ D) from shared (→ A); (2) pilot A on **JobProvider or SystemLogger** (simple semantics, no motion risk, high fan-in) reusing the existing in-process Grpc.Core infrastructure; (3) instrument the COM wrappers in `CommonUtils\ComServerWrappers\` with call-frequency telemetry to build the latency-budget evidence before touching MachineSrv-facing links.

---

*Method: dedicated repo exploration of `BIS\Sources\apps\Falcon.Net` (project references, ComServerWrappers, connector call sites, gRPC/proto references, MMF/Remoting/pipe/WCF greps), cross-checked against the system-wide scans in the companion documents. All evidence paths are repo-relative and were located in source; items that could not be pinned are flagged in §1.9 rather than asserted.*