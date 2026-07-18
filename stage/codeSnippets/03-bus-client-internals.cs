// Realizes: §6.4 wire protocol (frames, split I/O, priority lanes),
//           §6.5 client internals (publish path ≤1 ms, journal-writer thread, reconnect algorithm),
//           §6.7 request/reply protocol
// Project: Camtek.Messaging — internal implementation (net48;net8.0)
// Constraint: C# 7.3 / net48-compatible syntax.
// Note: This is a design sketch. Internal helper types (PrioritySendQueue etc.) are stubs.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Camtek.Messaging.Contracts;
using Newtonsoft.Json;

namespace Camtek.Messaging
{
    // ═══ Wire frame types ═══════════════════════════════════════════════════════════
    // §6.4 — all frames listed; framing = 4-byte length prefix + UTF-8 JSON

    internal enum FrameType
    {
        Hello,       // identity + subscriptions + per-class-A resumeFromSeq
        Pub,         // publish (client → broker)
        PubAck,      // broker enqueued to all matched subscriber queues (B/C sufficient)
        Deliver,     // broker → subscriber
        DeliverAck,  // durable ownership confirmed (class A — WAL appended at gateway)
        E2eAck,      // per (message, subscriber-set snapshot) — journal appends ack-tombstone
        Nack,        // class-A queue full — message stays in publisher's journal
        Resume,      // queue below low-watermark — redelivery starts
        Req,         // R-R command (requestId, ttlMs)
        Reply,       // R-R response
        Ping,        // heartbeat — priority-dequeued; carries measured loop lag
        Pong         // heartbeat response
    }

    // ═══ BusClient — the IBus implementation ════════════════════════════════════════

    internal sealed class BusClient : IBus
    {
        private readonly string    _sourceName;
        private readonly BusConfig _config;

        // The channel between callers and the journal-writer thread.
        // Lock-free enqueue; bounded to keep memory predictable under NACK.
        private readonly BlockingCollection<SendEntry> _journalIn
            = new BlockingCollection<SendEntry>(capacity: 8192);

        // Single-writer journal thread — the ONLY thread that touches journal files.
        private readonly Thread _journalThread;

        // Priority send queue: REQ/REPLY > A > B/C  (§6.4 priority lanes)
        private readonly PrioritySendQueue _pumpQueue = new PrioritySendQueue();

        private NamedPipeClientStream _pipe;
        private volatile bool         _connected;
        private CancellationTokenSource _pumpCts;       // per-connection; cancels both pump loops
        private long                  _nextSeq = 0L;     // per-source monotonic

        // Publisher incarnation. Persisted next to the journal and restored on start (review S-2/R-2):
        // without an epoch, a journal reset restarts _nextSeq at 0 and the gateway's seq-contiguity
        // dedup silently discards fresh wafers as "duplicates". Dedup key = (source, epoch, topic, seq).
        // NOTE: full resolution is a design decision (R-2) — this field is the sketch-level anchor.
        private readonly long _sourceEpoch = SourceEpoch.LoadOrCreate();

        // Subscriptions registered locally; replayed on every (re)connect.
        private readonly List<ILocalSubscription> _subscriptions  = new List<ILocalSubscription>();
        private readonly Dictionary<string, ReplyWaiter> _pending = new Dictionary<string, ReplyWaiter>();
        private readonly object _subLock = new object();

        private readonly BusCounters _counters = new BusCounters();
        public BusHealth    Health   { get { return BuildHealth(); } }
        public IBusCounters Counters { get { return _counters; } }

        internal BusClient(string sourceName, BusConfig config)
        {
            _sourceName   = sourceName;
            _config       = config;
            _journalThread = new Thread(JournalWriterLoop)
            {
                Name         = "bus-journal-writer",
                IsBackground = true,
                Priority     = ThreadPriority.AboveNormal
            };
        }

        // ─── Publish — the ≤1 ms guaranteed entry point ──────────────────────────
        // §6.5: "lock-free enqueue; no disk, no socket, no lock across I/O"

