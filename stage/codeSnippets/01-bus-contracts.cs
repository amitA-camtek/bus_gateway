// Realizes: §1.4 cross-cutting contracts, §6.2 topic registry, §6.3 envelope, §03-lanes Lane-A
// Project: Camtek.Messaging.Contracts (net48;net8.0)
// Purpose: Envelope, topic registry (class + ACL + storm), payload DTOs.
//          NO logic — this assembly is pure data contracts shared by ALL processes.
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using System.Collections.Generic;

namespace Camtek.Messaging.Contracts
{
    // ═══ Durability classes ═══════════════════════════════════════════════════════════
    // §1.4: A = never-lose · B = latest-wins retained · C = best-effort counted drops
    //       R-R = commands (Ttl + dequeue-gate + reply cache, at-most-once effect)

    public enum DurabilityClass
    {
        /// <summary>Journal + WAL + subscriber-set E2E ack. scan.committed, error telemetry.</summary>
        A_NeverLose   = 0,
        /// <summary>Same wire as A, plus storm coalescing. tool.telemetry.</summary>
        A_ErrorsOnly  = 1,
        /// <summary>Latest-wins, retained — new subscribers see last value on subscribe. tool.state.</summary>
        B_Retained    = 2,
        /// <summary>Drop-oldest, counted. scan.announced, loader.events, scan.operations.</summary>
        C_BestEffort  = 3,
        /// <summary>Ttl mandatory, at-most-once effect. gui.commands, tool.commands.</summary>
        R_RequestReply = 4
    }

    // ═══ Publish ACL ════════════════════════════════════════════════════════════════
    // §1.4: "*.commands = GEM shim + gateway only"
    // §6.8: enforced at the library/broker boundary, not in review comments

    [Flags]
    public enum Acl
    {
        None        = 0,
        AoiMain     = 1 << 0,
        ToolManager = 1 << 1,
        GemShim     = 1 << 2,
        Gateway     = 1 << 3,
        EfemServer  = 1 << 4,
        Any         = ~0
    }

    // ═══ Storm control ══════════════════════════════════════════════════════════════
    // §6.5: "error-class telemetry coalesced by (source, errorCode) + token bucket"
    // §1.4: "a flapping sensor costs summaries, not 300k journaled messages"

    public sealed class StormControl
    {
        public static readonly StormControl None = new StormControl();

        public Func<object, (string source, string errorCode)> KeySelector { get; private set; }
        public bool     FirstImmediate  { get; private set; }
        public TimeSpan SummaryInterval { get; private set; }
        public int      TokensPerSecond { get; private set; }
        public int      BurstCapacity   { get; private set; }

        public static StormControl CoalesceByKey<T>(
            Func<T, (string source, string errorCode)> key,
            bool    firstImmediate,
            TimeSpan summaryEvery,
            (int perSecond, int burst) tokenBucket)
        {
            return new StormControl
            {
                KeySelector     = o => key((T)o),
                FirstImmediate  = firstImmediate,
                SummaryInterval = summaryEvery,
                TokensPerSecond = tokenBucket.perSecond,
                BurstCapacity   = tokenBucket.burst
            };
        }

        private StormControl() { }
    }

    public static class Rate
    {
        public static (int perSecond, int burst) PerSecond(int rate, int burst)
            => (rate, burst);
    }

    // ═══ Topic — the compile-time registry ══════════════════════════════════════════
    // §6.2: "Topics are declared, not stringly-typed — durability class, payload type,
    //        publish ACL, and storm control are compile-time properties of the topic"

    public sealed class Topic
    {
        public string         Name            { get; private set; }
        public DurabilityClass DurabilityClass { get; private set; }
        public Type           PayloadType     { get; private set; }
        public Acl            Publishers      { get; private set; }
        public StormControl   StormControl    { get; private set; }

        private Topic() { }

        public static Topic Define(string name, DurabilityClass cls, Type payloadType,
            Acl publishers = Acl.Any, StormControl stormControl = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException("name");
            return new Topic
            {
                Name            = name,
                DurabilityClass = cls,
                PayloadType     = payloadType,
                Publishers      = publishers,
                StormControl    = stormControl ?? StormControl.None
            };
        }

        public override string ToString() => Name;
    }

