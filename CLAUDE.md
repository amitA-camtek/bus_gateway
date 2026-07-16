# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this directory is

`c:\Users\amita\Desktop\camtek` is an **architecture reference workspace** for the Camtek Falcon AOI project. It contains detailed design documents, not source code. The actual monorepo is at `C:\CamtekGit\` — start there for any code changes.

---

## Reference documents here

| File | What it covers |
|------|---------------|
| [frmProduction.md](frmProduction.md) | Deep reference for `frmProduction` — the invisible GUI-side COM controller in Falcon.Net. Covers its four COM wrappers, the 25 `Fire*` event bridge methods, all callers of `ChangeToolState`, and how ToolGateway (gRPC) connects via `frmScanTab` (not through `frmProduction`). |
| [falcon-aoi-architecture-reference.md](falcon-aoi-architecture-reference.md) | Full system block diagram: Falcon AOI ↔ factory host (SECS/GEM), ↔ ToolGateway (gRPC :5005), ↔ Fleet.Main and TSMC cloud. Includes the Enter-Production sequence and tool state machine. |
| [frmProduction-architecture.svg](frmProduction-architecture.svg) | Rendered architecture diagram (opens in any browser). Mermaid source is in `.mmd`. |

---

## Real code — where to look

All source lives under `C:\CamtekGit\BIS\Sources\`. Key files referenced by the documents here:

| Concern | Path (relative to `BIS\Sources\`) |
|---------|----------------------------------|
| `frmProduction` controller | `apps\Falcon.Net\Forms\frmProduction.cs` |
| ToolManager COM singleton | `ToolManagement\ToolManager\Server\ToolManager.cs` |
| ProductionManager engine | `ToolManagement\ToolManager\ProductionManager\ProductionManager.cs` |
| COM event bus | `ToolManagement\ToolManager\Server\ToolEvents.cs` |
| ToolManagerUiWrapper | `apps\Falcon.Net\CommonUtils\ComServerWrappers\ToolManagerUiWrapper.cs` |
| ExternalControlCbUiWrapper | `apps\Falcon.Net\CommonUtils\ComServerWrappers\ExternalControlCbUiWrapper.cs` |
| Start-Production trigger | `apps\Falcon.Net\Forms\frmJobTab.cs:941–982` |
| ToolGateway hook points | `apps\Falcon.Net\Forms\frmScanTab.cs` ~:1888–1902 and :10162 (`OnReportScanResults` itself sits at :7284–7330 and contains no push) |
| gRPC publisher | `system\CamtekSystem\PubSub\ToolApi\ToolApiPublisher.cs:88` |
| SECS/GEM host mapping (live) | `ToolManagement\SecsGemObjects\Clients\RemoteControllers\RemoteControl.cs` — C# logic over native Cimetrix `SECSGemDriver`. (`SecsGemClient\E30RemoteControl.cpp:46` is legacy — not in `Falcon_2022.sln`) |
| COM event hub host | `ToolManagement\FalconWrapper\` — `FalconWrapper.exe` (out-of-proc ATL EXE hosting `CFalconEvents`/ScanManager/AutoCycleManager) |
| TSMC bridge | `apps\Falcon.Net\Modules\Tsmc\ScanResultsReady.cs` (+ `ScanComplete.cs`) |

The broader monorepo CLAUDE.md (build commands, C# standards, git conventions) is at `C:\CamtekGit\CLAUDE.md`.

---

## Key non-obvious facts (from the reference docs)

- **`frmProduction` is invisible.** Despite the `frm` prefix it has no UI — it is a VB6→C# port using the "form-as-code-module + COM host" pattern. Its designer contains only a `ToolTip`. Access it via `MainContext.Instance.Forms.frmProduction`.
- **ToolGateway bypasses `frmProduction`.** The gRPC call to ToolGateway (port 5005) originates in `frmScanTab` (~:1888–1902, :10162) — *after* results are copied to their stable path. `frmProduction.FireWaferScanResultsAreReady` fires earlier on the COM event bus (SECS/GEM etc., hosted in `FalconWrapper.exe`) and ToolGateway does not subscribe to that bus. Caution: the push is *not* reliably fire-and-forget — `ToolApiPublisher` can block the scan thread when the gateway is down (no gRPC deadline, `Thread.Sleep(1000)` + process spawn per publish attempt).
- **`ChangeToolState` has exactly three callers:** `clsInitAOI.cs` (Engineering on startup) and `frmJobTab.cs` (Production on operator Start Production — two code paths, lines 948 and 981).
- **COM event direction is two-way.** `frmProduction` pushes ~25 `Fire*` events *down* into `mFalconFireEvents` (ScanManager/AutoCycleManager), while COM callbacks from ToolManager fire *up* via `OnToolStateChanged`. These are different wrappers and different interfaces.
- **`ExternalControlCbUiWrapper` only forwards two commands to `frmProduction`:** `GuiStartManualScan` and `GuiExportMap`. All other factory-host commands are handled inside the wrapper itself.
- **Tool state enum values:** `NotInitialized → Initialization → Engineering ↔ EngineeringToProduction → Production`. Failure during `EngineeringToProduction` reverts to `Engineering`.
