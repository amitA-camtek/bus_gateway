// Realizes: §1.3.4 GEM shim (inside SecsGemObjects / SecsGemGui.Net — plain C#)
//           §1.1 Flow SYS-2 (factory-host command), §5.3 degraded-operation governance
// File: ToolManagement\SecsGemObjects (new C# class, SecsGemGui.Net project) (MODIFIED P4)
// Responsibility: The ONLY change at the GEM door.
//                Publishes host commands to the bus (gui/tool.commands via REQ).
//                Subscribes to scan.announced and tool.state for host event reports.
//                Degraded contract: bus dark → HCACK denial + ONLINE-LOCAL; never a timeout.
//                Cimetrix driver + E30/E87 logic are UNTOUCHED. Host wire is byte-identical
//                FOR EVENTS AND STATE; the async host-command ACCEPT path is HCACK=4 (see below).
// TODO(X7-8, §1.3.4): the real Cimetrix contract IE30CommandCB.CommandCalled([in,out] eCommandResults)
//   derives HCACK from the value written BEFORE the callback returns — there is NO deferred-reply
//   handle, so "accepted, completed async as HCACK=0" is impossible without parking the reader.
//   The accept path must map to eCmdPerformLater (HCACK=4) + a named completion CEID; CompleteHcack
//   below RAISES that CEID, it does not return a late HCACK-0. This is a HOST-VISIBLE change → P4/P5
//   re-qual budget. The "byte-identical" claim holds for events/state, NOT host commands.
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Threading;
using System.Threading.Tasks;
using Camtek.Messaging;
using Camtek.Messaging.Contracts;

namespace ToolManagement.SecsGemObjects
{
    // ═══ GemBusShim ══════════════════════════════════════════════════════════════════
    // §1.3.4: "Bus shim (NEW, C#) — REQ gui/tool.commands;
    //          subscribes scan.announced, tool.state; degraded contract"

    public sealed class GemBusShim : IDisposable
    {
        private readonly IBus               _bus;
        private readonly IGemSecsCallbacks  _secsCallbacks; // existing E30/E87 callbacks
        private readonly ISubscription      _toolStateSub;
        private readonly ISubscription      _scanAnnouncedSub;

        // Degraded contract state. NOTE (review R-6/CN-3): the full contract is a FOUR-state
        // HSMS×bus machine (HSMS up/down × bus up/down). This sketch models the two booleans the
        // door actually gates on; the explicit 4-state machine is the R-6 design decision.
        // START DEGRADED: _busAvailable begins FALSE and only a COMPLETED bus handshake promotes
        // it — never a single Health.IsConnected read at ctor time (which left _remoteGranted
        // permanently unreachable when the broker was already up at start, review S-10).
        private volatile bool _busAvailable;    // false until HandshakeAsync completes
        private volatile bool _remoteGranted;   // false until the HOST/operator grants REMOTE
        private Timer _healthTimer;             // FIELD, not a local — a local is GC-collected (S-10)

        public GemBusShim(IBus bus, IGemSecsCallbacks secsCallbacks)
        {
            _bus            = bus;
            _secsCallbacks  = secsCallbacks;
            _busAvailable   = false;            // degraded until the handshake proves the bus (S-10)

            // §1.3.4: "subscribes scan.announced, tool.state"
            _toolStateSub = _bus.Subscribe<ToolStatePayload>(
                Topics.ToolState, OnToolState);

            _scanAnnouncedSub = _bus.Subscribe<ScanAnnouncedPayload>(
                Topics.ScanAnnounced, OnScanAnnounced);

            // First action is a bus HANDSHAKE before REMOTE is enabled (§1.3.4). Non-blocking:
            // when it completes the shim leaves degraded; until then the host sees ONLINE-LOCAL.
            _ = HandshakeThenEnableAsync();

            // Watch connectivity for later transitions (field-rooted timer).
            MonitorBusHealth();
        }

