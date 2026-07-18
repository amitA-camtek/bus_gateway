// Realizes: §2.4 Component: ToolStateReactions + stateSeq
// Project: apps\Falcon.Net\AOI_Main (net48, C# 7.3)
// Responsibility: GUI reactions to tool-state transitions — extracted from
//                frmProduction.ToolStateChanged (~60 lines) into a testable class,
//                applied in stateSeq order (stale snapshot cannot overwrite newer event).
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Collections.Generic;
using System.Linq;
using Camtek.Messaging.Contracts;

namespace Falcon.Net.Bus
{
    // ═══ ToolStateReactions ══════════════════════════════════════════════════════════
    // §2.4: "extracted from frmProduction.ToolStateChanged (~60 lines, verified no direct
    //        control access) into a testable class, applied in stateSeq order"

    public sealed class ToolStateReactions
    {
        // All dependencies injected — the class has NO Windows.Forms references.
        // This makes it directly unit-testable without a running WinForms pump.

        private readonly IGuiStateSink    _gui;
        private readonly IJobStateSink    _jobs;
        private readonly IBatchStateSink  _batch;

        public ToolStateReactions(IGuiStateSink gui, IJobStateSink jobs, IBatchStateSink batch)
        {
            _gui   = gui;
            _jobs  = jobs;
            _batch = batch;
        }

        // §2.4: "atomic reaction block — never split across disciplines (P3 rule)"
        // All reactions for a given state change run inside ONE Apply call.
        // The apply point is always the UI thread (marshaled by BusAdapter).
        public void Apply(ToolStateEnum state)
        {
            switch (state)
            {
                case ToolStateEnum.NotInitialized:
                case ToolStateEnum.Initialization:
                    _gui.DisableProduction();
                    _batch.Clear();
                    break;

                case ToolStateEnum.Engineering:
                    _gui.ShowEngineeringMode();
                    _gui.SetLightTower(LightColor.Yellow);
                    _batch.Clear();
                    _jobs.UnloadCurrentJob();
                    break;

                case ToolStateEnum.EngineeringToProduction:
                    _gui.ShowTransitioning();
                    _gui.SetLightTower(LightColor.Yellow);
                    break;

                case ToolStateEnum.Production:
                    _gui.ShowProductionMode();
                    _gui.SetLightTower(LightColor.Green);
                    _jobs.ReloadJob();
                    break;
            }
        }
    }

    // ═══ stateSeq ordering — the ordering logic lives in BusAdapter (§2.4) ═════════
    // Reproduced here as a standalone testable unit for clarity.
    // In the real codebase these three methods live inside BusAdapter.

    public sealed class ToolStateOrderingBuffer
    {
        // §2.4 flow: subscribe → buffer pre-snapshot events → fetch snapshot → replay buffer.
        // "stateSeq is stamped INSIDE the transition-commit lock" (§2.4 diagram note)
        // "single application point (UI thread) — total order —
        //  the stale-snapshot-overwrites-newer-event race is structurally closed"

        private long _lastAppliedSeq = -1;
        private readonly List<ToolStateEvent> _preSnapshotBuffer = new List<ToolStateEvent>();
        private bool _snapshotApplied;

        private readonly ToolStateReactions _reactions;

        public ToolStateOrderingBuffer(ToolStateReactions reactions)
        {
            _reactions = reactions;
        }

        // Called (marshalled, UI thread) for each bus event before the snapshot is ready.
        // §2.4 code sketch: reproduced exactly.
        public void OnToolStateEvent(ToolStateEvent e)
        {
            if (!_snapshotApplied) { _preSnapshotBuffer.Add(e); return; }
            ApplyIfNewer(e.State, e.StateSeq);
        }

        // Called (marshalled, UI thread) once the COM/retained snapshot arrives.
        // §2.4 code sketch: reproduced exactly.
        public void OnSnapshot(ToolStateEnum state, long seq)
        {
            ApplyIfNewer(state, seq);
            foreach (var e in _preSnapshotBuffer.OrderBy(x => x.StateSeq))
                ApplyIfNewer(e.State, e.StateSeq);
            _preSnapshotBuffer.Clear();
            _snapshotApplied = true;
        }

        // §2.4 code sketch: reproduced exactly.
        private void ApplyIfNewer(ToolStateEnum s, long seq)
        {
            if (seq <= _lastAppliedSeq) return;   // stale — drop
            _lastAppliedSeq = seq;
            _reactions.Apply(s);                  // ToolStateReactions.Apply — atomic block
        }
    }

    // ═══ Interfaces (injection points) ══════════════════════════════════════════════
    // Keeping ToolStateReactions free of WinForms makes the full reaction set unit-testable.

    public interface IGuiStateSink
    {
        void DisableProduction();
        void ShowEngineeringMode();
        void ShowProductionMode();
        void ShowTransitioning();
        void SetLightTower(LightColor color);
    }

    public interface IJobStateSink
    {
        void UnloadCurrentJob();
        void ReloadJob();
    }

    public interface IBatchStateSink
    {
        void Clear(); // mBatch = null
    }

    public enum LightColor { Red, Yellow, Green, Off }
}