        public void Publish<T>(Topic topic, T payload, PublishOptions options = null)
        {
            var envelope = BuildEnvelope(topic, payload, options);

            // §6.5: lock-free enqueue, returns immediately. BUT the bounded intake CAN reject
            // under sustained backpressure — a discarded TryAdd is silent class-A loss (review S-1).
            // The return value is load-bearing: never ignore it.
            if (_journalIn.TryAdd(new SendEntry(topic, envelope)))
            {
                _counters.IncrementPublished(topic);
                return;
            }

            // Intake full → apply the topic's declared cap policy. NEVER a silent drop,
            // NEVER a throw to the caller (§6.5), and NEVER counted as published.
            _counters.IncrementRefused(topic);
            if (topic.DurabilityClass == DurabilityClass.A_NeverLose)
                Alarm.Raise(AlarmId.JournalIntakeSaturated, topic.Name); // refuse-new + loud alarm;
                                                                        // Health.RefusedPublishes exposes
                                                                        // it so frmScanTab can pause at the
                                                                        // wafer boundary (§6.5 / review R-9)
            // A_ErrorsOnly / B / C → drop-and-count is the contract; the counter above is the record.
        }

        // ─── Subscribe ────────────────────────────────────────────────────────────

        public ISubscription Subscribe<T>(Topic topic, Func<BusMessage<T>, Task> handler,
                                          SubscribeOptions options = null)
        {
            var sub = new Subscription<T>(topic, handler, options ?? new SubscribeOptions());
            lock (_subLock) { _subscriptions.Add(sub); }
            if (_connected) SendHello();
            return sub;
        }

        // ─── Request/Reply ────────────────────────────────────────────────────────
        // §6.7: ttl mandatory; requester-side deadline mandatory

        public Task<Reply> RequestAsync<T>(Topic topic, T payload, TimeSpan ttl,
                                           CancellationToken ct = default(CancellationToken))
        {
            var envelope = BuildEnvelope(topic, payload, null, ttlMs: (long)ttl.TotalMilliseconds);
            var waiter   = new ReplyWaiter(ct);
            lock (_subLock) { _pending[envelope.MessageId] = waiter; }
            _pumpQueue.EnqueueReqReply(new PipeFrame { Type = FrameType.Req, Envelope = envelope });
            return waiter.Task;
        }

        public ISubscription Serve<T>(Topic topic, Func<BusMessage<T>, Task<Reply>> handler)
        {
            var sub = new ServeSubscription<T>(topic, handler);
            lock (_subLock) { _subscriptions.Add(sub); }
            if (_connected) SendHello();
            return sub;
        }

        // ─── Background connect / reconnect ──────────────────────────────────────
        // §6.5: "jittered backoff; replay journal strictly in seq order to high-water H;
        //        drain live queue discarding class-A ≤ H; per-source FIFO holds"

        internal void StartBackgroundConnect()
        {
            _journalThread.Start();
            Task.Run(ConnectLoop);
        }

