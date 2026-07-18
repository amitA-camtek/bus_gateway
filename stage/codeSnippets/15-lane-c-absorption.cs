// Realizes: §03-lanes Lane-C (CONS absorption) — in-proc seam + event bridge
// Files: AOI_Main\CommonUtils\ComServerWrappers\RobotUiConnector.cs (NEW seam)
//        machine\RobotUIControls.NET\RobotUiModule.cs (MODIFIED — step 4 pump deletion)
// Census: RobotUI ✅ PASSED (sole-consumer, single-STA criterion met)
// Phase: Wave 1 — first CONS module (different files from P1a bus work — safe concurrency)
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Windows.Forms;
using Falcon.Net.Bus;

namespace Falcon.Net.CommonUtils.ComServerWrappers
{
    // ═══ The in-proc seam (reproduced exactly from §03-lanes Lane-C) ═════════════════
    // §03-lanes Lane-C code sketch: reproduced verbatim.
    // "Absorption seam — same interface, no process, no COM."

    public static class RobotUiConnector
    {
        public static IRobotUi Instance { get { return _lazy.Value; } }

        private static readonly Lazy<IRobotUi> _lazy = new Lazy<IRobotUi>(() =>
            ToolConfig.ModuleMode("RobotUI") == "inproc"
                ? (IRobotUi)new RobotUiModule(MainContext.Instance.UiMarshaller) // in-proc,
                : new RobotUiRotProxy());                                        // flag-rollback
    }

    // ═══ RobotUiModule — the in-proc instance ════════════════════════════════════════
    // §03-lanes Lane-C step 4 (the real work):
    //   "delete the module's own Application.Run pump + DoEvents loop;
    //    form created on the main UI thread; all COM proxies confined to one apartment"
    // Single-STA rule is an ACCEPTANCE CRITERION — dual-STA mutual-Invoke = permanent deadlock.

    internal sealed class RobotUiModule : IRobotUi, IDisposable
    {
        private readonly UiMarshaller    _uiMarshaller;
        private readonly IEfemEvents     _efemEvents; // existing COM event source

        // frmRobot is now an OWNED form on AOI's main UI thread (no private Application.Run)
        private readonly Form            _frmRobot;

        public event EventHandler<WaferTypeLoadedArgs> WaferTypeLoaded;
        public event EventHandler<CassetteLoadedArgs>  CassetteLoaded;

        public RobotUiModule(UiMarshaller uiMarshaller)
        {
            _uiMarshaller = uiMarshaller;

            // §03-lanes Lane-C: step 4 — form created on the main UI thread
            // All COM proxies (MachineSrv, EFEM, WafersDB, JobProvider) acquired here in the
            // main apartment — they move with the module from the old process into AOI_Main.
            _frmRobot   = new Form(); // TODO: replace with the actual frmRobot type
            _efemEvents = AcquireEfemEventsProxy(); // COM proxy, main apartment

            // ── Event bridge (reproduced exactly from §03-lanes Lane-C code sketch) ──
            // §03-lanes Lane-C: "the COM CB sink becomes a plain .NET event, marshalled once"
            _efemEvents.WaferTypeLoaded += args =>
                _uiMarshaller.TryPost(() => WaferTypeLoaded?.Invoke(this, args));

            _efemEvents.CassetteLoaded += args =>
                _uiMarshaller.TryPost(() => CassetteLoaded?.Invoke(this, args));

            // Pre-P2: EFEM events arrive via COM through UiMarshaller.
            // Post-P2: loader.events subscription replaces the COM sink (§03-lanes Lane-A P2).
        }

        // ── IRobotUi implementation ────────────────────────────────────────────────
        // Call sites in AOI_Main use IRobotUi — no change needed at the call site;
        // the connector seam provides either the in-proc or the ROT proxy transparently.

