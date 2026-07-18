# 4 — Impact Analysis: Projects To Be Created / Modified / Retired

> Level: **repository projects**. Paths relative to `C:\CamtekGit\BIS\Sources` unless noted. Phase tags: `P0–P5` = fabric phases, `Waves 0–2 / Deferred` = the funded plan.
> Up-links: system context → [01-system-architecture.md](01-system-architecture.md) · migration lanes → [03-appendix-four-lanes.md](03-appendix-four-lanes.md).
> Down-link: program plan → [05-roadmap-and-risks.md](05-roadmap-and-risks.md).
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
| **Wave 0** | `Utilities\ToolGateway\ToolGateway.BL` (spool) | **4 spool fixes** (poison/outage split, overflow, periodic drain, backpressured restore) = the LB1/LB5 live-bug fixes — ship in **Wave 0**, ahead of P1a (see §5.2 and Track D) | Medium — gateway release |
| P1a | `Utilities\ToolGateway\ToolGateway.BL` + `.Endpoint` + `.Tests` | + `BusSource` (WAL-before-ack) + `CommandPublisher` (:5007); net7→net8. **:5007 default-deny authz is a P1a exit criterion** (security work-stream, [05 §5.6](05-roadmap-and-risks.md)) | Medium — gateway release; :5005 kept for dual-run |
| P1a | `apps\Falcon.Net\Classes\clsInitAOI.cs` | Remove `EnsureToolGatewayRunning` → `EnsureBusRunning`; non-blocking bus connect | Low, flag-guarded |
| P1b | `system\CamtekSystem` (`PubSub\ToolApi\*`, `PublisherFactory`) | Retire `ToolApiPublisher` + `toolapi.proto`; later MSMQ/RabbitMQ variants | Medium — shared assembly rebuild, no API break |
| P2 | `apps\Falcon.Net`: `Forms\frmScanTab.cs` (~50 sites), `Forms\frmProduction.cs`, `Forms\frmMain.cs`, `Modules\modWaferAlignment.cs`, `Forms\frmVerifyTab.cs`, `Cmm\CmmReceiverApiRequetsHandler.cs`, `LoginController.cs`, `Classes\clsInitAOI.cs`, `clsMultiRecipe.cs`, `ExternalCoordSystemsAlign.cs`, `clsCalibrationManager.cs`, `MainContextModule.cs` | **~80 `Fire*` call sites across 12 files** (verified count — ~2× the earlier "~40" estimate); frmProduction wrappers → BusAdapter/UiMarshaller/ToolStateReactions (staged); dual-publish into FalconWrapper continues | **High file-count, mechanical** — the program's biggest AOI diff; **effort re-priced ~2×** |
| P2 | EFEM/AutoLoader COM server *(project per census)* | `loader.events` publish shim | Low — additive |
| P2–P4 | `ToolManagement\FalconWrapper` (vcxproj) | **ZERO change (decided)** — bridge is AOI-side | none |
| P3 | `ToolManagement\ToolManager` (`Server\ToolEvents.cs`, `ToolManager.cs` transition path) | **Introduce a transition-serialization lock (does NOT exist today** — FEA-1/R-8); dual-publish + **`stateSeq`** stamped inside it, covering **all** state writers (frmProduction.CheckState, BufferStation ToolManagementAdapter, ProductionGui frmProductionGuiBL, ProductionManager internal — not 3 sites) | **Not a small diff** — introduces locking into the state machine + deadlock audit of the sync CB fan-out; **high semantic**, shadow-gated |
| P4 | `apps\Falcon.Net\CommonUtils\ComServerWrappers\ExternalControlCbUiWrapper.cs` | Full **~18–21-callback** surface → in-proc BusAdapter dispatch + **compensation table** (never a bus publisher; only `GuiStartManualScan`/`GuiExportMap` were ever forwarded to frmProduction) | Med-High — FlaUI + record-replay gated |
| P4 | `ToolManagement\SecsGemObjects` (+ `SecsGemGui.Net`) | GEM bus shim (C#): commands + subscriptions + **degraded contract** | Medium — wire untouched |
| P5 *(optional, per customer)* | `SecsGemObjects\Clients\RemoteControllers\RemoteControl.cs`; ToolManager command intake | `tool.commands` path | **High — re-qual budgeted** |

### Track S (services)

| Change | Projects | Blast radius |
|---|---|---|
| Pilot (census winner; SystemLogger leading) | New `Camtek.API.<Pilot>` + ToolServices module; AOI seam site per census; **client policy mandatory** (deadline/breaker/fallback) | Low — proves the whole lane |
| CMM gateway proxy (Wave 2) | Gateway: gRPC forward :5007→:50055 (same contract); CMM reconfigured; :50055 confirmed localhost-only | Low-Med — **EOL-Grpc.Core-runtime containment** (not external-surface closure — :50055 is already loopback-bound; the real external door is :5005, retired at P1b), no CMM contract change |
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

> **Rollback-class rule (R-OPS-1):** the moment code is retired, its rollback class **degrades from *flag* to *reinstall*** — the fleet dashboard and the site playbook must both reflect that. A retirement ships **only after the dashboard confirms 100 % of fleet tools (including gateway-disabled and offline tools, via the field-service fingerprint bundle) have run the new path for a full release**. Critically, at **P1b the gateway keeps its `:5005` listener one release longer than any AOI-side `ToolApiPublisher` still exists in the fleet** — otherwise a rolled-back R+1 AOI hitting an R+2 gateway with no `:5005` re-triggers LB2 (scan-thread block). LB2 is therefore fixed in **Wave 0** even though the code retires at P1b — it *is* the rollback path.

| Retired | Phase | Rollback class after retirement | Replaced by |
|---|---|---|---|
| `ToolApiPublisher` + `toolapi.proto` + port :5005 (+ firewall rule) | P1b | *reinstall* (gateway keeps :5005 one release past the last AOI publisher) | Bus subscription |
| `EnsureToolGatewayRunning` | P1a | *flag* | ToolHost supervision |
| `IFalconFireEvents` fan-out via FalconWrapper | P2–P4 per subscriber (façade possibly permanent — customer contract) | *flag* per subscriber | `scan.*` topics |
| `IToolManagerCB` fan-out | P3 | *flag* until retention closes, then *reinstall* | `tool.state` (retained) |
| `IFalconExternalControlCB` full surface | P4 | *flag* | In-proc dispatch + compensations |
| `IProductionManagerCB` / `ICarrierExecuterCB` | **P5 only — optional** | *reinstall* (re-qual) | `tool.commands` / `production.carrier` |
| ~5–7 singleton exes + their ROT registrations | Track C, one per release | *reinstall* | In-proc modules |
| `:50055` listener + Grpc.Core server dep | Only at the CMM split (deferred) | *reinstall* | Gateway proxy → per-op split |
| 2 Windows services (`Camtek.DataServer` registration, `Camtek.RMSToolService`) + FAR supervisor service | ToolHost waves | *reinstall* | ToolHost children (3 services → 1) |

## 4.4 Build / deploy / infrastructure

| Item | Impact |
|---|---|
| `BIS\build\Falcon_2022.sln` | + Messaging net48 projects; ToolHost/Broker/ToolServices in their own solutions. **Runtime target: .NET 10 LTS** for the net8-era components (ToolHost/broker/gateway/ToolServices) — .NET 8 is EOL 2026-11, mid-rollout; do not repeat the net7 mistake (R-OPS-6) |
| Binary drops `c:\bis\bin` + `c:\bis\bin\x64` | + `Camtek.Messaging.dll` (both bitnesses; `TreatWarningsAsErrors` applies). **Runtime servicing on air-gapped fabs: self-contained publish** for the .NET-10 components so a runtime patch ships inside the Camtek installer (no Windows Update / nuget on tool PCs) (R-OPS-6) |
| `DeployUI` (`DeployUI2.ps1`, `msbuild.actions.ps1`, `stopall.ps1`) | Publish steps for ToolHost/broker/gateway/ToolServices; **`sc stop Camtek.ToolHost` (graceful child drain, R-OPS-3) with failure-actions disabled for the deploy window**, not process-name kills; ToolHost-self-update sequence documented; per-singleton handling removed as Track C lands. **One-time spool migrator** (R-OPS-3): the new gateway, on first start, reads the old-format `FailedMessages-*.txt` + `.overflow.txt`, re-emits through the new WAL, archives the originals — so an in-flight outage backlog is never stranded at upgrade |
| Installers (WiX / RMS installer) | ToolHost `ServiceInstall`; DataServer/RMS re-registration as children; absorbed-module entries removed. **Config-dir ACL** (write = ToolHost service account only) + **signed-manifest verification** (R-7/§6.8.4) |
| `Install\Scripts\ReservePorts.bat` + firewall | **Firewall rules for :5007 inbound + :5060; ReservePorts touched only for the :50055 retirement** (5007/5060/5100 sit below the dynamic range — the earlier "ReservePorts +:5060,:5007" was a no-op). − :5005; :50055 localhost-only then removed. Bus needs **no ports** |
| Config | New: endpoint manifest (ToolHost-owned, **signed + verified**, hash → fleet fingerprint), `toolbus.json`, children config, ≤5 signed fleet profiles **retained on disk for last-known-good boot** (R-OPS-2). Journals/spool on the system volume with quotas + **at-rest ACLs** (R-7/§6.8.5) — separate from tile/zip data |
| CI (`xbuild\*.yml`) | **TestKit runs on net48 × both bitnesses** (the build AOI actually loads — not net8-only, R-TS-1) + net8/10. **Tier table (R-TS-3):** PR = fast assertions 1–4,6–11 both TFMs (<10 min); nightly = 5,12,13 + T-L2/3/5; release/P0 = T-L1/4/6 + FlaUI + record-replay. Wave-0 exit criterion: the pipeline exists and blocks a deliberately-broken build (today it runs zero tests) |
| Test instruments (R-TS-2) | **GEM record-replay harness** (built + mutation-qualified: N seeded byte/order mutations → 100 % flagged) and the **shadow comparator** (qualified by an injected-divergence suite, measured false-positive rate) are named Wave-0 deliverables with acceptance criteria — without them the per-edge gate is unenforceable |
| `Packages\` (local NuGet) | + `System.Threading.Channels` (needs §0.4 approval) |

**Reading the map:** the heaviest single diff is P2's mechanical `Fire*` sweep in `apps\Falcon.Net`; the highest-risk *small* diffs are P3 (`ToolEvents.cs` + `stateSeq`, state-machine semantics) and P5 (`RemoteControl.cs`, re-qual). Everything else is additive new projects or seam-guarded swaps with flag rollback inside a defined retention window.
