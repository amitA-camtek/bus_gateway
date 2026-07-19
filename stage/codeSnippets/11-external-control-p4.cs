// Realizes: §03-lanes Lane-D, §04 §4.2 Track B P4
// File: apps\Falcon.Net\CommonUtils\ComServerWrappers\ExternalControlCbUiWrapper.cs (MODIFIED)
// Responsibility: Full ~18-21-callback surface → in-proc BusAdapter dispatch + compensation table.
//                FalconWrapper.exe is NEVER modified and never becomes a bus client.
//                The customer contract stays byte-identical.
// Phase: P4 — FlaUI + record-replay gated.
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Collections.Generic;
using Falcon.Net.Bus;

namespace Falcon.Net.CommonUtils.ComServerWrappers
{
    // ═══ ExternalControlCbUiWrapper — modified at P4 ════════════════════════════════
    // §03-lanes Lane-D flow (reproduced):
    //   Customer → FalconWrapper (ZERO change) → COM callback → ExternalControlCbUiWrapper
    //   → in-proc BusAdapter dispatch (Ttl/mode gates + marshal)
    //   → ACCEPTED or EXPIRY → compensation Fire* so customer never waits for a done-event

    public sealed class ExternalControlCbUiWrapper : IFalconExternalControlCB
    {
        private readonly BusAdapter  _busAdapter;
        private readonly IFalconFireEvents _fireEvents; // the COM event bus back to FalconWrapper

        // ── P4 rule (review CC10): every external command maps to a synthesized
        // completion the contract expects — reproduced exactly from §03-lanes Lane-D.
        private static readonly Dictionary<ExternalCommand, Action<IFalconFireEvents>>
            Compensations = new Dictionary<ExternalCommand, Action<IFalconFireEvents>>
        {
            { ExternalCommand.StartManualScan, fe => fe.ManualScanDone()          },
            { ExternalCommand.ExportMap,       fe => fe.ExportMapCompleted(false) },
            // value-returning callbacks (GuiSetWaferType etc.): defined default returns
        };

        public ExternalControlCbUiWrapper(BusAdapter busAdapter, IFalconFireEvents fireEvents)
        {
            _busAdapter  = busAdapter;
            _fireEvents  = fireEvents;
        }

        // ── StartManualScan — representative callback; all ~18-21 follow this pattern ─
        // §03-lanes Lane-D: "in-process dispatch (P4) - Ttl/mode gates + marshal apply"

        public void GuiStartManualScan()
        {
            // Ttl deadline on the MONOTONIC clock (review S-4/CC-11) — never DateTime.UtcNow, which
            // an NTP step can move. Derived from the site E30 timeout minus the measured GEM margin.
            var expiresAt = MonotonicClock.Now.AddSeconds(TtlConfig.GetSeconds(ExternalCommand.StartManualScan));

            _busAdapter.DispatchExternalCommand(
                command:   ExternalCommand.StartManualScan,
                expiresAt: expiresAt,
                onExpiry:  () => RunCompensation(ExternalCommand.StartManualScan));
        }

        public void GuiExportMap()
        {
            var expiresAt = MonotonicClock.Now.AddSeconds(TtlConfig.GetSeconds(ExternalCommand.ExportMap));
            _busAdapter.DispatchExternalCommand(
                command:   ExternalCommand.ExportMap,
                expiresAt: expiresAt,
                onExpiry:  () => RunCompensation(ExternalCommand.ExportMap));
        }

        // The remaining ~16-19 callbacks: same pattern.
        // Pre-P4 these went through direct COM calls; at P4 each gets the same
        // Ttl/mode gate and compensation guarantee.

        // ── Compensation runner ────────────────────────────────────────────────────
        // §03-lanes Lane-D: "a NACK/expiry has no COM channel back to the customer —
        //  every external command maps to a synthesized completion the contract expects"

        private void RunCompensation(ExternalCommand command)
        {
            Action<IFalconFireEvents> compensation;
            if (Compensations.TryGetValue(command, out compensation))
                compensation(_fireEvents);
            // If not in the table: no completion event expected (e.g. fire-and-forget cmds)
        }
    }

    // ═══ BusAdapter.DispatchExternalCommand (addition to BusAdapter) ═════════════════
    // This method is added to BusAdapter at P4 to support ExternalControlCbUiWrapper.
    // It applies the same two-stage Ttl gate and BeginInvoke post as ServeGuiCommands,
    // but is called synchronously from the COM callback thread.