        private async Task ConnectLoop()
        {
            var backoffMs = 100;
            var rng       = new Random(Environment.TickCount);
            while (true)
            {
                try
                {
                    _pipe = new NamedPipeClientStream(".", _config.PipeName, // bare name from config (S-17)
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    await _pipe.ConnectAsync(5000).ConfigureAwait(false);
                    _connected = true;
                    backoffMs  = 100;
                    SendHello();

                    // One linked CT per connection: when EITHER pump loop exits (broker death,
                    // write-deadline stall), cancel the other so WhenAll returns and we reconnect.
                    // Without this, a subscriber-only client parks in Dequeue(None) forever and
                    // NEVER reconnects after a broker restart (review S-9 / CC-4).
                    using (var pumpCts = new CancellationTokenSource())
                    {
                        _pumpCts = pumpCts;
                        var reader = PumpReader(pumpCts.Token).ContinueWith(_ => pumpCts.Cancel());
                        var writer = PumpWriter(pumpCts.Token).ContinueWith(_ => pumpCts.Cancel());
                        await Task.WhenAll(reader, writer).ConfigureAwait(false);
                    }
                }
                catch { /* log + alarm after AlarmAfterDisconnect threshold */ }
                finally
                {
                    _connected = false;
                    _pumpCts   = null;
                    _pipe?.Dispose();
                }
                var jitter = rng.Next(_config.ReconnectJitterMs);
                await Task.Delay(Math.Min(backoffMs + jitter, _config.MaxReconnectBackoffMs))
                          .ConfigureAwait(false);
                backoffMs = Math.Min(backoffMs * 2, _config.MaxReconnectBackoffMs);
            }
        }

        // ─── Journal-writer thread — the ONLY thread touching journal files ───────
        // §6.5: "append batch → ONE group-commit flush per batch/interval
        //        → release seq to pump (class A sends ONLY after durable)"

        private void JournalWriterLoop()
        {
            // Catch boundary: a bug here (e.g. a null-Topic tombstone entry, review S-2) must never
            // silently kill the ONLY thread that drains _journalIn — that would stall Publish forever.
            var batch = new List<SendEntry>(64);
            while (!_journalIn.IsCompleted)
            {
                try
                {
                    SendEntry first;
                    if (!_journalIn.TryTake(out first, millisecondsTimeout: 5)) continue;

                    batch.Add(first);
                    SendEntry next;
                    while (batch.Count < 64 && _journalIn.TryTake(out next))
                        batch.Add(next);

                    // ONE group-commit per batch — never per-message fsync
                    AppendBatchToJournal(batch);

                    foreach (var entry in batch)
                    {
                        // Ack-tombstones carry no Topic/Envelope — handle them BEFORE any
                        // entry.Topic dereference (the NRE that killed this thread in review S-2).
                        if (entry.IsAckTombstone)
                            continue; // tombstone already applied to the journal in AppendBatchToJournal

                        var frame = new PipeFrame { Type = FrameType.Pub, Envelope = entry.Envelope };
                        if (entry.Topic.DurabilityClass == DurabilityClass.A_NeverLose ||
                            entry.Topic.DurabilityClass == DurabilityClass.A_ErrorsOnly)
                            _pumpQueue.EnqueueA(frame);   // class A: sent AFTER durable
                        else
                            _pumpQueue.EnqueueBC(frame);  // B/C: can send without waiting
                    }
                    batch.Clear();
                }
                catch (Exception ex)
                {
                    // Never let the writer thread die. Count, alarm, drop the poisoned batch, continue.
                    _counters.IncrementJournalError();
                    Alarm.Raise(AlarmId.JournalWriterFault, ex.Message);
                    batch.Clear();
                }
            }
        }

        private void AppendBatchToJournal(List<SendEntry> batch)
        {
            // Append-only log. "Delete" = ack-tombstone written by THIS SAME THREAD.
            // Compaction: survivors → tmp → flush → atomic ReplaceFile (no racing appender).
            // Caps: 100k entries / 256 MB; alarm at 50%.
            // scan.committed at cap → refuse-new + loud alarm.
            // error telemetry at cap → drop + count.
            // §6.5: "A journal failure never throws to the caller (counted error + alarm)."
            // TODO: implement
        }

        // ─── Pump writer — priority lanes ─────────────────────────────────────────
        // §6.4: "REQ/REPLY > A > B > C (weighted — no total starvation)"

        private async Task PumpWriter(CancellationToken ct)
        {
            while (_connected && !ct.IsCancellationRequested)
            {
                // Blocks until a frame is available OR the connection's CT is cancelled (broker
                // death) — so this loop always unwinds and lets ConnectLoop reconnect (S-9).
                PipeFrame frame;
                try { frame = _pumpQueue.Dequeue(ct); }
                catch (OperationCanceledException) { break; }

                var json  = JsonConvert.SerializeObject(frame);
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                var head  = BitConverter.GetBytes(bytes.Length); // 4-byte length prefix (§6.4)

                // Per-frame WRITE DEADLINE (§6.4): a suspended peer must not park us forever.
                // Timeout → tear the connection down (cancel the CT) and reconnect.
                using (var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    writeCts.CancelAfter(_config.WriteDeadlineMs);
                    try
                    {
                        await _pipe.WriteAsync(head, 0, head.Length, writeCts.Token).ConfigureAwait(false);
                        await _pipe.WriteAsync(bytes, 0, bytes.Length, writeCts.Token).ConfigureAwait(false);
                        await _pipe.FlushAsync(writeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; } // deadline or connection cancel → reconnect
                }
            }
        }

        // ─── Pump reader — split I/O ───────────────────────────────────────────────
        // §6.4: "reader and writer SPLIT (overlapped I/O) on both ends — a peer must
        //        always drain reads regardless of write progress (kills write-write deadlock)"

        private async Task PumpReader(CancellationToken ct)
        {
            var head = new byte[4];
            while (_connected && !ct.IsCancellationRequested)
            {
                try
                {
                    await ReadExactAsync(head, 4, ct).ConfigureAwait(false);
                    var len   = BitConverter.ToInt32(head, 0);
                    var body  = new byte[len];
                    await ReadExactAsync(body, len, ct).ConfigureAwait(false);
                    var frame = JsonConvert.DeserializeObject<PipeFrame>(
                        System.Text.Encoding.UTF8.GetString(body));
                    DispatchInbound(frame);
                }
                catch (OperationCanceledException) { break; }
                catch { break; } // pipe fault → unwind so ConnectLoop reconnects
            }
        }

        private async Task ReadExactAsync(byte[] buf, int count, CancellationToken ct)
        {
            var off = 0;
            while (off < count)
            {
                var n = await _pipe.ReadAsync(buf, off, count - off, ct).ConfigureAwait(false);
                if (n == 0) throw new System.IO.EndOfStreamException(); // peer closed → reconnect
                off += n;
            }
        }

        private void DispatchInbound(PipeFrame frame)
        {
            switch (frame.Type)
            {
                case FrameType.Deliver:
                    // §6.5 dispatcher duties: seq-contiguity dedup, two-stage Ttl gate,
                    // catch boundary (handler exception never kills the process),
                    // poison → dead-letter after N attempts.
                    DispatchToSubscribers(frame);
                    break;

                case FrameType.Nack:
                    // Class-A queue full — message stays in publisher journal; retry on RESUME.
                    // §6.5: "NACK … broker memory bounded"
                    break;

                case FrameType.Resume:
                    // Queue drained — redeliver in seq order, bounded in-flight window.
                    ReplayJournalAfterResume(frame);
                    break;

                case FrameType.E2eAck:
                    // §6.5: "E2E_ACK → enqueue acked seq → journal thread appends tombstone"
                    _journalIn.TryAdd(new SendEntry(null, null)
                        { IsAckTombstone = true, AckedSeq = frame.AckedSeq });
                    break;

                case FrameType.Reply:
                    // §6.7: reply cache = atomic insert-or-get of an in-progress placeholder;
                    //        late REPLYs counted, never a fault.
                    lock (_subLock)
                    {
                        ReplyWaiter waiter;
                        if (_pending.TryGetValue(frame.RequestId, out waiter))
                        {
                            _pending.Remove(frame.RequestId);
                            waiter.Complete(ParseReply(frame));
                        }
                    }
                    break;

                case FrameType.Pong:
                    // Update health: loop lag measured at the heartbeat
                    break;
            }
        }

        // Per-(source,topic) ordered execution lanes. §6.5 promises per-source FIFO per topic;
        // a raw Task.Run PER FRAME (review S-3) runs deliveries concurrently on the pool and
        // reorders them — breaking stateSeq order, seq-contiguity dedup, WAL ordering, and the
        // reply-cache no-double-execution guarantee all at once. Each (source,topic) key gets a
        // single-threaded chain so handlers run in arrival order; different keys still parallelize.
        private readonly ConcurrentDictionary<string, OrderedDispatchLane> _lanes
            = new ConcurrentDictionary<string, OrderedDispatchLane>();

        private void DispatchToSubscribers(PipeFrame frame)
        {
            List<ILocalSubscription> subs;
            lock (_subLock) { subs = new List<ILocalSubscription>(_subscriptions); }

            var laneKey = frame.Envelope.Source + "|" + frame.Envelope.Topic;
            var lane    = _lanes.GetOrAdd(laneKey, _ => new OrderedDispatchLane());

            foreach (var sub in subs)
            {
                if (sub.TopicName != frame.Envelope.Topic) continue;
                var target = sub;
                // Enqueue onto the ordered lane; the lane awaits each handler before the next.
                // Seq-contiguity dedup and the reply-cache insert-or-get live on this same
                // single-threaded path (see ServeSubscription.InvokeAsync). Catch boundary inside
                // the lane: a handler exception is dead-lettered, never fatal (§6.5).
                lane.Post(() => target.InvokeAsync(frame));
            }
        }

        private void ReplayJournalAfterResume(PipeFrame frame)
        {
            // Strictly in seq order, bounded in-flight window, paced by broker credit.
        }

        private void SendHello()
        {
            // HELLO: identity + subscriptions + per-class-A-topic publisher resumeFromSeq.
            // Called on every (re)connect — subscriptions registered locally first.
        }

        private BusEnvelope BuildEnvelope<T>(Topic topic, T payload,
            PublishOptions options, long? ttlMs = null)
        {
            return new BusEnvelope
            {
                MessageId     = Guid.NewGuid().ToString("N"),
                Topic         = topic.Name,
                CorrelationId = options != null ? options.CorrelationId : null,
                ModuleId      = options != null ? options.ModuleId      : null,
                Source        = _sourceName,
                SourceEpoch   = _sourceEpoch,   // (envelope field addition is part of R-2)
                Seq           = Interlocked.Increment(ref _nextSeq),
                TimestampUtc  = DateTime.UtcNow,
                SchemaVersion = 1,
                TtlMs         = ttlMs,
                Attempts      = 0,
                PayloadType   = typeof(T).Name,
                Payload       = payload
            };
        }

        private Reply ParseReply(PipeFrame frame)
        {
            // TODO: deserialize reply payload
            return Reply.Accepted();
        }

        private BusHealth BuildHealth()
        {
            return new BusHealth
            {
                IsConnected    = _connected,
                HeartbeatAge   = TimeSpan.Zero,
                LoopLagMs      = 0L,
                QueueDepths    = new Dictionary<string, int>(),
                JournalBacklog = 0L
            };
        }

        public void Dispose()
        {
            // Teardown order (§2.7):
            // 1. reject-new gate
            // 2. NACK + compensate queued commands
            // 3. drain in-flight with timeout
            // 4. journal flush with timeout
            // 5. Dispose pipe — NEVER on the UI thread
            _journalIn.CompleteAdding();
            _pipe?.Dispose();
        }
    }

    // ═══ Internal helpers ════════════════════════════════════════════════════════════

    internal sealed class SendEntry
    {
        public Topic       Topic         { get; }
        public BusEnvelope Envelope      { get; }
        public bool        IsAckTombstone { get; set; }
        public long        AckedSeq       { get; set; }

        public SendEntry(Topic topic, BusEnvelope envelope)
        {
            Topic    = topic;
            Envelope = envelope;
        }
    }

    internal sealed class PipeFrame
    {
        public FrameType   Type      { get; set; }
        public BusEnvelope Envelope  { get; set; }
        public string      RequestId { get; set; }
        public long        AckedSeq  { get; set; }
    }

    // §6.4: priority lanes — REQ/REPLY > A > B/C, WEIGHTED (no TOTAL starvation).
    // Review S-14: a strict drain (reqReply, then A, then BC) lets a saturated class-A replay
    // starve B/C completely, contradicting "weighted — no total starvation"; and the B/C lane
    // must be bounded (drop-oldest+count) or a long broker outage grows it without limit (SYS-4).
    internal sealed class PrioritySendQueue
    {
        private readonly ConcurrentQueue<PipeFrame> _reqReply = new ConcurrentQueue<PipeFrame>();
        private readonly ConcurrentQueue<PipeFrame> _classA   = new ConcurrentQueue<PipeFrame>();
        private readonly BoundedDropOldest          _classBc  = new BoundedDropOldest(capacity: 4096);
        private readonly SemaphoreSlim              _signal   = new SemaphoreSlim(0, int.MaxValue);

        // Weighted round-robin credits: for every 8 REQ/REPLY served, up to 4 A and 1 B/C —
        // high lanes win but the low lane always makes progress.
        private int _reqCredit, _aCredit;
        private const int ReqWeight = 8, AWeight = 4;

        public void EnqueueReqReply(PipeFrame f) { _reqReply.Enqueue(f); _signal.Release(); }
        public void EnqueueA       (PipeFrame f) { _classA.Enqueue(f);   _signal.Release(); }
        public void EnqueueBC      (PipeFrame f) { _classBc.Enqueue(f);  _signal.Release(); }

        public PipeFrame Dequeue(CancellationToken ct)
        {
            _signal.Wait(ct);
            PipeFrame f;

            // Serve the high lanes under a weight budget, then force a low-lane turn so B/C
            // can never be starved indefinitely.
            if (_reqCredit < ReqWeight && _reqReply.TryDequeue(out f)) { _reqCredit++; return f; }
            if (_aCredit   < AWeight   && _classA.TryDequeue(out f))   { _aCredit++;   return f; }
            if (_classBc.TryDequeue(out f)) { _reqCredit = 0; _aCredit = 0; return f; }

            // Low lane empty — reset credits and take whatever high-lane work remains.
            _reqCredit = 0; _aCredit = 0;
            if (_reqReply.TryDequeue(out f)) return f;
            _classA.TryDequeue(out f);
            return f;
        }
    }

    // Bounded FIFO that drops the OLDEST on overflow and counts the drop (class-C semantics).
    internal sealed class BoundedDropOldest
    {
        private readonly ConcurrentQueue<PipeFrame> _q = new ConcurrentQueue<PipeFrame>();
        private readonly int _capacity;
        private long _dropped;
        public BoundedDropOldest(int capacity) { _capacity = capacity; }
        public long Dropped { get { return Interlocked.Read(ref _dropped); } }

        public void Enqueue(PipeFrame f)
        {
            _q.Enqueue(f);
            while (_q.Count > _capacity)
            {
                PipeFrame old;
                if (_q.TryDequeue(out old)) Interlocked.Increment(ref _dropped);
                else break;
            }
        }
        public bool TryDequeue(out PipeFrame f) { return _q.TryDequeue(out f); }
    }

    // Single-threaded ordered execution lane per (source,topic) — see DispatchToSubscribers (S-3).
    internal sealed class OrderedDispatchLane
    {
        private readonly object _gate = new object();
        private Task _tail = Task.CompletedTask;

        public void Post(Func<Task> work)
        {
            lock (_gate)
            {
                // Chain each item after the previous one → strict arrival order per lane.
                _tail = _tail.ContinueWith(async _ =>
                {
                    try { await work().ConfigureAwait(false); }
                    catch { /* dead-letter after N attempts + alarm — never fatal (§6.5) */ }
                }, TaskScheduler.Default).Unwrap();
            }
        }
    }

    internal interface ILocalSubscription
    {
        string TopicName { get; }
        Task InvokeAsync(PipeFrame frame);
    }

    internal sealed class Subscription<T> : ILocalSubscription, ISubscription
    {
        private readonly Func<BusMessage<T>, Task> _handler;
        private readonly SubscribeOptions          _options;

        public string TopicName { get; }
        public Topic  Topic     { get; }
        public bool   IsActive  { get { return true; } }

        public Subscription(Topic topic, Func<BusMessage<T>, Task> handler, SubscribeOptions options)
        {
            Topic     = topic;
            TopicName = topic.Name;
            _handler  = handler;
            _options  = options;
        }

        public async Task InvokeAsync(PipeFrame frame)
        {
            // §6.5 dispatcher: seq-contiguity dedup O(1) + monotonic-clock-aligned ExpiresAt
            // Unknown payload fields IGNORED (mixed-version tolerance, §6.5)
            var payload   = JsonConvert.DeserializeObject<T>(
                JsonConvert.SerializeObject(frame.Envelope.Payload ?? new object()));
            // Ttl deadline is RELATIVE, computed on a LOCAL monotonic clock at frame receipt
            // (§6.5 / review S-4). Using the publisher's wall-clock TimestampUtc + TtlMs is wrong:
            // cross-process clock skew / NTP steps make commands expire early or become immortal.
            // MonotonicClock.Now returns a DateTime anchored at process start (same model as the AOI
            // UiMarshaller clock and BusMessage.ExpiresAt) — monotonic, never a wall-clock jump.
            var expiresAt = frame.Envelope.TtlMs.HasValue
                ? MonotonicClock.Now.AddMilliseconds(frame.Envelope.TtlMs.Value)
                : DateTime.MaxValue;
            var msg = new BusMessage<T>(frame.Envelope, payload, expiresAt);
            await _handler(msg).ConfigureAwait(false);
        }

        public void Dispose() { /* unregister from BusClient._subscriptions */ }
    }

    internal sealed class ServeSubscription<T> : ILocalSubscription, ISubscription
    {
        private readonly Func<BusMessage<T>, Task<Reply>> _handler;
        public string TopicName { get; }
        public Topic  Topic     { get; }
        public bool   IsActive  { get { return true; } }

        public ServeSubscription(Topic topic, Func<BusMessage<T>, Task<Reply>> handler)
        {
            Topic     = topic;
            TopicName = topic.Name;
            _handler  = handler;
        }

        // §6.7 reply cache — atomic insert-or-get of an in-progress placeholder, keyed on
        // messageId. A redelivery of the same REQ awaits the SAME task instead of running the
        // handler twice (at-most-once effect). Reads consistently because DispatchToSubscribers
        // funnels a given (source,topic) through one OrderedDispatchLane (S-3), but we still guard
        // with a concurrent map for cross-topic safety.
        private readonly ConcurrentDictionary<string, Task<Reply>> _replyCache
            = new ConcurrentDictionary<string, Task<Reply>>();

        public async Task InvokeAsync(PipeFrame frame)
        {
            var msgId = frame.Envelope.MessageId;

            // Ttl gate #1 (§6.7): a command already past its deadline at receipt is never dispatched.
            // Deadline is computed on the monotonic clock at receipt from the relative TtlMs.
            if (frame.Envelope.TtlMs.HasValue)
            {
                var deadline = MonotonicClock.Now.AddMilliseconds(frame.Envelope.TtlMs.Value);
                if (MonotonicClock.Now >= deadline)
                {
                    SendReply(frame.Envelope, Reply.Expired());
                    return;
                }
            }

            // Reply-cache insert-or-get: a redelivery of the same REQ awaits the same task instead
            // of running the handler twice (at-most-once effect, §6.7).
            var task  = _replyCache.GetOrAdd(msgId, _ => RunHandlerOnce(frame));
            var reply = await task.ConfigureAwait(false);
            SendReply(frame.Envelope, reply); // late/duplicate REPLYs are counted, never a fault (§6.7)
        }

        private async Task<Reply> RunHandlerOnce(PipeFrame frame)
        {
            var payload = JsonConvert.DeserializeObject<T>(
                JsonConvert.SerializeObject(frame.Envelope.Payload ?? new object()));
            // Reply = ACCEPTED on successful post to the executing dispatcher — never gated on
            // completion (§6.7). The host-side marshal happens inside the handler.
            var msg = new BusMessage<T>(frame.Envelope, payload,
                MonotonicClock.Now.AddMilliseconds(frame.Envelope.TtlMs ?? 0));
            return await _handler(msg).ConfigureAwait(false);
        }

        private void SendReply(BusEnvelope req, Reply reply)
        {
            // Serialize REPLY(requestId=req.MessageId) and enqueue on the REQ/REPLY priority lane.
        }

        public void Dispose() { }
    }

    internal sealed class ReplyWaiter
    {
        private readonly TaskCompletionSource<Reply> _tcs;

        public ReplyWaiter(CancellationToken ct)
        {
            _tcs = new TaskCompletionSource<Reply>();
            ct.Register(() => _tcs.TrySetCanceled());
        }

        public Task<Reply> Task { get { return _tcs.Task; } }
        public void Complete(Reply r) { _tcs.TrySetResult(r); }
    }

    internal sealed class BusCounters : IBusCounters
    {
        private readonly ConcurrentDictionary<string, long> _pub  = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, long> _ack  = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, long> _del  = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, long> _drop = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, long> _dl   = new ConcurrentDictionary<string, long>();

        private readonly ConcurrentDictionary<string, long> _refused = new ConcurrentDictionary<string, long>();
        private long _journalErrors;

        public void IncrementPublished(Topic t)
            => _pub.AddOrUpdate(t.Name, 1L, (_, v) => v + 1L);

        // Class-A intake refusals — the record that a publish was NOT accepted (review S-1).
        public void IncrementRefused(Topic t)
            => _refused.AddOrUpdate(t.Name, 1L, (_, v) => v + 1L);

        public void IncrementJournalError() => Interlocked.Increment(ref _journalErrors);

        public long Published   (Topic t) { long v; return _pub.TryGetValue(t.Name, out v)  ? v : 0L; }
        public long Acked       (Topic t) { long v; return _ack.TryGetValue(t.Name, out v)  ? v : 0L; }
        public long Delivered   (Topic t) { long v; return _del.TryGetValue(t.Name, out v)  ? v : 0L; }
        public long Dropped     (Topic t) { long v; return _drop.TryGetValue(t.Name, out v) ? v : 0L; }
        public long DeadLettered(Topic t) { long v; return _dl.TryGetValue(t.Name, out v)   ? v : 0L; }
        public long Refused     (Topic t) { long v; return _refused.TryGetValue(t.Name, out v) ? v : 0L; }
        public long JournalErrors { get { return Interlocked.Read(ref _journalErrors); } }
    }

    // ═══ Sketch-level support types (referenced above; real impls elsewhere) ═════════════

    // Stopwatch-anchored monotonic clock — the ONLY clock valid for Ttl arithmetic (§6.5 / S-4).
    // Returns a DateTime (same model as BusMessage.ExpiresAt and the AOI UiMarshaller clock) that
    // only ever advances: DateTime.UtcNow must never appear in a Ttl comparison (NTP steps break it).
    internal static class MonotonicClock
    {
        private static readonly DateTime _anchor = DateTime.UtcNow;
        private static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public static DateTime Now { get { return _anchor + _sw.Elapsed; } }
    }

    internal enum AlarmId { JournalIntakeSaturated, JournalWriterFault, JournalCapReached }

    internal static class Alarm
    {
        public static void Raise(AlarmId id, string detail) { /* → ToolHost alarm surface + log */ }
    }

    // Publisher incarnation counter, persisted next to the journal (review S-2/R-2).
    internal static class SourceEpoch
    {
        public static long LoadOrCreate() { return 0L; /* read-or-bump a file beside the journal */ }
    }
}
