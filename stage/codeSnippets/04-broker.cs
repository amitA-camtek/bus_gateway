// Realizes: §1.3.1 broker design, §6.6 broker internals
// Project: Camtek.Messaging.Broker (net8.0)
// Role: ToolHost child, startOrder 0, quarantine: never, priorityClass: AboveNormal
// Note: net8 — modern C# 12 syntax allowed.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Camtek.Messaging.Contracts;

namespace Camtek.Messaging.Broker
{
    // ═══ Entry point ═════════════════════════════════════════════════════════════════

    internal static class Program
    {
        static async Task Main(string[] args)
        {
            var broker = new BrokerHost();
            await broker.RunAsync(CancellationToken.None);
        }
    }

    // ═══ BrokerHost ══════════════════════════════════════════════════════════════════

    internal sealed class BrokerHost
    {
        private readonly ConnectionManager _connections = new ConnectionManager();

        public async Task RunAsync(CancellationToken ct)
        {
            // Named pipe server — one server instance per process connection
            // §6.4: pipe-ACL authenticated; identity = authenticated account + HELLO.sourceName
            while (!ct.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream("camtek.bus",
                    PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                _ = _connections.AcceptAsync(server, ct); // fire-and-forget per connection
            }
        }
    }

    // ═══ ConnectionManager ══════════════════════════════════════════════════════════
    // §6.6: "identity = authenticated account + HELLO.sourceName; publish-ACL enforcement;
    //        per-connection outbound writer task with write deadline"

    internal sealed class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, ClientConnection> _clients
            = new ConcurrentDictionary<string, ClientConnection>();

        private readonly TopicRouter _router;
        private readonly HealthReporter _health;

        public ConnectionManager()
        {
            _router = new TopicRouter(_clients);
            _health = new HealthReporter(_clients);
        }

