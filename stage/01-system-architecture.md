# 1 — System Architecture

> Level: **system** (processes on the tool PC and the tool's external connections).
> Up-link: why we change → [00-context-and-case.md](00-context-and-case.md).
> Down-links: AOI_Main internals → [02-aoi-architecture.md](02-aoi-architecture.md) · migration method → [03-appendix-four-lanes.md](03-appendix-four-lanes.md) · project impact → [04-impact-analysis.md](04-impact-analysis.md) · program plan → [05-roadmap-and-risks.md](05-roadmap-and-risks.md) · bus build spec → [06-bus-implementation.md](06-bus-implementation.md).

---

## 1.1 Architecture views

### View 1 — Context (highest level)

The tool has exactly **two doors** — GEM for the factory host, ToolConnect for everything else — one internal **fabric**, and the **machine core** doing the work.

```mermaid
flowchart LR
    HOST["Factory Host"]
    subgraph T["Falcon Tool"]
        GEMD["GEM door\n(SecsGemGui.Net over\nCimetrix driver - wire unchanged)"]
        FAB[["Fabric\nCamtek.Messaging bus"]]
        GWD["Gateway door\nToolConnect"]
        CORE["Machine core\nAOI_Main (GUI + scan engine) ·\nToolManager (state machine) · EFEM"]
    end
    CLOUD["Fleet · TSMC · MES"]

    HOST <--> GEMD
    GEMD <--> FAB
    FAB <--> CORE
    FAB <--> GWD
    GWD <--> CLOUD
```

### View 2 — Process view

```mermaid
flowchart LR
    HOST["Factory Host\nSECS/GEM"]
    FLEET["Fleet.Main (central)"]
    TSMC["TSMC Cloud"]
    MES["MES / analytics"]

    subgraph TOOL["Tool PC"]
        subgraph TH["ToolHost (the ONE Windows service)"]
            BUS[["Bus broker (child)"]]
            GW["ToolConnect gateway (child)"]
            TS["Camtek.ToolServices host (child, :5060)"]
        end
        SGP["SecsGemGui.Net\nGEM logic + bus shim"]
        AOI["AOI_Main (.NET FW 4.8)\nbus client - dials out, never listens"]
        FW["FalconWrapper.exe\nlegacy event hub (frozen facade,\nAOI-side bridge)"]
        TM["ToolManager / ProductionManager\nstate machine + bus shim"]
    end

    HOST <-->|"SECS-II / HSMS"| SGP
    SGP <--> BUS
    AOI <--> BUS
    AOI <--> TS
    TM <--> BUS
    GW <--> BUS
    AOI -.->|"legacy Fire* until migrated"| FW
    GW -->|gRPC| FLEET
    GW -->|"native SDK"| TSMC
    MES <-->|"gRPC/REST :5007"| GW
```

### View 3 — Component view (system altitude)

```mermaid
flowchart TB
    subgraph BRK["Bus broker"]
        TOP[["9 registered topics at P1a (+2 at P2-P3)\nscan.* · tool.* · gui.commands ·\nloader.events · production.carrier"]]
        SUBQ["Per-subscriber bounded queues\nA: NACK / B: coalesce+retained / C: drop+count"]
    end
    subgraph GWC["ToolConnect gateway"]
        BSRC["BusSource (WAL-before-ack)"]
        CMDP["CommandPublisher :5007"]
        SINKS["EventRouter - SinkDispatchers -\nFleetSink · TsmcSink"]
        WAL["WAL spool + dead-letter"]
    end
    subgraph AOIC["AOI_Main (drill-down: doc 02)"]
        BA["BusAdapter"]
        SC["ServiceClients"]
    end
    TMC["ToolManager shim\n(tool.state publisher, stateSeq)"]
    GEMC["GEM shim\n(commands publisher, degraded contract)"]
    THC["ToolHost supervisor\n(job objects, health :5100, counters)"]

    BA <--> TOP
    TMC --> TOP
    GEMC <--> TOP
    TOP --> BSRC --> SINKS
    BSRC --> WAL
    CMDP --> TOP
    THC -.-> BRK
    THC -.-> GWC
```

## 1.2 System communication flows

### Flow SYS-1 — wafer scan results, operator → cloud (class A, zero silent loss)

```mermaid
sequenceDiagram
    participant ST as AOI scan flow
    participant J as Publisher journal (bus library)
    participant BUS as Broker
    participant GW as Gateway BusSource
    participant EXT as Fleet / TSMC

    ST->>BUS: publish scan.announced (identifiers only - no file paths)
    Note over BUS: GEM shim receives early timing report
    ST->>J: publish scan.committed - ≤1 ms enqueue, journal-writer thread commits
    J->>BUS: pump publishes after durable
    BUS->>GW: deliver
    GW->>GW: WAL spool append (durable ownership FIRST)
    GW-->>BUS: DELIVER_ACK - publisher journal appends ack-tombstone
    GW->>EXT: Fleet gRPC + TSMC zip upload (poison-only dead-letter)
    Note over J,GW: any process down at any hand-off - the message waits<br/>in a durable store and replays. No silent loss anywhere
```

### Flow SYS-2 — factory-host command (wire unchanged)

```mermaid
sequenceDiagram
    participant H as Factory Host
    participant G as GEM door (driver + logic + shim)
    participant BUS as Broker
    participant FP as AOI BusAdapter

    H->>G: S2F41 StartManualScan (same bytes as today)
    G->>BUS: REQ gui.commands (Ttl from site E30 config, requester deadline)
    BUS->>FP: deliver (priority lane - never behind bulk)
    FP->>FP: Ttl gates + BeginInvoke post
    FP-->>BUS: REPLY ACCEPTED (on post)
    BUS-->>G: reply
    G->>H: HCACK ok
    Note over G: bus dark - deliberate HCACK denial + host-visible degrade,<br/>never a timeout (degraded contract, §1.3.4)
```

### Flow SYS-3 — external command (MES, new capability)

```mermaid
sequenceDiagram
    participant M as MES
    participant GW as CommandPublisher (:5007)
    participant BUS as Broker
    participant TM as ToolManager

    M->>GW: remote operation request
    GW->>GW: authenticate + authorize + audit
    GW->>BUS: REQ tool.commands (Ttl - topic ACL allows GEM shim + gateway only)
    BUS->>TM: deliver - Ttl check - execute or reject
    TM-->>BUS: reply
    BUS-->>GW: reply
    GW->>M: response
    Note over GW: bus down - :5007 answers "fabric unavailable" immediately
```

### Flow SYS-4 — degraded mode (broker restart)

```mermaid
sequenceDiagram
    participant P as All publishers
    participant BUS as Broker
    participant TH as ToolHost

    P--xBUS: broker down or hung (loop-lag heartbeat missed)
    P->>P: publish keeps returning ≤1 ms - class A to journal,<br/>B/C to bounded local queues
    TH->>BUS: restart child (quarantine: never - infinite backoff + alarm)
    P->>BUS: jittered reconnect - journal replay in seq order, paced
    Note over P,BUS: broker restart empties in-memory retained slots -<br/>every class-B publisher re-publishes current state on reconnect (R-5)
```

## 1.3 System-level new components — complete designs

### 1.3.1 Bus broker (`Camtek.Messaging.Broker`)

**Responsibility:** route typed topic messages between local processes with per-class delivery guarantees. Holds **no business logic and no persistence** — durability lives at the edges.

```mermaid
flowchart TB
    subgraph B["Broker process (net8, ToolHost child, startOrder 0)"]
        CONN["Connection manager\nnamed pipe per process - ACL identity -\nreader/writer SPLIT per connection"]
        REG["Topic registry\nclass + publish-ACL per topic"]
        QA["Class-A queues (bounded 128)\nfull - NACK - RESUME on drain"]
        QB["Class-B retained slots\nlocked keyed-slot, atomic dequeue,\nlast value delivered on subscribe"]
        QC["Class-C queues\ndrop-oldest + counted"]
        WR["Per-connection writer task\npriority lanes REQ/REPLY over A over B/C\nwrite deadline - disconnect on stall"]
        HB["Heartbeat - PING priority-dequeued,\nreports measured loop lag"]
        CTR["Counters - pushed to ToolHost\nevery heartbeat (survive broker death)"]
    end
    CONN --> REG --> QA & QB & QC --> WR
```

Key decisions: E2E-ack per **(message, declared durable-subscriber set)** — durable subscribers are a **static topic-registry property** (e.g. `scan.committed → {ToolGateway}`), so a merely *disconnected* durable subscriber (gateway restart) does **not** shrink the set — the message waits in the publisher journal and redelivers (this closes the gateway-restart silent-loss channel, R-1); only a genuinely **gateway-disabled** tool (no *declared* durable subscriber, set by signed profile) acks immediately; identity is the **OS-authenticated pipe account**, not a self-asserted `sourceName` (R-7); loop-lag heartbeat so ToolHost distinguishes *degraded* from *hung*; `quarantine: never` + `priorityClass: AboveNormal`.

**Flow — class-A delivery with a slow subscriber:** deliver → subscriber queue fills → `NACK` (message stays in the *publisher's* journal, broker memory bounded) → queue drains → `RESUME` → publisher redelivers in seq order with a bounded in-flight window. The broker can never be OOM'd by its slowest consumer.

### 1.3.2 ToolConnect gateway (evolved ToolGateway)

**Responsibility:** the tool's only door besides GEM — events out (Fleet/TSMC), authorized commands in (MES/CMM). ~70% exists today with tests; the additions:

```mermaid
flowchart LR
    BUSG[["Bus"]]
    subgraph G["ToolConnect (net8, ToolHost child, quarantine: never)"]
        BS["BusSource (NEW)\nsubscribes scan.committed,\ntool.telemetry, tool.state\nDELIVER_ACK = WAL-append ONLY (R-4)\nper-sink WAL state machine (R-3)\nhealth = consumption liveness token"]
        CP["CommandPublisher (NEW) :5007\nvalidate + authorize + audit -\npublishes tool/gui.commands"]
        ER["EventRouter (EXISTS)"]
        SD["SinkDispatchers (EXISTS)\nbounded 1000, batch"]
        FS["FleetSink (EXISTS)\n+ keepalive/deadline,\nre-register on reconnect"]
        TSK["TsmcSink (EXISTS)\nzip + native SDK shim"]
        SP["WAL spool (role change)\npoison-only dead-letter -\noutage retries forever under quota -\nperiodic 60 s drain"]
        PRX["CMM proxy (NEW)\nforwards :5007 to :50055\nmodal ops: long deadline, cap 1"]
    end
    FLEETG["Fleet.Main"]
    TSMCG["TSMC"]
    MESG["MES / CMM"]

    BUSG --> BS --> ER --> SD --> FS --> FLEETG
    SD --> TSK --> TSMCG
    BS --> SP
    MESG --> CP --> BUSG
    MESG --> PRX
```

**Flow — outage recovery:** sink down → messages sit in the WAL spool (DELIVER_ACK already sent on WAL append — **not** gated on sink routing, R-4) → periodic drain retries at a capped rate, oldest-first, interleaved with live traffic → a one-hour outage drains in <10 min without any restart. Each WAL entry tracks **per-sink** completion (R-3), so a message delivered to Fleet but pending for TSMC is retried only to TSMC, never re-sent to Fleet. Dead-lettering happens only for *poison* (fails while the sink is connected). At WAL quota the gateway **withholds DELIVER_ACK** (backpressure to the alarmed publisher journal) rather than dropping — loss is never taken at the sink hop (R-4).

### 1.3.3 ToolHost supervisor

**Responsibility:** the single Windows service (3 → 1); supervises the tool's headless children with job objects, per-child restart classes, and the tool's health/diagnostics surface.

```mermaid
flowchart TB
    SCM["Windows SCM (auto-start,\nfailure actions)"]
    subgraph THS["Camtek.ToolHost"]
        SUP["ProcessSupervisor\njob objects (KILL_ON_JOB_CLOSE) -\nrestart backoff - per-child quarantine class"]
        HA["HealthAggregator :5100\nper-child probes + bus counters mirror +\nbroker delivered vs gateway processed"]
        CFG["children config + endpoint manifest\n(single source of truth - hash in fleet fingerprint)"]
    end
    SCM --> THS
    SUP --> C1["broker (order 0, never quarantine,\nAboveNormal, pipe-lag probe)"]
    SUP --> C2["gateway (never quarantine)"]
    SUP --> C3["ToolServices host"]
    SUP --> C4["DataServer · FAR python · ..."]
```

**Flow — crash containment:** child exits → log + backoff restart → `maxPerHour` exceeded → *leaf* children quarantine (siblings unaffected); **broker/gateway never quarantine** (infinite max-backoff restarts + escalating alarm — a dark fabric costs more than a 2-minute retry). A killed ToolHost tears down all children via job objects — no orphans, ever.

### 1.3.4 GEM shim (inside `SecsGemObjects` / SecsGemGui.Net — plain C#)

**Responsibility:** the only change at the GEM door. Publishes host commands to the bus; subscribes to state/results for host event reports. The Cimetrix driver and E30/E87 logic are untouched — host wire behavior is byte-identical.

```mermaid
flowchart LR
    HOSTS["Factory Host"]
    subgraph SG["SecsGemGui.Net process"]
        DRV["Cimetrix SECSGemDriver\n(native - UNTOUCHED)"]
        OBJ["SecsGemObjects E30/E87 logic\n(UNTOUCHED)"]
        SHIM["Bus shim (NEW, C#)\nREQ gui/tool.commands -\nsubscribes scan.announced, tool.state -\ndegraded contract"]
    end
    BUSS[["Bus"]]
    HOSTS <--> DRV --- OBJ --- SHIM <--> BUSS
```

**Degraded contract — an explicit 4-state machine (resolves R-6).** The fab-facing promise is: *the fab never discovers a bus outage through mysterious host timeouts.* The shim is a state machine over **HSMS × bus**, not two booleans, and it **starts in the degraded state** — it leaves only when a bus **handshake** (a real REQ/PONG round-trip, not a `Health.IsConnected` flag read) completes:

| HSMS | Bus | Shim behavior |
|---|---|---|
| up | up (handshake done) | Normal. Host commands → REQ `gui/tool.commands`; **REMOTE is host/operator-granted** (the shim never auto-grants). |
| up | **down/hung** | Host-visible control state → **ONLINE-LOCAL** + dedicated alarm CEID; REMOTE refused; a host command is answered with a deliberate **HCACK denial**, *decided on the reader thread and returned immediately* — never a `Task.Wait` that parks the SECS reader for ~Ttl (the exact timeout the contract forbids). The command is completed **asynchronously off the reader thread** within the E30 window. |
| down | up | Bus fine, no host — nothing to report; shim idle. |
| down | down | Both alarms; recovery re-handshakes bus, host re-selects. |

**"Bus available" is the composite signal** — connected AND heartbeat-fresh AND loop-lag < L — not the raw socket flag (a hung broker holds the pipe open). On recovery the shim returns to **ONLINE-LOCAL and lets the host re-grant REMOTE** (auto-promotion to REMOTE is a compliance bug). `SecsGemGui.Net` is a **ToolHost-supervised child** with startOrder > broker (it was previously unmanaged, so its handshake had no ordering guarantee).

**No missed E30 transitions (resolves the class-B gap, DI-8).** `tool.state` stays class-B/retained for the *current-state* consumers, but the GEM shim additionally subscribes to a small **bounded last-N transition ring** (the last N `tool.state` transitions, N ≈ 16) republished by ToolManager, so after a reconnect blip the shim replays the intermediate transitions — e.g. an `Engineering → EngineeringToProduction → Engineering` failure cycle — and reports every E30 CEID the host expects. This makes the design independent of a per-site "does the host need intermediate transitions?" answer: it always delivers them, so **no per-site host sign-off is required**. (`stateSeq` already lets the shim *detect* a gap; the ring lets it *recover* one.)

**Standing (a normal P0 gate, not a design gap):** the Ttl margin `ttl + margin < E30` uses per-site E30 timeouts — a **P0 measurement of the same class the design already carries** (group-commit interval, single-instance ceilings, TsmcSink service time; §5.2 Wave 0). The shim **asserts `ttl + margin < E30` at config load and fails loudly** if a site's config violates it, so a bad number is caught at startup, never in production. The machine is fully specified; the numbers are measured at P0 like every other tool.

## 1.4 Cross-cutting contracts (summary — normative text in the proposal set)

| Contract | Rule |
|---|---|
| **Durability classes** | **A** never-lose (journal + WAL + **declared-durable-subscriber** E2E ack, R-1; dedup keyed by `(source, epoch, topic, seq)`, R-2): `scan.committed` · **A-ErrorsOnly** never-lose up to the storm-cap, drop+count beyond (honest bound ~2.8 h at 10/s/source): error telemetry · **B** latest-wins, retained, republished-on-reconnect (R-5): `tool.state`, `production.carrier` · **C** best-effort, counted drops: `scan.announced`, `loader.events`, `scan.operations` · **R-R** commands: Ttl + dequeue-gate + reply cache — at-most-once effect, never late |
| **Publish bound** | ≤1 ms unconditional (lock-free enqueue; single journal-writer thread group-commits off the caller) — contract-tested under disk co-load |
| **Payload contract** | `scan.announced` carries **no file paths** — a mis-wired consumer cannot read half-copied files |
| **Security** (R-7 — see [§6.8](06-bus-implementation.md)) | Publish ACLs key on the **OS-authenticated pipe account** (distinct service accounts per privileged publisher), never a self-asserted `sourceName`; default-deny; **signed+verified child manifest** (fail-closed); `:5007` default-deny, authenticated (**mTLS — decided**; Windows-auth fallback), minimum-interface bound + rate-limited; spool/journal/dead-letter at-rest ACLs; **append-only off-bus audit** before publish. Owner: Security (Ofek Harel) — a P1a entry criterion |
| **Storm control** | Error telemetry coalesced per `(source, errorCode)` + token bucket in the library — a flapping sensor costs summaries, not 300k journaled messages |
| **Endpoints** | One ToolHost-owned manifest; endpoint hash in the fleet fingerprint; DNS for Fleet |
| **Ports** | :5007 gateway commands · :5060 ToolServices · :5100 ToolHost health · :5050 Fleet (remote) · retired: :5005; contained→retired: :50055. The bus uses **no ports** (named pipes) |

Load: nominal <1 msg/s, wafer bursts ~50, storms capped at 10/s per source — every buffer is sized against this model with 4–5 orders of magnitude of single-instance headroom (no load balancing needed on-tool; fleet-side herd control via jitter + drain caps).
