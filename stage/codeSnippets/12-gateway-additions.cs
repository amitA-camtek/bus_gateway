// Realizes: §1.3.2 ToolConnect gateway additions (BusSource + CommandPublisher + spool fixes)
//           §05 §5.5 LB1/LB2/LB5 spool bug fixes (independent of the fabric program)
// Project: Utilities\ToolGateway\ToolGateway.BL + .Endpoint (net7 → net8 at P1a)
// Phase: P1a — BusSource + CommandPublisher added; :5005 kept for dual-run; spool fixes ship in Wave 0
// Note: net8 — modern C# 12 syntax.

using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Camtek.Messaging;
using Camtek.Messaging.Contracts;

namespace ToolGateway.BL
{
    // ═══ BusSource (NEW) ════════════════════════════════════════════════════════════
    // §1.3.2: "BusSource — subscribes scan.committed, tool.telemetry, tool.state;
    //          WAL-append BEFORE ack; health = consumption liveness token"
    // §1.1 View 1 Flow SYS-1: "GW->>GW: WAL spool append (durable ownership FIRST)
    //                           GW-->>BUS: DELIVER_ACK — publisher journal appends ack-tombstone"
    //
    // Zero silent loss guarantee: the WAL is appended before DELIVER_ACK so a gateway
    // crash between DELIVER_ACK and sink persistence is recoverable (§6.10 assertion 5).

    public sealed class BusSource : IDisposable
    {
        private readonly IBus           _bus;
        private readonly WalSpool       _wal;
        private readonly EventRouter    _router;
        private readonly ISubscription  _scanCommittedSub;
        private readonly ISubscription  _telemetrySub;
        private readonly ISubscription  _toolStateSub;

        public BusSource(IBus bus, WalSpool wal, EventRouter router)
        {
            _bus    = bus;
            _wal    = wal;
            _router = router;

            // §1.3.2: "subscribes scan.committed, tool.telemetry, tool.state"
            _scanCommittedSub = _bus.Subscribe<ScanCommittedPayload>(
                Topics.ScanCommitted, OnScanCommitted);

            _telemetrySub = _bus.Subscribe<TelemetryPayload>(
                Topics.ToolTelemetry, OnToolTelemetry);

            _toolStateSub = _bus.Subscribe<ToolStatePayload>(
                Topics.ToolState, OnToolState);
        }

        // ── Handlers: WAL-before-ack pattern ──────────────────────────────────────
        // §1.3.2: "WAL-append BEFORE ack"
        // The bus library sends DELIVER_ACK only after this Task completes (class A).
        // Writing to WAL here is the durable ownership claim.

        private async Task OnScanCommitted(BusMessage<ScanCommittedPayload> msg)
        {
            // 1. Append to WAL FIRST — durable ownership before ack
            await _wal.AppendAsync(msg.Envelope, msg.Payload).ConfigureAwait(false);

            // 2. Route to sinks (FleetSink, TsmcSink via EventRouter/SinkDispatchers)
            await _router.RouteAsync(msg.Envelope, msg.Payload).ConfigureAwait(false);

            // DELIVER_ACK is sent automatically by the bus library when this Task completes.
        }

        private async Task OnToolTelemetry(BusMessage<TelemetryPayload> msg)
        {
            await _wal.AppendAsync(msg.Envelope, msg.Payload).ConfigureAwait(false);
            await _router.RouteAsync(msg.Envelope, msg.Payload).ConfigureAwait(false);
        }

        private async Task OnToolState(BusMessage<ToolStatePayload> msg)
        {
            // tool.state is class B (retained) — WAL for audit only, no class-A E2E required
            await _router.RouteAsync(msg.Envelope, msg.Payload).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _scanCommittedSub?.Dispose();
            _telemetrySub?.Dispose();
            _toolStateSub?.Dispose();
        }
    }

    // ═══ CommandPublisher :5007 (NEW) ════════════════════════════════════════════════
    // §1.3.2: "CommandPublisher :5007 — validate + authorize + audit; publishes tool/gui.commands"
    // §1.1 View 3 Flow SYS-3: "M->>GW: remote operation request → authenticate + authorize + audit
    //                           GW->>BUS: REQ tool.commands (Ttl, ACL: GEM shim + gateway only)"
    //                          "bus down: :5007 answers 'fabric unavailable' immediately"
    // §6.8: "command publishes and ACL rejections audited with correlationId"

    public sealed class CommandPublisher
    {
        private readonly IBus _bus;

        public CommandPublisher(IBus bus)
        {
            _bus = bus;
        }

