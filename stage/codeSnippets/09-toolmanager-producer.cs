// Realizes: §03-lanes Lane-A P3, §04 §4.2 Track B P3
// File: ToolManagement\ToolManager\Server\ToolEvents.cs (MODIFIED — NOT a small diff; HIGH SEMANTIC, shadow-gated)
// Responsibility: ToolManager-side dual-publish of tool.state + stateSeq stamping.
//                stateSeq is stamped INSIDE a transition-commit lock so the snapshot,
//                COM callback, and bus event all carry the same counter.
// ⚠ REVIEW R-8/FEA-1: that lock DOES NOT EXIST in ToolManager today (_toolState is assigned
//   UNLOCKED in FireToolStateChanged; ChangeToolStateInternal has no lock). P3 must first
//   INTRODUCE + qualify transition serialization across ALL state writers (not 3), with a
//   deadlock audit of the synchronous CB fan-out that will run under it. It is NOT a "3-line" diff.
// Phase: P3 (shadow-gated dual-publish; COM path retained until retention window)
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Threading;
using Camtek.Messaging;
using Camtek.Messaging.Contracts;

namespace ToolManagement.ToolManager.Server
{
    // ═══ ToolEvents.cs — the dual-publish (ALL state writers) ═══════════════════════
    // §04 §4.2 Track B P3: "dual-publish + stateSeq stamped inside it [the transition-
    //   serialization lock to be introduced], covering ALL state writers
    //   (frmProduction.CheckState, BufferStation ToolManagementAdapter, ProductionGui
    //   frmProductionGuiBL, ProductionManager internal — not 3 sites)" (C-2)
    // "Not a small diff — HIGH SEMANTIC, shadow-gated"
    //
    // The sites are wherever the state machine commits a transition + fires the COM
    // IToolManagerCB.OnToolStateChanged callback. Add the bus publish INSIDE the
    // transition-commit lock — the lock to be introduced (R-8) — so the stamp is atomic.
    //
    // Do NOT move the stateSeq stamp outside the lock. That is the entire semantic
    // guarantee — see §2.4: "stateSeq is stamped inside a transition-commit lock (to be introduced)"

    // ── Existing class (simplified — show the modification pattern only) ──────────

    public sealed class ToolEvents
    {
        private readonly object    _stateLock = new object(); // transition lock TO BE INTRODUCED (R-8)
        private ToolStateEnum      _currentState;
        private long               _stateSeq;                // NEW — monotonic counter

        private readonly IBus      _bus;                     // NEW — injected at P3
        private long               _sourceEpoch;             // NEW — incarnation (M-9); persisted with state
        private readonly FanOutWorker _fanOut;               // NEW — single-consumer, seq-ordered (R-8)
        private readonly TransitionRing _replayRing;         // NEW — last-N ring → tool.state.replay (X7-7)

        // Existing COM event fan-out (unchanged path — dual-publish adds the bus publish beside it).
        private readonly IToolManagerCbSink _comSink;

        public ToolEvents(IBus bus, IToolManagerCbSink comSink)
        {
            _bus     = bus;
            _comSink = comSink;
            _fanOut  = new FanOutWorker(FanOutOne);          // drains posted records in seq order
            _replayRing = new TransitionRing(16);            // N per the P0 transition-burst measurement
        }

        // ── The commit sites ──────────────────────────────────────────────────────
        // CRITICAL (M-27/CON7-6, §2.4): the COM callback fan-out must run OUTSIDE the lock.
        // A bare lock held ACROSS the synchronous cross-process IToolManagerCB callback deadlocks
        // (a subscriber's callback, in another apartment, can call back into a ToolManager getter
        // that needs the same lock — the CC-8 cross-apartment reentrancy cycle). The short lock
        // covers ONLY the state assignment + seq stamp and captures an immutable (prev,new,seq)
        // record; the record is handed to a single-consumer fan-out worker that performs the COM
        // callback + bus publish + ring append with NO lock held (total order via one worker).

        public void CommitEngineeringToProductionTransition(string r) => Commit(ToolStateEnum.EngineeringToProduction, r);
        public void CommitProductionTransition(string r)              => Commit(ToolStateEnum.Production, r);
        public void RevertToEngineering(string r)                     => Commit(ToolStateEnum.Engineering, r);

        private void Commit(ToolStateEnum newState, string reason)
        {
            StateTransition rec;
            lock (_stateLock)                      // covers ONLY the stamp — never the fan-out
            {
                var prev      = _currentState;
                _currentState = newState;
                var seq       = Interlocked.Increment(ref _stateSeq);
                rec = new StateTransition(prev, newState, seq, _sourceEpoch, reason);
            }
            _fanOut.Post(rec);                     // single-consumer worker, drains in seq order,
        }                                          // does COM callback + bus publish + ring append OUTSIDE any lock