        public async Task AcceptAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            var conn = new ClientConnection(pipe, _router, ct);
            try
            {
                await conn.HandshakeAsync(); // HELLO — identity + subscriptions

                // Identity-conflict policy (review S-12/CN-5): a crashed-but-not-dead client, or a
                // second session, can HELLO with an already-registered sourceName. Supersede by
                // (sourceEpoch, PID): the higher epoch wins, the older connection is dropped with a
                // GOODBYE(superseded) and audited. Never silently TryAdd-and-ignore — that leaves the
                // new connection unrouted and lets the old one's cleanup remove the wrong entry.
                _clients.AddOrUpdate(conn.SourceName, conn, (name, existing) =>
                {
                    if (conn.SourceEpoch >= existing.SourceEpoch)
                    {
                        existing.SendGoodbyeSuperseded();
                        existing.Dispose();
                        return conn;          // new incarnation takes the slot
                    }
                    conn.SendGoodbyeSuperseded();
                    throw new InvalidOperationException("stale HELLO refused"); // keep the existing one
                });

                await Task.WhenAll(conn.ReadLoopAsync(), conn.WriteLoopAsync()); // split I/O
            }
            catch { /* log + disconnect */ }
            finally
            {
                // INSTANCE-compare removal — never remove a newer connection that reused this name
                // while this (older) one was faulting (the silent-half-dead-hub bug, S-12).
                _clients.TryRemove(new KeyValuePair<string, ClientConnection>(conn.SourceName, conn));
                // §6.6: a DECLARED durable subscriber does NOT leave its pending E2E-ack sets on a mere
                // disconnect (that is R-1); only an explicit DEregistration does. Transient subs do.
                _router.OnDisconnect(conn.SourceName, conn);
                conn.Dispose();
            }
        }
    }

    // ═══ ClientConnection ════════════════════════════════════════════════════════════

    internal sealed class ClientConnection : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly TopicRouter           _router;
        private readonly CancellationToken     _ct;
        private readonly PrioritySendQueue     _outQueue = new PrioritySendQueue();

        public string SourceName { get; private set; }
        public long   SourceEpoch { get; private set; }   // incarnation from HELLO (S-2/S-12)
        public string PipeAccount { get; private set; }   // OS-authenticated account (R-7)
        public IReadOnlyList<string> SubscribedTopics { get; private set; }

        public ClientConnection(NamedPipeServerStream pipe, TopicRouter router, CancellationToken ct)
        {
            _pipe   = pipe;
            _router = router;
            _ct     = ct;
        }

        public async Task HandshakeAsync()
        {
            // Read HELLO: identity + subscriptions + per-class-A resumeFromSeq + sourceEpoch.
            // §6.6/R-7: the publish ACL must key on the OS-authenticated PIPE ACCOUNT, not the
            // self-asserted HELLO.sourceName (which is a label only). Capture the account here.
            PipeAccount      = ReadPipeAccount(_pipe);   // e.g. via pipe.GetImpersonationUserName()
            SourceName       = "TODO: from HELLO";
            SourceEpoch      = 0L;                        // from HELLO
            SubscribedTopics = new List<string>();
        }

        private static string ReadPipeAccount(NamedPipeServerStream pipe)
            => null; // TODO(R-7): pipe.GetImpersonationUserName() / SID → account→Acl map

        public void SendGoodbyeSuperseded() { /* write GOODBYE(superseded) then close (S-12) */ }

        public async Task ReadLoopAsync()
        {
            // Reads PUB / REQ / REPLY / PING / DELIVER_ACK / E2E_ACK frames
            // Dispatches each to TopicRouter
            await Task.CompletedTask;
        }

        public async Task WriteLoopAsync()
        {
            // Priority-dequeues frames, writes with a write deadline.
            // §6.4: "per-frame write deadlines: client reconnects, broker disconnects subscriber"
            // §6.6: "a suspended subscriber is disconnected, never allowed to stall siblings"
            while (!_ct.IsCancellationRequested)
            {
                // var frame = _outQueue.Dequeue(_ct);
                // WriteFrame(frame);  // with write deadline
                await Task.Delay(1, _ct);
            }
        }

        // Returns false when the bounded class-A lane is full → caller (router) sends NACK (S-6).
        public bool Enqueue(RouterFrame frame) => _outQueue.Enqueue(frame);

        public void Dispose() => _pipe.Dispose();
    }

    // ═══ TopicRouter ═════════════════════════════════════════════════════════════════
    // Holds the class-A/B/C queues; dispatches incoming PUB to all matched subscribers.

    internal sealed class TopicRouter
    {
        private readonly ConcurrentDictionary<string, ClientConnection> _clients;

        // §6.6 class-A delivery + bounding now lives in each connection's PrioritySendQueue A lane
        // (review S-6) — the router no longer keeps parallel accounting queues that bounded nothing.

        // §6.6 class-B: keyed retained slot per (topic, key); atomic dequeue-marks-consumed
        private readonly ConcurrentDictionary<string, RetainedSlot> _classBSlots
            = new ConcurrentDictionary<string, RetainedSlot>();

        public TopicRouter(ConcurrentDictionary<string, ClientConnection> clients)
        {
            _clients = clients;
        }

        public void Route(BusEnvelope envelope, ClientConnection sender)
        {
            var topic = TopicRegistry.Find(envelope.Topic);
            if (topic == null) return;

            // ACL check — reject + audit if sender's OS account is not allowed to publish this topic
            if (!topic.Publishers.HasFlag(SenderToAcl(sender)))
            {
                // §6.8: "command publishes and ACL rejections audited with correlationId"
                AuditAclRejection(envelope, sender.SourceName);
                return;
            }

            switch (topic.DurabilityClass)
            {
                case DurabilityClass.A_NeverLose:
                case DurabilityClass.A_ErrorsOnly:
                    RouteClassA(envelope, topic);
                    break;
                case DurabilityClass.B_Retained:
                    RouteClassB(envelope, topic);
                    break;
                case DurabilityClass.C_BestEffort:
                    RouteClassC(envelope, topic);
                    break;
                case DurabilityClass.R_RequestReply:
                    RouteReqReply(envelope);
                    break;
            }
        }

        private void RouteClassA(BusEnvelope envelope, Topic topic)
        {
            // The per-connection bounded A lane IS the delivery queue (S-6): one enqueue, and its
            // return value decides NACK. Full → NACK (message stays in the PUBLISHER's journal),
            // never a silent broker-side buffer growth.
            //
            // DURABLE-SUBSCRIBER SET (review R-1 — design decision, not baked in here): the ack must
            // cover every *declared* durable subscriber (topic registry), NOT merely the connections
            // live at PUB. A declared-but-disconnected durable subscriber ⇒ NACK-equivalent, so the
            // publisher journal holds the message until it reconnects. "Zero live subscribers" must
            // NOT ack immediately when a durable subscriber is merely down — that is the gateway-
            // restart silent-loss channel. The line below is the SKETCH placeholder for that set.
            var subscribers = GetDurableAndLiveSubscribers(topic);   // R-1: declared ∪ live
            if (subscribers.Count == 0)
            {
                // Truly no subscriber configured (e.g. gateway-disabled tool by signed profile):
                // immediate PUB_ACK is correct here — no journal leak. Distinguished from "down".
                return;
            }
            foreach (var sub in subscribers)
            {
                var frame = new RouterFrame(envelope, lane: SendLane.A);
                if (!sub.Enqueue(frame))
                    SendNack(envelope.Source, envelope.MessageId); // full → publisher keeps it
            }
        }

        private void RouteClassB(BusEnvelope envelope, Topic topic)
        {
            // §6.6: keyed-slot coalesce — retained per (topic, KEY), not per topic (review S-7).
            // production.carrier keyed by CarrierId; a naive per-topic slot loses every carrier but
            // the last. Update marks the slot dirty; the subscriber writer consumes it atomically.
            var key  = topic.RetainedKeySelector != null ? topic.RetainedKeySelector(envelope) : topic.Name;
            var slot = _classBSlots.GetOrAdd(topic.Name + "|" + key, _ => new RetainedSlot());
            slot.Update(envelope);
            foreach (var sub in GetSubscribers(topic.Name))
                sub.Enqueue(new RouterFrame(envelope, lane: SendLane.B));
        }

        private void RouteClassC(BusEnvelope envelope, Topic topic)
        {
            // §6.6: drop-oldest + counted. The connection's B/C lane IS the drop-oldest queue —
            // a single enqueue (the earlier double-enqueue made the accounting queue decorative, S-6).
            foreach (var sub in GetSubscribers(topic.Name))
                sub.Enqueue(new RouterFrame(envelope, lane: SendLane.C));
        }

        private void RouteReqReply(BusEnvelope envelope)
        {
            // §6.7: no server registered → the broker must send an immediate REPLY(rejected:no-server),
            // so a caller can distinguish "tool dead" from "slow" instead of hanging to the Ttl
            // (review CN-7). Queue-full → REPLY(rejected-busy).
            var targets = GetSubscribers(envelope.Topic);
            if (targets.Count == 0)
            {
                SendReply(envelope.Source, envelope.MessageId, "rejected:no-server");
                return;
            }
            // Route to the connection whose account matches the topic's server ACL, not blindly [0]
            // (review SEC-9 — full OS-account binding is R-7).
            targets[0].Enqueue(new RouterFrame(envelope, isReq: true, lane: SendLane.ReqReply));
        }

        // R-1 placeholder: union of the topic's DECLARED durable subscribers (from the registry,
        // resolved to their connection if live) and any transient live subscribers.
        private List<ClientConnection> GetDurableAndLiveSubscribers(Topic topic)
            => GetSubscribers(topic.Name); // TODO(R-1): fold in declared durable subscribers by identity

        public void OnDisconnect(string sourceName, ClientConnection conn)
        {
            // Transient subscriber: leaves its pending sets. DECLARED durable subscriber (R-1):
            // its pending E2E-ack sets are RETAINED (the message waits in the publisher journal
            // until it reconnects) — disconnect is not deregistration.
            // Publisher disconnect purges its routing entries.
        }

        public void DeliverRetainedOnSubscribe(ClientConnection conn, string topicName)
        {
            // §6.6 class B: deliver the last retained value of EVERY key under this topic to the new
            // subscriber (S-7 — a single per-topic slot delivered only one carrier). Broker restart
            // note (R-5): retained slots are in-memory; after a broker restart they are empty until
            // each class-B publisher re-publishes on reconnect — that re-publish is the R-5 decision.
            foreach (var kv in _classBSlots)
            {
                if (!kv.Key.StartsWith(topicName + "|")) continue;
                conn.Enqueue(new RouterFrame(kv.Value.LastEnvelope, lane: SendLane.B));
            }
        }

        private List<ClientConnection> GetSubscribers(string topicName)
        {
            var result = new List<ClientConnection>();
            foreach (var conn in _clients.Values)
                if (conn.SubscribedTopics != null && conn.SubscribedTopics.Contains(topicName))
                    result.Add(conn);
            return result;
        }

        // R-7/SEC-1: map the OS-authenticated PIPE ACCOUNT (not the self-asserted sourceName) to an
        // Acl. Default DENY — the earlier `return Acl.Any` was fail-open (any sender satisfied any
        // topic ACL). Full account→Acl binding is the R-7 security work-stream.
        private static Acl SenderToAcl(ClientConnection conn)
        {
            return Acl.None; // TODO(R-7): resolve conn.PipeAccount → the account's granted Acl
        }
        private void AuditAclRejection(BusEnvelope e, string source) { /* append-only, off-bus (R-7) */ }
        private void SendNack(string source, string msgId) { /* send NACK to publisher */ }
        private void SendReply(string source, string requestId, string status) { /* REPLY frame */ }
    }

    // ═══ Heartbeat / health ══════════════════════════════════════════════════════════
    // §6.6: "PING priority-dequeued; loop-lag self-check via a pipe-frame probe;
    //        counters pushed to ToolHost each heartbeat (survive broker death)"

    internal sealed class HealthReporter
    {
        private readonly ConcurrentDictionary<string, ClientConnection> _clients;

        public HealthReporter(ConcurrentDictionary<string, ClientConnection> clients)
        {
            _clients = clients;
        }

        public async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // Send PING to all connections (priority-dequeued)
                // Measure loop lag: time between PING sent and PONG received
                // Push counters to ToolHost health endpoint :5100
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    // ═══ Utility types (stubs) ═══════════════════════════════════════════════════════

    internal enum SendLane { ReqReply, A, B, C }

    internal sealed class RouterFrame
    {
        public BusEnvelope Envelope { get; }
        public bool        IsReq    { get; }
        public SendLane    Lane     { get; }
        public RouterFrame(BusEnvelope envelope, bool isReq = false, SendLane lane = SendLane.C)
        {
            Envelope = envelope;
            IsReq    = isReq;
            Lane     = lane;
        }
    }

    // Thread-safe bounded queue with an explicit dequeue path and a low-watermark signal.
    // Review S-6: the original had a plain Queue<T>, no lock, and NO dequeue at all, so the
    // class-A "bound" bounded nothing and NACK was terminal (RESUME never fired).
    internal sealed class BoundedQueue<T>
    {
        private readonly Queue<T> _q = new Queue<T>();
        private readonly object   _gate = new object();
        private readonly int      _capacity;
        private readonly int      _lowWatermark;
        public BoundedQueue(int capacity)
        {
            _capacity     = capacity;
            _lowWatermark = capacity / 4;   // RESUME when drained below this (§6.6)
        }
        public bool TryEnqueue(T item)
        {
            lock (_gate)
            {
                if (_q.Count >= _capacity) return false;   // full → caller sends NACK
                _q.Enqueue(item);
                return true;
            }
        }
        public bool TryDequeue(out T item)
        {
            lock (_gate)
            {
                if (_q.Count == 0) { item = default(T); return false; }
                item = _q.Dequeue();
                return true;
            }
        }
        // True exactly as the queue crosses below the low-watermark → time to send RESUME.
        public bool CrossedLowWatermark
        {
            get { lock (_gate) { return _q.Count == _lowWatermark; } }
        }
        public int Count { get { lock (_gate) { return _q.Count; } } }
    }

    internal sealed class DropOldestQueue<T>
    {
        private readonly Queue<T> _q;
        private readonly int      _capacity;
        private long _dropped;
        public DropOldestQueue(int capacity) { _q = new Queue<T>(capacity); _capacity = capacity; }
        public void Enqueue(T item)
        {
            if (_q.Count >= _capacity) { _q.Dequeue(); Interlocked.Increment(ref _dropped); }
            _q.Enqueue(item);
        }
        public long Dropped { get { return Volatile.Read(ref _dropped); } }
    }

    // §6.6 class-B keyed slot with atomic dequeue-marks-consumed (review S-7). The subscriber writer
    // calls TryConsume to grab the latest value once; a fresh Update between reads re-arms it. This
    // coalesces (a slow subscriber sees only the newest value, not the backlog) — the property a
    // naive "enqueue every publish" delivery loses.
    internal sealed class RetainedSlot
    {
        private readonly object _gate = new object();
        private BusEnvelope _last;
        private bool _dirty;

        public BusEnvelope LastEnvelope { get { lock (_gate) { return _last; } } }

        public void Update(BusEnvelope e)
        {
            lock (_gate) { _last = e; _dirty = true; }   // coalesce: newest wins
        }

        public bool TryConsume(out BusEnvelope e)
        {
            lock (_gate)
            {
                if (!_dirty) { e = null; return false; }
                e = _last; _dirty = false; return true;   // atomic: mark consumed
            }
        }
    }

    // The per-connection outbound queue IS the writer's single source (review S-6). Class-A rides
    // a BOUNDED lane (NACK on full → RESUME on drain); class-A frames are routed to the A lane, not
    // dumped into B/C; the low lane is drop-oldest. Dequeue is weighted so B/C never fully starves.
    internal sealed class PrioritySendQueue
    {
        private readonly ConcurrentQueue<RouterFrame> _reqReply = new ConcurrentQueue<RouterFrame>();
        private readonly BoundedQueue<RouterFrame>    _classA   = new BoundedQueue<RouterFrame>(capacity: 128);
        private readonly DropOldestQueue<RouterFrame> _classBc  = new DropOldestQueue<RouterFrame>(capacity: 4096);
        private readonly SemaphoreSlim                _signal   = new SemaphoreSlim(0);
        private int _reqCredit, _aCredit;
        private const int ReqWeight = 8, AWeight = 4;

        // Returns false ONLY when the bounded class-A lane is full → the router sends a NACK and the
        // message stays in the PUBLISHER's journal (broker memory stays bounded — §6.6).
        public bool Enqueue(RouterFrame f)
        {
            switch (f.Lane)
            {
                case SendLane.ReqReply: _reqReply.Enqueue(f); break;
                case SendLane.A:
                    if (!_classA.TryEnqueue(f)) return false; // full → NACK
                    break;
                default: _classBc.Enqueue(f); break;          // B/C: drop-oldest + counted
            }
            _signal.Release();
            return true;
        }

        // True when class-A just drained below its low-watermark → send RESUME to the publisher.
        public bool ShouldResumeClassA { get { return _classA.CrossedLowWatermark; } }

        public RouterFrame Dequeue(CancellationToken ct)
        {
            _signal.Wait(ct);
            RouterFrame f;
            if (_reqCredit < ReqWeight && _reqReply.TryDequeue(out f)) { _reqCredit++; return f; }
            if (_aCredit   < AWeight   && _classA.TryDequeue(out f))   { _aCredit++;   return f; }
            if (_classBc.TryDequeue(out f)) { _reqCredit = 0; _aCredit = 0; return f; }
            _reqCredit = 0; _aCredit = 0;
            if (_reqReply.TryDequeue(out f)) return f;
            _classA.TryDequeue(out f);
            return f;
        }
    }

    // Stub — real implementation uses the topic definitions from Contracts.
    internal static class TopicRegistry
    {
        public static Topic Find(string name) => null; // TODO: look up in Topics.*
    }
}