    public partial class BusAdapter
    {
        // Separate serialization gate for the EXTERNAL (customer) door. The GEM door must NOT share
        // one slot with it (review D-2/CC-10) — a host command in flight would otherwise make a
        // customer command "complete" instantly, a cross-door interference that exists nowhere today.
        private readonly CommandSerializationGate _externalGate = new CommandSerializationGate();

        public void DispatchExternalCommand(ExternalCommand command,
            DateTime expiresAt, Action onExpiry)
        {
            // Gate #1 — current thread (COM callback thread from FalconWrapper), monotonic clock.
            if (expiresAt <= MonotonicClock.Now)
            {
                // Genuinely EXPIRED before dispatch → the compensation (today's catch-path parity).
                onExpiry();
                return;
            }

            if (!_externalGate.TryEnter(command.ToString()))
            {
                // BUSY is NOT expiry (D-2/CC-10): do NOT synthesize a success completion, and do NOT
                // fire it INLINE inside the customer's inbound COM frame (the customer arms its done-
                // handler after the call returns and would miss an inline event; and a later real
                // completion of the in-flight command would then duplicate it). Reply rejected-busy
                // through the wrapper's normal COM return; the customer retries.
                RejectBusy(command);
                return;
            }

            bool posted = _uiMarshaller.TryPost(() =>
            {
                // Gate #2 — first statement on the UI thread.
                if (_shutdown.IsCancellationRequested)        // late delegate at teardown → no-op (CC-15)
                {
                    _externalGate.Exit(command.ToString());
                    return;
                }
                if (expiresAt <= MonotonicClock.Now)
                {
                    onExpiry();                               // expired while queued → compensation
                    _externalGate.Exit(command.ToString());
                    return;
                }
                // Release the gate on TRUE completion, not the async-void prologue (S-13/CC-12).
                Task exec = _dispatch.Execute(command.ToString());
                exec.ContinueWith(t =>
                {
                    // M-11/GS7-6: today's catch path fires an immediate completion (e.g.
                    // ManualScanDone) on a FAULTED dispatch — parity requires compensating a
                    // faulted/canceled task too, not only Ttl expiry, or the customer strands.
                    if (t.IsFaulted || t.IsCanceled) onExpiry();   // synthesized completion event
                    _externalGate.Exit(command.ToString());
                });
            });

            if (!posted)
                RejectBusy(command); // shutting down — a rejection, not a synthesized success
        }

        // _shutdown (CancellationToken) is declared on the BusAdapter partial in 05-bus-adapter.cs.
        private void RejectBusy(ExternalCommand command)
        {
            // Map to the contract's FAILURE/again signal, never the success completion. For commands
            // whose only completion is a success event (e.g. ManualScanDone), the wrapper returns a
            // busy HRESULT on the COM call instead of firing any Fire* event.
        }
    }

    // ═══ Wrapper call-frequency telemetry (Track D — additive, feeds every gate) ════
    // §04 §4.2 Track D / instruments:
    // "Wrapper call-frequency telemetry (additive logging, feeds every lane's gate)"
    // Added to every wrapper in track D BEFORE the migration decision is made.

    public sealed class WrapperCallTelemetry
    {
        private readonly BusAdapter _busAdapter;
        private readonly string     _wrapperName;

        public WrapperCallTelemetry(BusAdapter busAdapter, string wrapperName)
        {
            _busAdapter  = busAdapter;
            _wrapperName = wrapperName;
        }

        public void Record(string callSite)
        {
            // Publish tool.telemetry (class A-ErrorsOnly, storm-coalesced).
            // This gives the "chattiness evidence base" for every lane's gate decision.
            _busAdapter.Publish(Topics.ToolTelemetry, new TelemetryPayload
            {
                Source        = _wrapperName,
                ErrorCode     = "WrapperCall",
                Message       = callSite,
                TimestampUtc  = DateTime.UtcNow
            });
        }
    }

    // ═══ Stub types ══════════════════════════════════════════════════════════════════

    public enum ExternalCommand
    {
        StartManualScan,
        ExportMap,
        // ... remaining ~13 commands
    }

    public interface IFalconExternalControlCB
    {
        void GuiStartManualScan();
        void GuiExportMap();
        // ... remaining callbacks
    }

    public interface IFalconFireEvents
    {
        void ManualScanDone();
        void ExportMapCompleted(bool success);
        // ... remaining Fire* events
    }

    public static class TtlConfig
    {
        // §6.7: "Ttl derives from per-site E30 timeout config minus a measured margin"
        public static double GetSeconds(ExternalCommand command) { return 30.0; }
    }
}
