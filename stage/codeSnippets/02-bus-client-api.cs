// Realizes: §6.2 public API (reproduced exactly)
// Project: Camtek.Messaging (net48;net8.0)
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Camtek.Messaging.Contracts;

namespace Camtek.Messaging
{
    // ═══ Reply — command result ══════════════════════════════════════════════════════
    // §6.7: "Reply = ACCEPTED on successful post to the executing dispatcher"
    // NOT execution completion — the final Ttl gate runs on the UI thread first.

    public sealed class Reply
    {
        public bool   IsAccepted { get; private set; }
        public string Reason     { get; private set; }

        private Reply() { }

        public static Reply Accepted()           => new Reply { IsAccepted = true };
        public static Reply Expired()            => new Reply { IsAccepted = false, Reason = "expired" };
        public static Reply Rejected(string why) => new Reply { IsAccepted = false, Reason = why };
        public static Reply RejectedBusy()       => new Reply { IsAccepted = false, Reason = "busy" };
    }

    // ═══ Options ════════════════════════════════════════════════════════════════════

    public sealed class PublishOptions
    {
        public string CorrelationId { get; set; }
        public string ModuleId      { get; set; }
    }

    public sealed class SubscribeOptions
    {
        public long ResumeFromSeq { get; set; } = 0L; // journal replay start (class A)
    }

    // ═══ Health and counters ════════════════════════════════════════════════════════
    // §6.2: BusHealth { connected, heartbeat age, queue depths, journal backlog }

    public sealed class BusHealth
    {
        // IsConnected is the COMPOSITE signal (pipe connected AND heartbeat fresh AND loop-lag < L),
        // not the raw socket flag — a hung broker holds the pipe open (review CN-9/S-9).
        public bool                    IsConnected    { get; set; }
        public TimeSpan                HeartbeatAge   { get; set; }
        public long                    LoopLagMs      { get; set; }
        public Dictionary<string, int> QueueDepths    { get; set; }
        public long                    JournalBacklog { get; set; }
        // Class-A publishes refused at the bounded intake / journal cap (review S-1). Non-zero =
        // the producer (frmScanTab) must pause at the wafer boundary rather than lose results.
        public long                    RefusedPublishes { get; set; }
    }

    public interface IBusCounters
    {
        long Published   (Topic topic);
        long Acked       (Topic topic);
        long Delivered   (Topic topic);
        long Dropped     (Topic topic);
        long DeadLettered(Topic topic);
    }

    public interface ISubscription : IDisposable
    {
        Topic Topic    { get; }
        bool  IsActive { get; }
    }

    // ═══ IBus — the main contract (reproduced exactly from §6.2) ══════════════════

    public interface IBus : IDisposable
    {
        // ≤1 ms ALWAYS: lock-free enqueue; class-A journaling happens on the
        // library's journal-writer thread BEFORE the pump sends (never on the caller).
        void Publish<T>(Topic topic, T payload, PublishOptions options = null);

        // Handlers run on pool threads; STA/UI marshaling is the HOST's job.
        ISubscription Subscribe<T>(Topic topic, Func<BusMessage<T>, Task> handler,
                                   SubscribeOptions options = null);

        // Commands — untyped reply (ACCEPTED/REJECTED + Reason, for gui.commands / tool.commands).
        Task<Reply> RequestAsync<T>(Topic topic, T payload, TimeSpan ttl,
                                    CancellationToken ct = default(CancellationToken));
        ISubscription Serve<T>(Topic topic, Func<BusMessage<T>, Task<Reply>> handler);

        // Commands — typed reply (for R-R topics with structured response payloads, e.g. tool.state.replay).
        // (C9-1) net48-compatible: explicit type parameters required at call sites in C# 7.3.
        Task<TRes> RequestAsync<TReq, TRes>(Topic topic, TReq payload, TimeSpan ttl,
                                            CancellationToken ct = default(CancellationToken));
        ISubscription Serve<TReq, TRes>(Topic topic, Func<BusMessage<TReq>, Task<TRes>> handler);

        // Broker liveness PING — priority-queued, proves broker answers not just socket open. (C9-3)
        Task<bool> PingAsync(TimeSpan timeout);

        BusHealth    Health   { get; } // connected, heartbeat age, queue depths, journal backlog
        IBusCounters Counters { get; } // per-topic published/acked/delivered/dropped/dead-lettered
    }

    // ═══ BusFactory (reproduced exactly from §6.2) ══════════════════════════════════

    public static class BusFactory
    {
        // NON-blocking: returns immediately, background jittered-backoff retry (infinite,
        // alarm after T). Subscriptions registered locally, replayed on (re)connect.
        public static IBus Connect(string sourceName, BusConfig config = null)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
                throw new ArgumentNullException("sourceName");
            config = config ?? new BusConfig();
            var client = new BusClient(sourceName, config);
            client.StartBackgroundConnect(); // fire-and-forget; never blocks
            return client;
        }
    }

    // ═══ BusConfig ══════════════════════════════════════════════════════════════════

    public sealed class BusConfig
    {
        // §6.4: one duplex named pipe per process, localhost only, pipe-ACL authenticated.
        // BARE pipe name (review S-17): the .NET NamedPipe*Stream APIs take "camtek.bus", NOT the
        // "\\.\pipe\" prefixed form. The client and broker MUST read this value rather than
        // hard-coding the literal, so the endpoint manifest is the single source of truth.
        public string   PipeName              { get; set; } = "camtek.bus";
        // §2.7: "degraded banner ≤ N s" — alarm when disconnected longer than this
        public TimeSpan AlarmAfterDisconnect  { get; set; } = TimeSpan.FromSeconds(30);
        // §6.5 reconnect: jittered backoff
        public int      MaxReconnectBackoffMs { get; set; } = 5000;
        public int      ReconnectJitterMs     { get; set; } = 500;
        // §6.4 per-frame write deadline — a suspended peer must not park the writer (review S-9).
        public int      WriteDeadlineMs       { get; set; } = 2000;
        // §6.5: "journals/spool on the system volume, separate from tile/zip data"
        public string   JournalDirectory      { get; set; } = @"C:\bis\journal";
    }
}