        // The fan-out worker body (one thread; ordered by seq):
        private void FanOutOne(StateTransition rec)
        {
            _comSink.OnToolStateChanged(MapToComState(rec.NewState)); // COM callback — NO lock held
            DualPublishToolState(rec, rec.Reason);                    // bus publish (tool.state)
            _replayRing.Append(rec);                                  // last-N ring → tool.state.replay (X7-7)
        }

        // ── Also expose a synchronous snapshot read (used by BusAdapter.FetchAndApplyToolStateSnapshot) ──

        public (ToolStateEnum State, long StateSeq) GetSnapshot()
        {
            lock (_stateLock)
            {
                return (_currentState, _stateSeq);
            }
        }

        // ── Dual-publish helper — same payload as the COM callback ────────────────
        // §03-lanes Lane-A: "Step 1 — DUAL-PUBLISH (bus path is shadow-only at P3)"
        // §04: "ToolState topic, class B retained"

        private void DualPublishToolState(StateTransition rec, string reason)
        {
            if (_bus == null) return; // flag-guarded: bus publish disabled until P3 activated

            // ≤1 ms — lock-free enqueue; never on the transition lock (§6.2 Publish bound)
            _bus.Publish(Topics.ToolState, new ToolStatePayload
            {
                State     = rec.NewState,
                PrevState = rec.PrevState,   // edges are load-bearing for the E30 mapping (M-8)
                StateSeq  = rec.Seq,         // ordered by (SourceEpoch, StateSeq) downstream (M-9)
                Reason    = reason
            });
        }

        private static int MapToComState(ToolStateEnum s)
        {
            // Map to the existing COM IToolManagerCB integer values (unchanged).
            return (int)s;
        }
    }

    // ═══ Shadow comparator stub (P3 gate requirement) ════════════════════════════════
    // §5.2 per-edge gate (R-TS-2 event-count, M-23): "zero unexplained shadow divergence over
    // >=10,000 scan.committed pairs / >=500 tool.state transitions incl. scripted storms" —
    // NOT calendar days (near-zero power at ~10 tool.state/day).
    // §05 §5.4 risk: "atomic reaction-block rule + (SourceEpoch, stateSeq) pairing" are the mitigations.
    //
    // The comparator receives both the COM copy and the bus copy, pairs them by
    // correlationId+seq, and fires an alarm on unexplained divergence.
    // Evictions (COM fired but bus not yet delivered) are an EXPLAINED category — they
    // never block the gate; only unpairable-and-non-eviction divergences block.

    public sealed class ToolStateShadowComparator
    {
        public void OnComEvent(int comState, long stateSeq, string correlationId)
        {
            // TODO: pair with bus copy; alarm on unexplained divergence
        }

        public void OnBusEvent(ToolStatePayload payload, string correlationId)
        {
            // TODO: pair with COM copy; alarm on unexplained divergence
        }
    }

    // ── Stub interfaces + supporting types (R-8 / X7-7 shape) ───────────────────────

    public interface IToolManagerCbSink
    {
        void OnToolStateChanged(int comToolState);
    }

    // Immutable transition record captured under the short lock, drained outside it.
    public sealed class StateTransition
    {
        public ToolStateEnum PrevState { get; }
        public ToolStateEnum NewState  { get; }
        public long          Seq       { get; }
        public long          Epoch     { get; }
        public string        Reason    { get; }
        public StateTransition(ToolStateEnum prev, ToolStateEnum ns, long seq, long epoch, string reason)
        { PrevState = prev; NewState = ns; Seq = seq; Epoch = epoch; Reason = reason; }
    }

    // Single-consumer, seq-ordered worker — the COM callback + bus publish run here, NO lock held.
    public sealed class FanOutWorker
    {
        public FanOutWorker(System.Action<StateTransition> body) { /* TODO: one thread, ordered queue */ }
        public void Post(StateTransition rec) { /* TODO: enqueue; worker drains in Seq order */ }
    }

    // Bounded last-N ring; served as the R-R tool.state.replay topic for gap recovery (X7-7).
    public sealed class TransitionRing
    {
        public TransitionRing(int n) { /* TODO: ring of N */ }
        public void Append(StateTransition rec) { /* TODO */ }
        public StateTransition[] Since(long fromSeq) { return null; /* TODO: replay after a reconnect gap */ }
    }
}
