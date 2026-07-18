# 4 — Impact Analysis: Projects To Be Created / Modified / Retired

> Level: **repository projects**. Paths relative to `C:\CamtekGit\BIS\Sources` unless noted. Phase tags: `P0–P5` = fabric phases, `Waves 0–2 / Deferred` = the funded plan, lanes per [03-appendix-four-lanes.md](03-appendix-four-lanes.md).
> Verified line references come from the repo investigations; items marked *(census)* are finalized by the Wave-0 censuses.

---

## 4.1 NEW projects (created — affect nothing until referenced)

| Project | Contents | Lane/owner |
|---|---|---|
| `Messaging\Camtek.Messaging` (net48;net8.0) | Bus client: API, send queue, **journal-writer thread**, pump (split I/O), dedup, request/reply, storm control | A |
| `Messaging\Camtek.Messaging.Contracts` (+ per-subsystem later) | Envelope, topic registry (class + ACL), payload DTOs | A |
| `Messaging\Camtek.Messaging.Broker` (net8) | Broker: connection manager, class queues, retained class B, counters | A |
| `Messaging\Camtek.Messaging.TestKit` / `.Tests` / `.Tap` | 14 contract-assertion groups incl. composite faults + T-L load tests; fault injection; bus-tap CLI | A |
| `ToolHost\Camtek.ToolHost` (net8) | Supervisor service + health :5100 + endpoint manifest | infra |
| `ToolServices\Camtek.ToolServices.Host` + `Camtek.API.<Module>` per service | One SVC host (:5060) + contracts per service | B |
| GEM record-replay harness (test project under `ToolManagement\`) | Host-visible regression net | D |
| Native client `camtek_bus.dll` (later, machine-layer adoption) | Flat-C bus client | A (deferred) |

**Dependency approvals required (policy §0.4):** `System.Threading.Channels` for net48; broker embed library only if the P0 build-vs-embed decision selects embed.

## 4.2 MODIFIED projects — by track and phase

### Track B (bus) — fabric phases

| Phase | Project | Change | Blast radius |
|---|---|---|---|
| P1a | `apps\Falcon.Net\AOI_Main.csproj` | + `Camtek.Messaging` reference (binary drop, both bitnesses); publish calls at frmScanTab hooks (~:1888-1902, :10162); BusAdapter skeleton | Low — additive beside `IPublisher` |
| P1a | `Utilities\ToolGateway\ToolGateway.BL` + `.Endpoint` + `.Tests` | + `BusSource` (WAL-before-ack) + `CommandPublisher` (:5007); net7→net8; **4 spool fixes** (poison/outage split, overflow, periodic drain, backpressured restore) | Medium — gateway release; :5005 kept for dual-run |
| P1a | `apps\Falcon.Net\Classes\clsInitAOI.cs` | Remove `EnsureToolGatewayRunning` → `EnsureBusRunning`; non-blocking bus connect | Low, flag-guarded |
| P1b | `system\CamtekSystem` (`PubSub\ToolApi\*`, `PublisherFactory`) | Retire `ToolApiPublisher` + `toolapi.proto`; later MSMQ/RabbitMQ variants | Medium — shared assembly rebuild, no API break |
| P2 | `apps\Falcon.Net`: `Forms\frmScanTab.cs`, `Forms\frmProduction.cs`, `Modules\modWaferAlignment.cs`, `Forms\frmVerifyTab.cs`, `Cmm\CmmReceiverApiRequetsHandler.cs` | ~40 `Fire*` sites → publish; frmProduction wrappers → BusAdapter/UiMarshaller/ToolStateReactions (staged); dual-publish into FalconWrapper continues | **High file-count, mechanical** — the program's biggest AOI diff |
| P2 | EFEM/AutoLoader COM server *(project per census)* | `loader.events` publish shim | Low — additive |
| P2–P4 | `ToolManagement\FalconWrapper` (vcxproj) | **ZERO change (decided)** — bridge is AOI-side | none |
| P3 | `ToolManagement\ToolManager` (`Server\ToolEvents.cs` + transition lock) | 3-site dual-publish + **`stateSeq`** stamped in the commit lock | Small diff / **high semantic** — shadow-gated |
| P4 | `apps\Falcon.Net\CommonUtils\ComServerWrappers\ExternalControlCbUiWrapper.cs` | Full ~15-callback surface → in-proc BusAdapter dispatch + **compensation table** (never a bus publisher) | Med-High — FlaUI + record-replay gated |
| P4 | `ToolManagement\SecsGemObjects` (+ `SecsGemGui.Net`) | GEM bus shim (C#): commands + subscriptions + **degraded contract** | Medium — wire untouched |
| P5 *(optional, per customer)* | `SecsGemObjects\Clients\RemoteControllers\RemoteControl.cs`; ToolManager command intake | `tool.commands` path | **High — re-qual budgeted** |

### Track S (services)

| Change | Projects | Blast radius |
|---|---|---|
| Pilot (census winner; SystemLogger leading) | New `Camtek.API.<Pilot>` + ToolServices module; AOI seam site per census; **client policy mandatory** (deadline/breaker/fallback) | Low — proves the whole lane |
| CMM gateway proxy (Wave 2) | Gateway: gRPC forward :5007→:50055 (same contract); CMM reconfigured; :50055 verified localhost-only | Low-Med — closes the external surface early, no CMM contract change |
| Shared services (WafersDB, InspectionMng, Maintenance, Automation) | Same module pattern + **compatibility façades** for non-AOI consumers (GEM/TAC stack, native C++) | Medium each |
| Client hygiene | `Camtek.ADC` + CMM client: Grpc.Core → `Grpc.Net.Client` | Low, mechanical |
| JobProvider (late — disqualified as pilot) | `ToolManagement\JobProvider\*` + compatibility connector; every consumer retests (SecsGem clients, NetTAC, TAC.Net, ProductionGui, RobotUI, C++ `ProcessProgramManager.cpp:84-88`) | High coordination |
| CMM per-operation split *(deferred — CMM contract change)* | `apps\Falcon.Net\Cmm\CmmReceiverApiRequetsHandler.cs` ops split; deletes :50055 + the Grpc.Core **server** dependency | Medium — external counterpart |

### Track C (consolidation) — four touch points per module

| Module | Implementation project | AOI seam site | Census |
|---|---|---|---|
| RobotUI | `machine\RobotUIControls.NET` (already referenced: `AOI_Main.csproj:2170`) | `RobotUIEventHandlerWrapper.cs:29,1288` | ✅ PASSED — absorb (single-STA criterion; its own MachineSrv/EFEM/WafersDB/JobProvider COM refs move in) |
| WaferLoader | — | — | ❌ FAILED (GEM/TAC + native C++ consumers) → lane D |
| BufferStationManager | — | — | ❌ FAILED (`BufferStationClient.cs:59`) → lane D |
| WaferLevelCassette / BSI / EBI / FRT / BsiHR helpers / CamtekUtils / WaferMapServer | per census | wrapper seam sites | pending (Wave 0) |

Per module also: `AOI_Main.csproj` (direct reference), installer/DeployUI (exe + ROT retirement — after the retention window).

### Track D / instruments

| Change | Projects |
|---|---|
| Wrapper call-frequency telemetry (additive logging, feeds every gate) | `apps\Falcon.Net\CommonUtils\ComServerWrappers\*` |
| Live-bug work items (independent of the program) | `system\CamtekSystem\AsyncTask\NonBlockingUITask.cs` (`.TotalMilliseconds` fix); `Utilities\ToolGateway\...\FleetMainServerClientImpl.cs` (ToolId string contract); `ToolGateway.BL\EventMessages\FailedMessagesHandler.cs` + `Sinks\SinkDispatcher.cs` (spool loop/overflow/drain); `PubSub\ToolApi\ToolApiPublisher.cs` (stale exe name) |

## 4.3 RETIRED (phase-tagged — deletions, with rollback retention)

| Retired | Phase | Replaced by |
|---|---|---|
| `ToolApiPublisher` + `toolapi.proto` + port :5005 (+ firewall rule) | P1b | Bus subscription |
| `EnsureToolGatewayRunning` | P1a | ToolHost supervision |
| `IFalconFireEvents` fan-out via FalconWrapper | P2–P4 per subscriber (façade possibly permanent — customer contract) | `scan.*` topics |
| `IToolManagerCB` fan-out | P3 | `tool.state` (retained) |
| `IFalconExternalControlCB` full surface | P4 | In-proc dispatch + compensations |
| `IProductionManagerCB` / `ICarrierExecuterCB` | **P5 only — optional** | `tool.commands` / `production.carrier` |
| ~5–7 singleton exes + their ROT registrations | Track C, one per release | In-proc modules |
| `:50055` listener + Grpc.Core server dep | Only at the CMM split (deferred) | Gateway proxy → per-op split |
| 2 Windows services (`Camtek.DataServer` registration, `Camtek.RMSToolService`) + FAR supervisor service | ToolHost waves | ToolHost children (3 services → 1) |

## 4.4 Build / deploy / infrastructure

| Item | Impact |
|---|---|
| `BIS\build\Falcon_2022.sln` | + Messaging net48 projects; ToolHost/Broker/ToolServices in their own net8 solutions |
| Binary drops `c:\bis\bin` + `c:\bis\bin\x64` | + `Camtek.Messaging.dll` (both bitnesses; `TreatWarningsAsErrors` applies) |
| `DeployUI` (`DeployUI2.ps1`, `msbuild.actions.ps1`, `stopall.ps1`) | Publish steps for ToolHost/broker/gateway/ToolServices; stop/start via the ToolHost API instead of process-name kills; per-singleton handling removed as Track C lands |
| Installers (WiX / RMS installer) | ToolHost `ServiceInstall`; DataServer/RMS re-registration as children; absorbed-module entries removed |
| `Install\Scripts\ReservePorts.bat` + firewall | + :5060, :5007; − :5005; :50055 localhost-only then removed. Bus needs **no ports** |
| Config | New: endpoint manifest (ToolHost-owned, hash → fleet fingerprint), `toolbus.json`, children config, ≤5 signed fleet profiles. Journals/spool on the system volume with quotas — separate from tile/zip data |
| CI (`xbuild\*.yml`) | TestKit + contract suites into PR pipelines; the composite-fault and T-L load scenarios as P0 gates |
| `Packages\` (local NuGet) | + `System.Threading.Channels` (needs §0.4 approval) |

**Reading the map:** the heaviest single diff is P2's mechanical `Fire*` sweep in `apps\Falcon.Net`; the highest-risk *small* diffs are P3 (`ToolEvents.cs` + `stateSeq`, state-machine semantics) and P5 (`RemoteControl.cs`, re-qual). Everything else is additive new projects or seam-guarded swaps with flag rollback inside a defined retention window.