    // ═══ Topic registry — 9 topics at P1a; +2 at P2–P3; machine.* reserved (review D-1/CON-2) ═══
    // §1.3.1 View 3: "9 registered topics" is the P1a set. The two P2/P2–P3 topics below MUST be
    // registered (class + payload + ACL) before their edge is planned, or the P2 design has no
    // contract to publish to.

    public static class Topics
    {
        // --- Lane A (BUS) ---

        /// <summary>C: identifiers only — no file paths (§1.4 structural safety rule).</summary>
        public static readonly Topic ScanAnnounced = Topic.Define(
            "scan.announced", DurabilityClass.C_BestEffort, typeof(ScanAnnouncedPayload),
            publishers: Acl.AoiMain);

        /// <summary>A: never-lose, journaled + WAL + E2E ack. Carries stable final path.</summary>
        public static readonly Topic ScanCommitted = Topic.Define(
            "scan.committed", DurabilityClass.A_NeverLose, typeof(ScanCommittedPayload),
            publishers: Acl.AoiMain);

        /// <summary>C: per-recipe scan operations (phase, focus, etc.).</summary>
        public static readonly Topic ScanOperations = Topic.Define(
            "scan.operations", DurabilityClass.C_BestEffort, typeof(ScanOperationsPayload),
            publishers: Acl.AoiMain);

        /// <summary>B: retained — new subscribers see current state immediately. stateSeq in payload.</summary>
        public static readonly Topic ToolState = Topic.Define(
            "tool.state", DurabilityClass.B_Retained, typeof(ToolStatePayload),
            publishers: Acl.ToolManager);

        /// <summary>R-R: GUI command (e.g. StartManualScan). ACL restricted to GEM shim + gateway.</summary>
        public static readonly Topic GuiCommands = Topic.Define(
            "gui.commands", DurabilityClass.R_RequestReply, typeof(GuiCommandPayload),
            publishers: Acl.GemShim | Acl.Gateway);

        /// <summary>R-R: ToolManager command (P5, per customer). ACL: GEM shim + gateway only.</summary>
        public static readonly Topic ToolCommands = Topic.Define(
            "tool.commands", DurabilityClass.R_RequestReply, typeof(ToolCommandPayload),
            publishers: Acl.GemShim | Acl.Gateway);

        /// <summary>C: EFEM wafer / carrier events (P2).</summary>
        public static readonly Topic LoaderEvents = Topic.Define(
            "loader.events", DurabilityClass.C_BestEffort, typeof(LoaderEventPayload),
            publishers: Acl.EfemServer);

        /// <summary>B: retained — carrier + production state (P5).</summary>
        public static readonly Topic ProductionCarrier = Topic.Define(
            "production.carrier", DurabilityClass.B_Retained, typeof(ProductionCarrierPayload),
            publishers: Acl.ToolManager);

        /// <summary>A-ErrorsOnly with storm coalescing — reproduced exactly from §03-lanes Lane-A.</summary>
        public static readonly Topic ToolTelemetry = Topic.Define(
            "tool.telemetry",
            DurabilityClass.A_ErrorsOnly,
            typeof(TelemetryPayload),
            publishers: Acl.AoiMain | Acl.ToolManager,
            stormControl: StormControl.CoalesceByKey<TelemetryPayload>(
                key:           t => (t.Source, t.ErrorCode),
                firstImmediate: true,
                summaryEvery:  TimeSpan.FromSeconds(10),
                tokenBucket:   Rate.PerSecond(10, burst: 100)));

        // --- P2 / P2–P3 additions (review D-1/CON-2) — registered here so the P2 edge has a contract ---

        /// <summary>R-R: the 3 ref-returning Fire* ops (P2). ACL: AOI republishes.</summary>
        public static readonly Topic ScanOperationsRequests = Topic.Define(
            "scan.operations.requests", DurabilityClass.R_RequestReply, typeof(ScanOperationsPayload),
            publishers: Acl.AoiMain);

        /// <summary>C: DDS-node status republished by AOI (P2–P3). RATE UNMEASURED — P0 must measure
        /// PizzasConnectionStatus emit period before this is sized (review LD-6).</summary>
        public static readonly Topic ScanDdsNodeStatus = Topic.Define(
            "scan.dds-node-status", DurabilityClass.C_BestEffort, typeof(ScanOperationsPayload),
            publishers: Acl.AoiMain);