        public void ShowSortingScreen(string waferId)
        {
            _uiMarshaller.TryPost(() =>
            {
                // Must run on the UI thread — the form is on AOI's main thread now
                // _frmRobot.ShowSortingScreen(waferId); // existing form method
            });
        }

        // ── Transitional dual-STA note (step 4) ────────────────────────────────────
        // §03-lanes Lane-C: "transitional dual-STA only with GIT marshaling + post-only calls"
        // If RobotUI's COM proxies temporarily live in a separate STA during the transition,
        // ALL calls to them MUST be BeginInvoke (TryPost) — never a synchronous Invoke.
        // Any Invoke creates mutual Invoke chains → permanent deadlock.

        private IEfemEvents AcquireEfemEventsProxy()
        {
            // Re-acquire in the main apartment.
            // The ROT registration (the old singleton proxy) is retired in step 5
            // after the N-release retention window.
            return null; // TODO: resolve via ROT or new direct COM activation
        }

        public void Dispose()
        {
            _frmRobot?.Dispose();
        }
    }

    // ═══ RobotUiRotProxy — the flag-rollback path ═══════════════════════════════════
    // Existing COM proxy path — unchanged; selected when ModuleMode("RobotUI") != "inproc".

    internal sealed class RobotUiRotProxy : IRobotUi
    {
        public event EventHandler<WaferTypeLoadedArgs> WaferTypeLoaded;
        public event EventHandler<CassetteLoadedArgs>  CassetteLoaded;

        public void ShowSortingScreen(string waferId)
        {
            // Existing COM call through RobotUIEventHandlerWrapper — unchanged.
        }
    }

    // ═══ Post-P2 update: loader.events subscription ══════════════════════════════════
    // §03-lanes Lane-A P2: "AOI's BusAdapter subscribes and feeds the (absorbed) RobotUI
    //                        and GUI via one marshal; the CB sink registration is retired"
    // This replaces the _efemEvents COM sink above.
    // Shown as a separate snippet for clarity; in practice it lives in BusAdapter.OnLoaderEvent.

    internal static class PostP2LoaderEventsWiring
    {
        // In BusAdapter, after P2:
        // _bus.Subscribe<LoaderEventPayload>(Topics.LoaderEvents, OnLoaderEvent);
        //
        // private Task OnLoaderEvent(BusMessage<LoaderEventPayload> msg)
        // {
        //     _uiMarshaller.TryPost(() =>
        //     {
        //         var module = RobotUiConnector.Instance;
        //         switch (msg.Payload.EventType)
        //         {
        //             case "WaferTypeLoaded":
        //                 module.WaferTypeLoaded?.Invoke(module,
        //                     new WaferTypeLoadedArgs { WaferId = msg.Payload.WaferId });
        //                 break;
        //             case "CassetteLoaded":
        //                 module.CassetteLoaded?.Invoke(module,
        //                     new CassetteLoadedArgs { WaferId = msg.Payload.WaferId });
        //                 break;
        //         }
        //     });
        //     return Task.CompletedTask;
        // }
    }

    // ═══ Stub types ══════════════════════════════════════════════════════════════════

    public interface IRobotUi
    {
        event EventHandler<WaferTypeLoadedArgs> WaferTypeLoaded;
        event EventHandler<CassetteLoadedArgs>  CassetteLoaded;
        void ShowSortingScreen(string waferId);
    }

    public interface IEfemEvents
    {
        event Action<WaferTypeLoadedArgs> WaferTypeLoaded;
        event Action<CassetteLoadedArgs>  CassetteLoaded;
    }

    public sealed class WaferTypeLoadedArgs : EventArgs { public string WaferId { get; set; } }
    public sealed class CassetteLoadedArgs  : EventArgs { public string WaferId { get; set; } }

    public static class MainContext
    {
        public static MainContextInstance Instance { get; } = new MainContextInstance();
    }

    public sealed class MainContextInstance
    {
        public UiMarshaller UiMarshaller { get; set; }
    }

}
