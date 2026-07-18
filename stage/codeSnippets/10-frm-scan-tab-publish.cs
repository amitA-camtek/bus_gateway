// Realizes: §2.6 Publisher integration (frmScanTab), §2.7 Startup & teardown (clsInitAOI)
// Files: apps\Falcon.Net\Forms\frmScanTab.cs (MODIFIED ~:1888-1902, :10162)
//        apps\Falcon.Net\Classes\clsInitAOI.cs (MODIFIED — EnsureBusRunning)
// Phase: P1a for scan.committed + tool.telemetry (the funded Wave-1 edge).
//        scan.announced is a P2 edge (review D-3/CON-5) — its publish is flag-guarded OFF at P1a
//        and enabled with the P2 Fire*-hub migration, not shipped early.
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Threading;
using Camtek.Messaging;
using Camtek.Messaging.Contracts;
using Falcon.Net.Bus;

namespace Falcon.Net.Forms
{
    // ═══ frmScanTab — publish hooks ══════════════════════════════════════════════════
    // §2.6: "the Fire* call sites and result hooks become one-line publishes through
    //        the BusAdapter façade; the ≤1 ms bound makes them scan-thread-safe by contract"

    public partial class frmScanTab
    {
        // Injected from MainContext. BusAdapter is the only publish point in AOI_Main.
        private BusAdapter _busAdapter;

        // ── Hook site 1: scan.announced — before CopyScanResults (~:1888) ────────
        // §1.4: "scan.announced carries NO file paths — structural safety rule"
        // §03-lanes Lane-A P1a: class C, best-effort

        private void PublishScanAnnounced(string waferId, string lotId, int slot, string correlationId)
        {
            // scan.announced is a P2 edge (D-3): guarded OFF at P1a so Wave-1's shadow-compare gate
            // isn't widened beyond scan.committed + tool.telemetry.
            if (_busAdapter == null || !ToolConfig.IsEdgeEnabled("scan.announced")) return;

            _busAdapter.Publish(Topics.ScanAnnounced, new ScanAnnouncedPayload
            {
                WaferId       = waferId,
                LotId         = lotId,
                Slot          = slot,
                CorrelationId = correlationId
            }, correlationId);
        }

        // ── Hook site 2: scan.committed — after CopyScanResults (~:1902 / :10162) ─
        // §2.6 code sketch: reproduced exactly.
        // "Class A: the bus library journals on ITS writer thread before sending;
        //  this call is a lock-free enqueue — no disk, no socket, no sleep. Ever."

        private void PublishScanCommitted(string waferId, string lotId, int slot,
                                          string stableFinalPath, string correlationId)
        {
            if (_busAdapter == null) return;

            // paths ONLY on committed — never on announced (§2.6)
            _busAdapter.Publish(Topics.ScanCommitted, new ScanCommittedPayload
            {
                WaferId       = waferId,
                LotId         = lotId,
                Slot          = slot,
                ResultsPath   = stableFinalPath,
                CorrelationId = correlationId
            }, correlationId);
        }

        // ── Existing scan flow (conceptual — showing call sites) ─────────────────
        // This method represents the existing post-scan result handler at ~:1888-1902.
        // Insert PublishScanAnnounced BEFORE CopyScanResults, PublishScanCommitted AFTER.

        private void OnScanResultsReady(WaferScanResult result)
        {
            // ... existing code ...

            var correlationId = result.CorrelationId; // if not yet threaded through, add it here

            // NEW: announce before copy (identifiers only)
            PublishScanAnnounced(result.WaferId, result.LotId, result.Slot, correlationId);

            // Existing: copy results to stable final path
            var stableFinalPath = CopyScanResults(result);

            // NEW: commit after copy (with stable path)
            PublishScanCommitted(result.WaferId, result.LotId, result.Slot, stableFinalPath, correlationId);

            // Existing: legacy ToolApiPublisher path kept for dual-run (P1a)
            //   _toolApiPublisher.PushEvent(result);  // retired at P1b

            // Existing: Fire* COM event path — unchanged until P2
            //   FireWaferScanResultsAreReady(result);
        }