        // machine.efem.state / machine.safety.alarm — RESERVED names for the machine layer's own
        // adoption program (gated on the multi-PC census A-1); intentionally NOT defined yet.
    }

    // ═══ Wire envelope ══════════════════════════════════════════════════════════════
    // §6.3: all fields reproduced exactly; schemaVersion = additive-only, ignore-unknown

    public sealed class BusEnvelope
    {
        public string   MessageId     { get; set; }   // GUID — R-R dedup key
        public string   Topic         { get; set; }
        public string   CorrelationId { get; set; }   // UnifiedLogger-aligned tracing
        public string   ModuleId      { get; set; }
        public string   Source        { get; set; }   // identity from HELLO
        public long     SourceEpoch   { get; set; }   // publisher incarnation (review S-2/R-2): dedup
                                                       // key = (Source, SourceEpoch, Topic, Seq) so a
                                                       // journal reset can't make fresh msgs look dup
        public long     Seq           { get; set; }   // per-(source,epoch) monotonic — ordering, dedup
        public DateTime TimestampUtc  { get; set; }
        public int      SchemaVersion { get; set; } = 1; // additive-only; ignore-unknown
        public long?    TtlMs         { get; set; }
        public int      Attempts      { get; set; }   // poison detection
        public string   PayloadType   { get; set; }
        public object   Payload       { get; set; }   // deserialized by the client into PayloadType
    }

    // ═══ Received message wrapper ═══════════════════════════════════════════════════

    public sealed class BusMessage<T>
    {
        public BusEnvelope Envelope  { get; }
        public T           Payload   { get; }
        /// <summary>Pre-computed on receive; compared against MonotonicClock.Now at the Ttl gates.</summary>
        public DateTime    ExpiresAt { get; }

        public BusMessage(BusEnvelope envelope, T payload, DateTime expiresAt)
        {
            Envelope  = envelope;
            Payload   = payload;
            ExpiresAt = expiresAt;
        }
    }

    // ═══ Payload DTOs ═══════════════════════════════════════════════════════════════
    // Only fields named in the design are included; add others via an additive schema version.

    /// <summary>§1.4: carries NO file paths — a mis-wired consumer cannot read half-copied files.</summary>
    public sealed class ScanAnnouncedPayload
    {
        public string WaferId       { get; set; }
        public string LotId         { get; set; }
        public int    Slot          { get; set; }
        public string CorrelationId { get; set; }
    }

    /// <summary>Carries the stable final path — ONLY after CopyScanResults (§2.6).</summary>
    public sealed class ScanCommittedPayload
    {
        public string WaferId       { get; set; }
        public string LotId         { get; set; }
        public int    Slot          { get; set; }
        public string ResultsPath   { get; set; }
        public string CorrelationId { get; set; }
    }

    public sealed class ToolStatePayload
    {
        public ToolStateEnum State    { get; set; }
        /// <summary>Stamped inside a ToolManager transition-commit lock to be introduced (R-8; §03-lanes P3).</summary>
        public long          StateSeq { get; set; }
        public string        Reason   { get; set; }
    }

    public enum ToolStateEnum
    {
        NotInitialized,
        Initialization,
        Engineering,
        EngineeringToProduction,
        Production
    }

    public sealed class GuiCommandPayload
    {
        public string                     Command    { get; set; }
        public string                     RequestId  { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
    }

    public sealed class ToolCommandPayload
    {
        public string                     Command    { get; set; }
        public string                     RequestId  { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
    }

    public sealed class TelemetryPayload
    {
        public string   Source          { get; set; }
        public string   ErrorCode       { get; set; }
        public string   Message         { get; set; }
        public string   CorrelationId   { get; set; }
        public DateTime TimestampUtc    { get; set; }
        public bool     IsSummary       { get; set; }
        public int      SuppressedCount { get; set; }
    }

    public sealed class LoaderEventPayload
    {
        public string EventType { get; set; }  // e.g. WaferTypeLoaded, CassetteLoaded
        public string WaferId   { get; set; }
        public int    Slot      { get; set; }
    }

    public sealed class ScanOperationsPayload
    {
        public string Operation     { get; set; }
        public string WaferId       { get; set; }
        public string CorrelationId { get; set; }
    }

    public sealed class ProductionCarrierPayload
    {
        public string CarrierId { get; set; }
        public string State     { get; set; }
        public long   StateSeq  { get; set; }
    }
}