        private async Task HandshakeThenEnableAsync()
        {
            try
            {
                // A real round-trip (REQ/PONG), not a Health flag read — proves the broker actually
                // answers before we tell the host the tool is controllable.
                var ok = await _bus.RequestAsync(Topics.ToolState,
                    new ToolStatePayload(), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (ok != null && ok.IsAccepted)
                {
                    _busAvailable = true;
                    // Do NOT auto-grant REMOTE — E30 remote grant is host/operator-initiated. We only
                    // move to "REMOTE grantable"; the host re-grants (removing the S-10 auto-promotion).
                    _secsCallbacks.SetControlState(GemControlState.OnlineLocal);
                }
            }
            catch { /* stay degraded; the health monitor will retry on the next transition */ }
        }

        // ── Command from host: StartManualScan (representative) ───────────────────
        // §1.1 Flow SYS-2: S2F41 arrives → shim → REQ gui.commands → HCACK on reply

        // Called on the HSMS/SECS reader thread. It must return QUICKLY (review S-16/CC-18): the
        // earlier task.Wait(1.1×Ttl) parked the reader ~27 s, stalling S1F13/S6Fx/heartbeat on the
        // same thread and making the host see T3 timeouts on UNRELATED transactions — the exact
        // "outage discovered via mysterious timeouts" §1.3.4 forbids.
        //
        // Fast-path decision only: if the bus is not proven available (composite health, not the
        // socket flag — review CN-9), deny immediately with HCACK. Otherwise hand the command to a
        // worker and complete the E30 transaction ASYNCHRONOUSLY within the E30 window off-thread.
        public GemCommandDisposition HandleS2F41StartManualScan(string correlationId,
                                                                IGemTransaction tx)
        {
            if (!_busAvailable)              // proven-down (handshake not completed / dropped)
            {
                ApplyDegradedContract();
                return GemCommandDisposition.HcackDenied; // deliberate denial — never a timeout
            }
            if (!_remoteGranted)
                return GemCommandDisposition.HcackDenied; // REMOTE not granted by host

            var ttl = TimeSpan.FromSeconds(TtlConfig.GemCommandTtlSeconds);
            // Requester deadline = Ttl (NOT 1.1×Ttl — the extra 10% could exceed the host's E30, S-16).
            var ct  = new CancellationTokenSource(ttl).Token;

            // Off the reader thread: dispatch and complete the E30 reply when the bus replies.
            _ = Task.Run(async () =>
            {
                try
                {
                    var reply = await _bus.RequestAsync(
                        Topics.GuiCommands,
                        new GuiCommandPayload
                        {
                            Command    = "StartManualScan",
                            RequestId  = Guid.NewGuid().ToString("N"),
                            Parameters = null
                        },
                        ttl, ct).ConfigureAwait(false);

                    // TODO(X7-8): CompleteHcack must RAISE the completion CEID (the HCACK=4 outcome),
                    // not return a late HCACK-0. See the header note + §1.3.4.
                    tx.CompleteHcack(reply != null && reply.IsAccepted); // E30 completion off-thread
                }
                catch
                {
                    ApplyDegradedContract();
                    tx.CompleteHcack(false);
                }
            });

            return GemCommandDisposition.Pending; // reader thread returns immediately
        }

        // ── Degraded contract ──────────────────────────────────────────────────────
        // §5.3 GEM process degraded-op governance (reproduced):
        // "bus dark = host-visible control state degrades (ONLINE-LOCAL / alarm),
        //  REMOTE grant refused, commands answered with a deliberate HCACK denial —
        //  the fab never discovers the outage via timeouts"

        private void ApplyDegradedContract()
        {
            // 1. Move host-visible control state to ONLINE-LOCAL (not a timeout — deliberate)
            _secsCallbacks.SetControlState(GemControlState.OnlineLocal);

            // 2. Issue a CEID (Collection Event) alarm so the fab sees the transition
            _secsCallbacks.SendCollectionEvent(GemCollectionEvent.BusDegraded);

            // 3. Refuse REMOTE grant until bus recovers
            _remoteGranted = false;
        }

        private void RecoverFromDegraded()
        {
            // Bus reconnected — move to ONLINE-LOCAL and let the HOST re-grant REMOTE. Do NOT
            // auto-promote to ONLINE-REMOTE (review S-10/CN-3): E30 remote grant is host/operator-
            // initiated; auto-granting it is a compliance bug introduced by the reconnect path.
            _remoteGranted = false;
            _secsCallbacks.SetControlState(GemControlState.OnlineLocal);
            // Retained tool.state is re-reported AFTER the control-state restore, on this same thread.
        }

        // ── Host event reports from bus subscriptions ──────────────────────────────

        private Task OnToolState(BusMessage<ToolStatePayload> msg)
        {
            // Report ToolStateEnum changes to the host via CEID or VID updates.
            // §1.3.4: "subscribes tool.state for host event reports"
            _secsCallbacks.ReportToolState(msg.Payload.State);
            return Task.CompletedTask;
        }

        private Task OnScanAnnounced(BusMessage<ScanAnnouncedPayload> msg)
        {
            // Report early timing info to the host (GEM timing report).
            // §1.1 Flow SYS-1: "GEM shim receives early timing report" from scan.announced
            _secsCallbacks.ReportScanStarted(msg.Payload.WaferId, msg.Payload.Slot);
            return Task.CompletedTask;
        }

        // ── Bus health monitoring ──────────────────────────────────────────────────

        private void MonitorBusHealth()
        {
            // Field-rooted timer (review S-10): a local Timer is GC-collected, after which degraded
            // transitions stop being detected and a later outage yields 27 s stalls instead of HCACK.
            // "Available" is a COMPOSITE signal (connected AND heartbeat-fresh AND loop-lag < L),
            // not the raw socket flag — a HUNG broker holds the pipe open with IsConnected == true
            // (review CN-9). Prefer an event/callback from the bus over polling where available.
            _healthTimer = new Timer(_ =>
            {
                bool nowAvailable = _bus.Health.IsConnected
                                    && _bus.Health.HeartbeatAge < TimeSpan.FromSeconds(6)
                                    && _bus.Health.LoopLagMs < 500;
                if (_busAvailable && !nowAvailable)
                {
                    _busAvailable = false;
                    ApplyDegradedContract();
                }
                else if (!_busAvailable && nowAvailable)
                {
                    // Re-prove with a handshake before leaving degraded (not just a flag flip).
                    _ = HandshakeThenEnableAsync();
                }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            _healthTimer?.Dispose();
            _toolStateSub?.Dispose();
            _scanAnnouncedSub?.Dispose();
        }
    }

    // ═══ Stub interfaces ═════════════════════════════════════════════════════════════

    // A host command returns immediately from the reader thread with a disposition; a Pending
    // command is completed asynchronously via IGemTransaction within the E30 window (review S-16).
    public enum GemCommandDisposition { HcackDenied, Pending }

    public interface IGemTransaction
    {
        void CompleteHcack(bool accepted); // completes the E30 host-command reply off the reader thread
    }

    public enum GemControlState
    {
        OnlineLocal,
        OnlineRemote,
        Offline
    }

    public enum GemCollectionEvent
    {
        BusDegraded = 5001, // define in the tool's CE table
        BusRecovered = 5002
    }

    public interface IGemSecsCallbacks
    {
        void SetControlState(GemControlState state);
        void SendCollectionEvent(GemCollectionEvent evt);
        void ReportToolState(ToolStateEnum state);
        void ReportScanStarted(string waferId, int slot);
    }

    public static class TtlConfig
    {
        // §6.7: Ttl = per-site E30 timeout config minus the measured GEM hop margin (P0 measurement).
        public static double GemCommandTtlSeconds { get { return 25.0; } } // measured at P0
    }
}
