// Realizes: §03-lanes Lane-A P3, §04 §4.2 Track B P3
// File: ToolManagement\ToolManager\Server\ToolEvents.cs (MODIFIED — small diff, high semantic)
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
    // ═══ ToolEvents.cs — the three-site dual-publish ════════════════════════════════
    // §04 §4.2 Track B P3: "3-site dual-publish + stateSeq stamped in the commit lock"
    //                       "Small diff / HIGH SEMANTIC — shadow-gated"
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

        // Existing COM event fan-out (unchanged path — dual-publish adds the bus publish beside it).
        private readonly IToolManagerCbSink _comSink;

        public ToolEvents(IBus bus, IToolManagerCbSink comSink)
        {
            _bus     = bus;
            _comSink = comSink;
        }

        // ── The three sites where state transitions commit ────────────────────────
        // All three follow this pattern. Only the state values differ.

        // Site 1: Engineering → EngineeringToProduction (on StartProduction)
        public void CommitEngineeringToProductionTransition(string reason)
        {
            long seq;
            lock (_stateLock)
            {
                _currentState = ToolStateEnum.EngineeringToProduction;
                seq           = Interlocked.Increment(ref _stateSeq); // stamp INSIDE lock

                // Existing COM callback — unchanged, still the authoritative path (P3 dual-run)
                _comSink.OnToolStateChanged(MapToComState(_currentState));
            }
            // Bus publish AFTER releasing the lock — the seq is already stamped above.
            // The value of seq cannot be overwritten by a concurrent transition because
            // the transition-commit lock serializes all state changes.
            DualPublishToolState(_currentState, seq, reason);
        }

        // Site 2: EngineeringToProduction → Production (transition succeeds)
        public void CommitProductionTransition(string reason)
        {
            long seq;
            lock (_stateLock)
            {
                _currentState = ToolStateEnum.Production;
                seq           = Interlocked.Increment(ref _stateSeq);
                _comSink.OnToolStateChanged(MapToComState(_currentState));
            }
            DualPublishToolState(_currentState, seq, reason);
        }

        // Site 3: EngineeringToProduction → Engineering (transition fails/aborted)
        public void RevertToEngineering(string reason)
        {
            long seq;
            lock (_stateLock)
            {
                _currentState = ToolStateEnum.Engineering;
                seq           = Interlocked.Increment(ref _stateSeq);
                _comSink.OnToolStateChanged(MapToComState(_currentState));
            }
            DualPublishToolState(_currentState, seq, reason);
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

        private void DualPublishToolState(ToolStateEnum state, long seq, string reason)
        {
            if (_bus == null) return; // flag-guarded: bus publish disabled until P3 activated

            // ≤1 ms — lock-free enqueue; never on the transition lock (§6.2 Publish bound)
            _bus.Publish(Topics.ToolState, new ToolStatePayload
            {
                State    = state,
                StateSeq = seq,    // the seq stamped inside the commit lock above
                Reason   = reason
            });
        }

        private static int MapToComState(ToolStateEnum s)
        {
            // Map to the existing COM IToolManagerCB integer values (unchanged).
            return (int)s;
        }
    }

    // ═══ Shadow comparator stub (P3 gate requirement) ════════════════════════════════
    // §5.2 per-edge gate: "zero unexplained shadow divergence over N production days"
    // §05 §5.4 risk: "atomic reaction-block rule + stateSeq pairing" are the mitigations.
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

    // ── Stub interfaces ───────────────────────────────────────────────────────────

    public interface IToolManagerCbSink
    {
        void OnToolStateChanged(int comToolState);
    }
}
