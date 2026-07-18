// Realizes: §2.3 Component: UiMarshaller (code sketch reproduced exactly from the design)
// Project: apps\Falcon.Net\AOI_Main (net48, C# 7.3)
// Responsibility: the process's SINGLE marshal primitive.
//                Replaces per-wrapper ad-hoc marshaling and NonBlockingUITask.
//                BeginInvoke-only, shutdown-aware. No DoEvents. Ever.
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Threading;
using System.Windows.Forms;

namespace Falcon.Net.Bus
{
    // ═══ UiMarshaller (reproduced exactly from §2.3) ════════════════════════════════

    // §2.3: "Explicitly NOT based on NonBlockingUITask (that primitive is a reentrant
    //        DoEvents pump and carries a live .Milliseconds-vs-.TotalMilliseconds timeout bug)."
    // §05 LB4: the timeout bug — see 16-live-bug-fixes.cs.

    public sealed class UiMarshaller
    {
        private readonly Control           _dispatcher; // main-form handle owner
        private readonly CancellationToken _shutdown;   // set by teardown step 1

        public UiMarshaller(Control dispatcher, CancellationToken shutdown)
        {
            _dispatcher = dispatcher;
            _shutdown   = shutdown;
        }

        public bool TryPost(Action work)
        {
            if (_shutdown.IsCancellationRequested) return false;
            if (!_dispatcher.IsHandleCreated || _dispatcher.IsDisposed) return false;
            _dispatcher.BeginInvoke(work);   // POST — never Invoke
            return true;
        }

        // For the rare caller that needs a result: post + wait-with-REAL-timeout.
        // §2.3: "caller decides what an abandoned wait means — never blocks forever"
        public bool TryPostAndWait(Action work, TimeSpan timeout)
        {
            // CRITICAL (review S-8/CC-9): if the CALLER is already the UI thread, posting-and-waiting
            // self-deadlocks — the posted work can't run until the UI thread returns, but the UI
            // thread is blocked in done.Wait. The value-returning customer COM callbacks
            // (GuiSetWaferType, get_GuiIsDestinationCarrierReady, …) arrive ON the UI thread, so this
            // is the common case, not the rare one. Run inline instead.
            if (!_dispatcher.InvokeRequired)
            {
                if (_shutdown.IsCancellationRequested) return false;
                work();
                return true;
            }

            using (var done = new ManualResetEventSlim())
            {
                if (!TryPost(() => { try { work(); } finally { done.Set(); } }))
                    return false;
                try
                {
                    // NOTE: TotalMilliseconds — NOT .Milliseconds (§05 LB4 fix)
                    return done.Wait((int)timeout.TotalMilliseconds, _shutdown);
                }
                catch (OperationCanceledException)
                {
                    return false; // shutdown during the wait → abandoned, not an exception (S-8)
                }
            }
        }

        // Run `work` on the UI thread as soon as the dispatcher handle exists; if it isn't ready
        // (startup ordering) or shutdown is pending, re-arm without blocking. Used by the snapshot
        // post so it can never be silently dropped (review S-11).
        public void RunWhenReady(Action work)
        {
            if (_shutdown.IsCancellationRequested) return;
            if (TryPost(work)) return;
            // Handle not created yet — re-check shortly on a pooled timer (net48-safe).
            Timer t = null;
            t = new Timer(_ =>
            {
                if (_shutdown.IsCancellationRequested) { t.Dispose(); return; }
                if (TryPost(work)) t.Dispose();
            }, null, 50, 50);
        }
    }

    // ═══ Placement notes ════════════════════════════════════════════════════════════
    //
    // Instantiated in MainContext alongside IBus and BusAdapter.
    // The dispatcher Control is typically the main form (e.g. frmMain or frmProduction)
    // whose Handle is created on the STA UI thread at application startup.
    //
    // Lane-C absorbed modules (RobotUI etc.) marshal their COM callbacks through
    // this SAME UiMarshaller — it has no bus dependency. The BusAdapter composes it.
    //
    // §2.7 teardown: the CancellationToken is set in teardown step 1 so that any
    // TryPost after that point returns false without queuing work.
    // Dispose NEVER happens on the UI thread (§2.7: "journal flush with timeout,
    // Dispose NEVER on the UI thread").
}
