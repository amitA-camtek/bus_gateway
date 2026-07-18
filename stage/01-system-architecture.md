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
        TOP[["9 registered topics\nscan.* · tool.* · gui.commands ·\nloader.events · production.carrier"]]
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
    Note over P,BUS: retained class B re-delivers current state to every subscriber
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

Key decisions: E2E-ack per **(message, subscriber-set snapshot at PUB)** — a disconnecting subscriber leaves every pending set (its durability claim ends with registration); zero-durable-subscriber publish acks immediately (no journal leak on gateway-disabled tools); loop-lag heartbeat so ToolHost distinguishes *degraded* from *hung*; `quarantine: never` + `priorityClass: AboveNormal`.

**Flow — class-A delivery with a slow subscriber:** deliver → subscriber queue fills → `NACK` (message stays in the *publisher's* journal, broker memory bounded) → queue drains → `RESUME` → publisher redelivers in seq order with a bounded in-flight window. The broker can never be OOM'd by its slowest consumer.

### 1.3.2 ToolConnect gateway (evolved ToolGateway)

**Responsibility:** the tool's only door besides GEM — events out (Fleet/TSMC), authorized commands in (MES/CMM). ~70% exists today with tests; the additions:

```mermaid
flowchart LR
    BUSG[["Bus"]]
    subgraph G["ToolConnect (net8, ToolHost child, quarantine: never)"]
        BS["BusSource (NEW)\nsubscribes scan.committed,\ntool.telemetry, tool.state\nWAL-append BEFORE ack\nhealth = consumption liveness token"]
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

**Flow — outage recovery:** sink down → messages sit in the WAL spool (already appended pre-ack) → periodic drain retries at a capped rate, oldest-first, interleaved with live traffic → a one-hour outage drains in <10 min without any restart. Dead-lettering happens only for *poison* (fails while the sink is connected).

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

**Degraded contract (the fab-facing rule):** four HSMS×bus states are defined; the critical one — *HSMS up / bus down* — forces the shim to degrade the **host-visible control state** (ONLINE-LOCAL / dedicated alarm, REMOTE grant refused) and answer commands with a deliberate HCACK denial code, so the fab never discovers a tool outage through mysterious timeouts. The shim's first action at start is a bus handshake **before** enabling REMOTE; retained `tool.state` removes staleness on reconnect.

## 1.4 Cross-cutting contracts (summary — normative text in the proposal set)

| Contract | Rule |
|---|---|
| **Durability classes** | **A** never-lose (journal + WAL + subscriber-set E2E ack): `scan.committed`, error telemetry · **B** latest-wins, retained: `tool.state`, `production.carrier` · **C** best-effort, counted drops: `scan.announced`, `loader.events`, `scan.operations` · **R-R** commands: Ttl + dequeue-gate + reply cache — at-most-once effect, never late |
| **Publish bound** | ≤1 ms unconditional (lock-free enqueue; single journal-writer thread group-commits off the caller) — contract-tested under disk co-load |
| **Payload contract** | `scan.announced` carries **no file paths** — a mis-wired consumer cannot read half-copied files |
| **Security** | Pipe ACLs (identity per connection); per-topic publish ACLs (`*.commands` = GEM shim + gateway only); no internal TCP listeners; one audited external door (:5007) |
| **Storm control** | Error telemetry coalesced per `(source, errorCode)` + token bucket in the library — a flapping sensor costs summaries, not 300k journaled messages |
| **Endpoints** | One ToolHost-owned manifest; endpoint hash in the fleet fingerprint; DNS for Fleet |
| **Ports** | :5007 gateway commands · :5060 ToolServices · :5100 ToolHost health · :5050 Fleet (remote) · retired: :5005; contained→retired: :50055. The bus uses **no ports** (named pipes) |

Load: nominal <1 msg/s, wafer bursts ~50, storms capped at 10/s per source — every buffer is sized against this model with 4–5 orders of magnitude of single-instance headroom (no load balancing needed on-tool; fleet-side herd control via jitter + drain caps).
