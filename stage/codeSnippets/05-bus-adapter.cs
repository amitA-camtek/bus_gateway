// Realizes: §2.2 Component: BusAdapter (code sketch reproduced exactly + expanded)
// Project: apps\Falcon.Net\AOI_Main (net48, C# 7.3)
// Responsibility: ALL bus interaction for the process — subscriptions, two-stage Ttl gate,
//                central Sim/VVR gate, command serialization gate, BeginInvoke post only,
//                request/reply server, publish façade.
// Constraint: C# 7.3 / net48-compatible syntax. No records. No switch expressions.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Camtek.Messaging;
using Camtek.Messaging.Contracts;

namespace Falcon.Net.Bus
{
    // ═══ BusAdapter ══════════════════════════════════════════════════════════════════

    public partial class BusAdapter : IDisposable
    {
        private readonly IBus                   _bus;
        private readonly UiMarshaller           _uiMarshaller;
        private readonly ISimVvrGate            _simVvrGate;
        private readonly ICommandDispatch       _dispatch;
        private readonly CompensationTable      _compensations;
        private readonly CommandSerializationGate _commandGate = new CommandSerializationGate();
        private readonly List<ISubscription>    _subscriptions = new List<ISubscription>();

        // ── State snapshot replay fields (§2.4 stateSeq ordering) ─────────────────
        // All of these are touched ONLY on the UI thread (every entry point marshals through
        // _uiMarshaller). No lock is needed — but the invariant must be asserted, not assumed
        // (review CC-13): OnToolStateEvent/OnSnapshot should Debug.Assert(!InvokeRequired).
        private long _lastAppliedSeq = -1;
        private long _lastEpoch      = -1;           // publisher incarnation (R-2/C8-CRIT-8): dedup key
                                                     // is (SourceEpoch, StateSeq) — epoch resets seq space
        private readonly List<ToolStateEvent> _preSnapshotBuffer = new List<ToolStateEvent>();
        private const int PreSnapshotBufferCap = 512;   // alarm + drop-oldest beyond this (S-11)
        private bool _snapshotApplied;

        private readonly CancellationToken _shutdown;            // set by teardown step 1 (§2.7)
        private const int _commandWatchdogMs = 120000;          // gate-release watchdog (S-13)

        private readonly ToolStateReactions _reactions;

        public BusAdapter(IBus bus, UiMarshaller uiMarshaller, ISimVvrGate simVvrGate,
                          ICommandDispatch dispatch, ToolStateReactions reactions)
        {
            _bus           = bus;
            _uiMarshaller  = uiMarshaller;
            _simVvrGate    = simVvrGate;
            _dispatch      = dispatch;
            _reactions     = reactions;
            _compensations = new CompensationTable();
        }

        // ─── Register all subscriptions (called from clsInitAOI — §2.7) ──────────
        // §2.7: "register ALL subscriptions (replayed on connect)"

        public void RegisterSubscriptions()
        {
            _subscriptions.Add(
                _bus.Serve<GuiCommandPayload>(Topics.GuiCommands, ServeGuiCommands));

            _subscriptions.Add(
                _bus.Subscribe<ToolStatePayload>(Topics.ToolState, OnToolStateDelivered));

            _subscriptions.Add(
                _bus.Subscribe<LoaderEventPayload>(Topics.LoaderEvents, OnLoaderEvent));
        }

        // ─── gui.commands — the two-stage Ttl gate + post (reproduced from §2.2) ─
        // §2.2 Critical section code: reproduced exactly.
        // RULES (review CC9): blocking Invoke is BANNED; reply = ACCEPTED on post;
        // Ttl re-checked as the FIRST statement on the executing (UI) thread.