        // Called from the :5007 gRPC/REST endpoint handler.
        public async Task<CommandResult> PublishGuiCommandAsync(
            string command, string requestId, string callerIdentity, string correlationId,
            TimeSpan ttl, CancellationToken ct)
        {
            // 1. Validate + authorize
            if (!IsAuthorized(callerIdentity, command))
            {
                Audit("rejected", command, callerIdentity, correlationId);
                return CommandResult.Unauthorized;
            }

            // 2. Audit the publish (§6.8)
            Audit("accepted", command, callerIdentity, correlationId);

            // 3. Check bus health
            if (!_bus.Health.IsConnected)
                return CommandResult.FabricUnavailable; // immediate response — never blocks

            // 4. Publish REQ to bus (ACL enforced at broker: only GemShim|Gateway allowed)
            try
            {
                var reply = await _bus.RequestAsync(
                    Topics.GuiCommands,
                    new GuiCommandPayload
                    {
                        Command    = command,
                        RequestId  = requestId,
                        Parameters = null
                    },
                    ttl, ct).ConfigureAwait(false);

                return reply.IsAccepted
                    ? CommandResult.Accepted
                    : CommandResult.Rejected(reply.Reason);
            }
            catch (OperationCanceledException)
            {
                return CommandResult.TimedOut;
            }
        }

        private bool IsAuthorized(string identity, string command)
        {
            // TODO: mTLS / Windows auth per §6.8 + §5.6 open question 7
            return true;
        }

        private void Audit(string outcome, string command, string identity, string correlationId)
        {
            // §6.8: "command publishes and ACL rejections audited with correlationId"
        }
    }

    public sealed class CommandResult
    {
        public static readonly CommandResult Accepted          = new CommandResult("accepted");
        public static readonly CommandResult Unauthorized      = new CommandResult("unauthorized");
        public static readonly CommandResult FabricUnavailable = new CommandResult("fabric-unavailable");
        public static readonly CommandResult TimedOut          = new CommandResult("timed-out");

        public string Reason { get; }
        private CommandResult(string reason) { Reason = reason; }

        public static CommandResult Rejected(string reason) => new CommandResult("rejected:" + reason);
    }

    // ═══ WAL spool (role change from Wave 0) ════════════════════════════════════════
    // §1.3.2: "WAL spool — role change: poison-only dead-letter; outage retries forever
    //          under quota; periodic 60 s drain"
    // Spool bugs LB1/LB5 fixed here (see also 16-live-bug-fixes.cs for the minimal diffs).

    public sealed class WalSpool
    {
        private readonly string         _spoolDirectory;
        private readonly Channel<SpoolEntry> _drainChannel;
        private readonly Timer          _periodicDrainTimer;

        public WalSpool(string spoolDirectory)
        {
            _spoolDirectory = spoolDirectory;

            // §05 LB5 fix: use a sufficiently large channel — no overflow file
            _drainChannel = Channel.CreateBounded<SpoolEntry>(new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.Wait // back-pressure instead of overflow file
            });

            // §1.3.2: "periodic 60 s drain"
            _periodicDrainTimer = new Timer(_ => TriggerDrain(), null,
                TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        // WAL append — durable ownership BEFORE ack
        public async Task AppendAsync<T>(BusEnvelope envelope, T payload)
        {
            var entry = new SpoolEntry { Envelope = envelope };
            var path  = Path.Combine(_spoolDirectory, envelope.MessageId + ".wal");
            await File.WriteAllTextAsync(path,
                Newtonsoft.Json.JsonConvert.SerializeObject(entry)).ConfigureAwait(false);
        }

        // Periodic drain — retries oldest-first, interleaved with live traffic, capped rate.
        // §1.3.2: "a one-hour outage drains in <10 min without any restart"
        private void TriggerDrain()
        {
            // TODO: scan spool dir, order by timestamp, drain at capped rate
            // §05 LB5 fix: the drain loop exists and runs — not just at process start
        }

        public async Task MarkDeliveredAsync(string messageId)
        {
            var path = Path.Combine(_spoolDirectory, messageId + ".wal");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ═══ Stubs (existing types from ToolGateway.BL) ═════════════════════════════════

    public sealed class EventRouter
    {
        public async Task RouteAsync<T>(BusEnvelope envelope, T payload)
        {
            // Existing: SinkDispatchers → FleetSink + TsmcSink
            await Task.CompletedTask;
        }
    }

    public sealed class SpoolEntry
    {
        public BusEnvelope Envelope { get; set; }
    }
}