        // Stub — real signature from existing codebase
        private string CopyScanResults(WaferScanResult result) { return ""; }
    }

    // ═══ clsInitAOI — startup changes ═══════════════════════════════════════════════
    // §2.7: "INIT->>BUS: BusFactory.Connect('AOI_Main') — NON-blocking, jittered retry"
    // §04 §4.2: "Remove EnsureToolGatewayRunning → EnsureBusRunning; non-blocking bus connect"

    public sealed class clsInitAOI
    {
        private readonly CancellationTokenSource _shutdownSource = new CancellationTokenSource();

        // ── Original: EnsureToolGatewayRunning (to be removed at P1a) ────────────
        // private void EnsureToolGatewayRunning() { /* start ToolGateway.exe via SCM */ }

        // ── New: EnsureBusRunning (flag-guarded, P1a) ─────────────────────────────
        // §2.7: "broker absent — AOI never hangs: degraded banner ≤ N s,
        //        AOI-side alarm, EnsureBusRunning self-heal (SCM start, then ToolHost child-restart API)"

        private void EnsureBusRunning()
        {
            // ToolHost owns the broker as a child — ask ToolHost to start if needed.
            // Fall back to SCM start if ToolHost is also down.
            // This call is fire-and-forget; AOI continues even if the bus is not yet up.
        }

        // ── New: BusFactory.Connect (non-blocking) ────────────────────────────────
        // §2.7 startup sequence:

        public IBus InitializeBus(System.Windows.Forms.Control mainFormForMarshaller)
        {
            EnsureBusRunning(); // ask ToolHost to ensure broker is running

            // NON-blocking: returns immediately; background jittered-backoff retry.
            // §6.2: "broker absent — AOI never hangs"
            var bus = BusFactory.Connect("AOI_Main", new BusConfig
            {
                AlarmAfterDisconnect  = TimeSpan.FromSeconds(30),
                ReconnectJitterMs     = 500,
                MaxReconnectBackoffMs = 5000,
                JournalDirectory      = @"C:\bis\journal"
            });

            // Build the UiMarshaller — uses the shutdown token from teardown step 1.
            var marshaller = new UiMarshaller(mainFormForMarshaller, _shutdownSource.Token);

            // Build BusAdapter and register ALL subscriptions (replayed on connect).
            // §2.7: "register ALL subscriptions (replayed on connect)"
            var adapter = new BusAdapter(bus, marshaller, /* simVvrGate */ null,
                                         /* dispatch */ null, /* reactions */ null);
            adapter.RegisterSubscriptions();

            // Fetch current tool state snapshot + buffer replay (§2.4 startup flow).
            adapter.FetchAndApplyToolStateSnapshot();

            // §2.7: "KEEP-lane COM init as today — ChangeToolState stays a direct call"
            InitializeKeepLaneCom();

            return bus;
        }

        private void InitializeKeepLaneCom()
        {
            // Existing MachineSrv / EfemSrv / S12 COM init — unchanged.
        }

        // ── Teardown ──────────────────────────────────────────────────────────────
        // §2.7: "reject-new gate → NACK+compensate queued commands → drain in-flight
        //        → journal flush with timeout → Dispose NEVER on the UI thread"

        public void Teardown(IBus bus, BusAdapter adapter)
        {
            // Step 1: set shutdown token (UiMarshaller.TryPost now returns false)
            _shutdownSource.Cancel();

            // Step 2: NACK + compensate any queued commands
            // (BusAdapter returns Rejected("shutting-down") for any further ServeGuiCommands)

            // Step 3: drain in-flight with timeout
            // Step 4: journal flush with timeout
            adapter.Dispose();
            bus.Dispose();
        }
    }

    // ── Stub types ────────────────────────────────────────────────────────────────

    public sealed class WaferScanResult
    {
        public string WaferId       { get; set; }
        public string LotId         { get; set; }
        public int    Slot          { get; set; }
        public string CorrelationId { get; set; }
    }
}