        private Task<Reply> ServeGuiCommands(BusMessage<GuiCommandPayload> msg)
        {
            // Gate #1 — dispatcher thread, monotonic clock captured at frame receipt.
            if (msg.ExpiresAt <= MonotonicClock.Now)
                return Task.FromResult(Reply.Expired());

            if (_simVvrGate.IsOffline(msg))
                return Task.FromResult(Reply.Rejected("offline-mode"));

            if (!_commandGate.TryEnter(msg.Payload.RequestId))
                return Task.FromResult(Reply.RejectedBusy());

            bool posted = _uiMarshaller.TryPost(() =>
            {
                // Gate #2 — FIRST statement on the UI thread. A command that sat behind a modal
                // dialog past its deadline must not run.
                if (_shutdown.IsCancellationRequested)        // late delegate at teardown → no-op (S-15/CC-15)
                {
                    _commandGate.Exit(msg.Payload.RequestId);
                    return;
                }
                if (msg.ExpiresAt <= MonotonicClock.Now)
                {
                    _compensations.Run(msg.Payload.Command);
                    _bus.Publish(Topics.ToolTelemetry, TelemetryEvent.CommandExpired(msg));
                    _commandGate.Exit(msg.Payload.RequestId);
                    return;
                }

                // The command flow is async (today's async-void scan flow). Exit the gate ONLY when
                // the flow actually COMPLETES — not at its first await (review S-13/CC-12). Execute
                // now returns a Task; a watchdog releases the gate + alarms if a flow never completes,
                // so a wedged command can't hold the gate closed forever.
                Task exec = _dispatch.Execute(msg.Payload.Command);
                ReleaseGateOnCompletion(exec, msg.Payload.RequestId);
            });

            return Task.FromResult(posted
                ? Reply.Accepted()
                : Reply.Rejected("shutting-down"));
        }

        private void ReleaseGateOnCompletion(Task exec, string requestId)
        {
            var released = 0;
            Action release = () =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                    _commandGate.Exit(requestId);
            };
            exec.ContinueWith(_ => release());                       // normal / faulted completion
            var watchdog = new Timer(__ => { Alarm(); release(); },  // never-completing flow guard
                null, _commandWatchdogMs, Timeout.Infinite);
            exec.ContinueWith(_ => watchdog.Dispose());
        }

        // ─── tool.state — stateSeq ordering (reproduced from §2.4) ───────────────
        // §2.4: "applied in stateSeq order so a stale snapshot can never overwrite
        //        a newer streamed event"

        private Task OnToolStateDelivered(BusMessage<ToolStatePayload> msg)
        {
            // Handler runs on a pool thread; marshal to UI thread before applying.
            var payload = msg.Payload;
            _uiMarshaller.TryPost(() => OnToolStateEvent(
                new ToolStateEvent { State = payload.State, SourceEpoch = payload.SourceEpoch, StateSeq = payload.StateSeq }));
            return Task.CompletedTask;
        }

        // Called from clsInitAOI after BusFactory.Connect (§2.7 startup flow).
        public void FetchAndApplyToolStateSnapshot()
        {
            // COM call to ToolManager today; retained class-B subscription post-P3.
            // stateSeq is stamped inside a ToolManager transition-commit lock that MUST BE INTRODUCED
            // (it does not exist today — review R-8/FEA-1; §03-lanes P3).
            var snapshot = FetchToolStateFromCom();

            // The snapshot post MUST land, or _snapshotApplied stays false forever and every later
            // tool.state event buffers unboundedly (review S-11/CC-13). At startup the main form
            // handle may not exist yet → TryPost returns false. Retry until the handle is up.
            PostSnapshotWhenReady(snapshot.State, snapshot.SourceEpoch, snapshot.StateSeq);
        }

        private void PostSnapshotWhenReady(ToolStateEnum state, long epoch, long seq)
        {
            if (_uiMarshaller.TryPost(() => OnSnapshot(state, epoch, seq)))
                return;
            // Handle not created / shutting down. Re-arm shortly; the marshaller is shutdown-aware,
            // so once shutdown is signalled this simply stops (no infinite loop at teardown).
            _uiMarshaller.RunWhenReady(() => PostSnapshotWhenReady(state, epoch, seq));
        }

        // UI thread — the stateSeq ordering logic from §2.4.
        private void OnToolStateEvent(ToolStateEvent e)
        {
            if (!_snapshotApplied)
            {
                // Bound the pre-snapshot buffer: if the snapshot never applied (S-11), don't grow
                // without limit — alarm and keep only the newest (class-B latest-wins semantics).
                if (_preSnapshotBuffer.Count >= PreSnapshotBufferCap)
                {
                    _preSnapshotBuffer.RemoveAt(0);
                    Alarm();
                }
                _preSnapshotBuffer.Add(e);
                return;
            }
            ApplyIfNewer(e.State, e.SourceEpoch, e.StateSeq);
        }

        // UI thread.
        private void OnSnapshot(ToolStateEnum state, long epoch, long seq)
        {
            ApplyIfNewer(state, epoch, seq);
            foreach (var e in _preSnapshotBuffer
                .OrderBy(x => x.SourceEpoch)
                .ThenBy(x => x.StateSeq))
                ApplyIfNewer(e.State, e.SourceEpoch, e.StateSeq);
            _preSnapshotBuffer.Clear();
            _snapshotApplied = true;
        }

        // Ordering key is (SourceEpoch, StateSeq): a ToolManager restart resets StateSeq to 0
        // but bumps epoch, so fresh-after-restart transitions are never dropped as stale (C8-CRIT-8).
        private void ApplyIfNewer(ToolStateEnum s, long epoch, long seq)
        {
            if (epoch < _lastEpoch) return;
            if (epoch == _lastEpoch && seq <= _lastAppliedSeq) return;
            _lastEpoch      = epoch;
            _lastAppliedSeq = seq;
            _reactions.Apply(s);                 // atomic block, never split across disciplines (P3 rule)
        }

        // ─── loader.events ────────────────────────────────────────────────────────

        private Task OnLoaderEvent(BusMessage<LoaderEventPayload> msg)
        {
            var payload = msg.Payload;
            _uiMarshaller.TryPost(() =>
            {
                // Feed absorbed RobotUI and GUI via one marshal (§03-lanes Lane-A P2).
            });
            return Task.CompletedTask;
        }

        // ─── Publish façade (≤1 ms — §1.4) ───────────────────────────────────────
        // The journal-writer thread takes ownership; the caller never touches disk.

        public void Publish<T>(Topic topic, T payload, string correlationId = null)
        {
            _bus.Publish(topic, payload, correlationId != null
                ? new PublishOptions { CorrelationId = correlationId }
                : null);
        }

        // ─── Stubs ────────────────────────────────────────────────────────────────

        private (ToolStateEnum State, long SourceEpoch, long StateSeq) FetchToolStateFromCom()
        {
            // TODO: call ToolManagerUiWrapper.GetCurrentState() + .GetCurrentStateSeq() + .GetSourceEpoch()
            return (ToolStateEnum.Engineering, 0L, 0L);
        }

        private static void Alarm() { /* → ToolHost alarm surface + log */ }

        public void Dispose()
        {
            foreach (var sub in _subscriptions) sub.Dispose();
            _subscriptions.Clear();
        }
    }

    // ═══ Supporting types ════════════════════════════════════════════════════════════

    public sealed class ToolStateEvent
    {
        public ToolStateEnum State       { get; set; }
        public long          SourceEpoch { get; set; } // publisher incarnation (C8-CRIT-8/R-2)
        public long          StateSeq    { get; set; }
    }

    /// <summary>
    /// One command in flight at a time. A second command while one is in flight is REJECTED-BUSY
    /// (review D-2/CON-4): the design has one busy semantic, not three. "Busy" is NOT "expired" — it
    /// must never run the compensation table (which would report a never-accepted command as
    /// COMPLETED to the customer). The reject flows back as Reply.RejectedBusy().
    /// §2.2: "command serialization gate (one gui.command in flight)"
    /// </summary>
    internal sealed class CommandSerializationGate
    {
        private volatile string _inFlight;

        public bool TryEnter(string requestId)
        {
            return Interlocked.CompareExchange(ref _inFlight, requestId, null) == null;
        }

        public void Exit(string requestId)
        {
            Interlocked.CompareExchange(ref _inFlight, null, requestId);
        }
    }

    // Placeholder interfaces — real signatures come from the existing codebase.
    public interface ISimVvrGate
    {
        bool IsOffline<T>(BusMessage<T> msg);
    }

    public interface ICommandDispatch
    {
        // Returns a Task that completes when the command flow ACTUALLY finishes (review S-13/CC-12).
        // Today's async-void scan flows are wrapped in a TaskCompletionSource keyed off their Fire*
        // done-event so the serialization gate can be released on true completion, not at first await.
        Task Execute(string command);
    }

    // Stopwatch-based monotonic clock (review S-4). NEVER DateTime.UtcNow for Ttl: an NTP step or
    // controller resync can move the wall clock backward/forward, expiring commands early or making
    // them immortal. This value only ever advances, and is anchored once at process start so it is
    // still a DateTime the sketch can compare — but it never jumps.
    public static class MonotonicClock
    {
        private static readonly DateTime _anchor = DateTime.UtcNow;
        private static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

        public static DateTime Now
        {
            get { return _anchor + _sw.Elapsed; } // monotonic: _sw.Elapsed never decreases
        }
    }

    public static class TelemetryEvent
    {
        public static TelemetryPayload CommandExpired<T>(BusMessage<T> msg)
        {
            return new TelemetryPayload
            {
                Source        = "AOI_Main",
                ErrorCode     = "CommandExpired",
                Message       = "Command expired in UI queue: " + msg.Envelope.MessageId,
                CorrelationId = msg.Envelope.CorrelationId,
                TimestampUtc  = DateTime.UtcNow
            };
        }
    }
}
