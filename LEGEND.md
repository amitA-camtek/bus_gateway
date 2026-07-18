# Document Legend — Falcon Tool Communication Architecture Workspace

> Map of every document in `c:\Users\amita\Desktop\camtek`, its role, status, and how they relate.
> Last updated: 2026-07-18.

---

## Folder structure

```
camtek\
├── LEGEND.md            ← this file (the index)
├── CLAUDE.md            ← workspace guide for Claude Code sessions
├── stage\               ← presentation package: top-down, no-duplication design set
├── 01-proposal\         ← THE authoritative design set (implement from these)
├── 02-reviews\          ← review records / audit trail (read-only history)
├── 03-inputs\           ← independent source analyses (input evidence)
├── 04-history\          ← superseded designs kept as the decision record
└── 05-reference\        ← pre-existing docs describing TODAY's system
```

**`stage\`** ([stage/README.md](stage/README.md)) is the hierarchically-ordered package derived from `01-proposal\`: [executive summary](stage/executive-summary.md) (leadership/approval) → [0 context and case](stage/00-context-and-case.md) → [1 system architecture](stage/01-system-architecture.md) → [2 AOI drill-down with component designs + code snippets](stage/02-aoi-architecture.md) → [3 four-lanes appendix (per-lane complete designs)](stage/03-appendix-four-lanes.md) → [4 impact analysis (projects to modify)](stage/04-impact-analysis.md). Each fact appears in exactly one stage doc; `01-proposal\` remains normative for full detail (risk registers, TestKit, wire protocol, roadmap gates).

**`tool-gateway-unification\`** ([tool-gateway-unification/README.md](tool-gateway-unification/README.md)) is the near-term, bus-independent design for unifying ToolManager and ToolGateway into one external-facing component: [executive summary](tool-gateway-unification/executive-summary.md) (leadership/approval) → [0 problem](tool-gateway-unification/00-problem-and-current-state.md) → [1 alternatives](tool-gateway-unification/01-alternatives.md) → [2 recommendation](tool-gateway-unification/02-recommendation.md) → complete designs [Alt 1](tool-gateway-unification/03-alt1-complete-design.md) / [Alt 3](tool-gateway-unification/05-alt3-complete-design.md) with their review records.

## Recommended reading order

**Using the `stage\` set (preferred — self-contained, DESIGN READY):**

1. [stage/executive-summary.md](stage/executive-summary.md) — start here (leadership / approval)
2. [stage/00-context-and-case.md](stage/00-context-and-case.md) — verified baseline + business case (technical)
3. [stage/01-system-architecture.md](stage/01-system-architecture.md) → [stage/02-aoi-architecture.md](stage/02-aoi-architecture.md) — system and AOI designs
4. [stage/05-roadmap-and-risks.md](stage/05-roadmap-and-risks.md) — wave plan, live bugs, governance
5. [stage/stage-review.md](stage/stage-review.md) — review record (as needed)

**Using the full `01-proposal\` set (for deep implementer detail):**

1. [01-proposal/executive-summary.md](01-proposal/executive-summary.md) — leadership overview
2. [01-proposal/camtek-tool-fabric-complete-design.md](01-proposal/camtek-tool-fabric-complete-design.md) — system design
3. [01-proposal/aoi-main-complete-communication-design.md](01-proposal/aoi-main-complete-communication-design.md) — AOI_Main design
4. [01-proposal/camtek-messaging-bus-design.md](01-proposal/camtek-messaging-bus-design.md) + [01-proposal/camtek-toolhost-design.md](01-proposal/camtek-toolhost-design.md) — implementer deep-dives
5. Review records ([02-reviews/](02-reviews/)) — the audit trail (as needed)

---

## 1. The proposal (authoritative, current)

| Document | What it is | Status |
|---|---|---|
| [executive-summary.md](01-proposal/executive-summary.md) | Business case for the migration — for CTO/architects/management. Sits in front of the proposal | Current |
| [camtek-tool-fabric-complete-design.md](01-proposal/camtek-tool-fabric-complete-design.md) | **System-level complete design**: bus fabric + ToolConnect gateway + GEM door + cross-cutting contracts + roadmap. High/mid/low block diagrams + flows per part; bus condensed as Appendix A. Unit of discussion: a *process* | Current — includes 2026-07-18 concurrency/connectivity addenda (§4.3 GEM degraded contract, Part IV operations addenda, WAL flows) |
| [aoi-main-complete-communication-design.md](01-proposal/aoi-main-complete-communication-design.md) | **AOI_Main complete communication design**: all ~21 links dispositioned across the four lanes (BUS / SVC / CONS / KEEP — SVC and CONS exist only here), current architecture, benefits, internal design (§3.5 BusAdapter/UiMarshaller/threading), wave migration plan, per-lane appendices A–D, impact map (Appendix E). Unit of discussion: a *link inside AOI_Main*. **Complements** the fabric doc — together they are the complete picture | Current — READY for ADR per round-3 verification; Rev 2 threading contract (2026-07-18) |
| [camtek-messaging-bus-design.md](01-proposal/camtek-messaging-bus-design.md) | **Bus (`Camtek.Messaging`) implementation design**: API, envelope, wire protocol, journal, broker internals, durability classes, security, load model, TestKit, fabric-wide adoption (§12) | Current — **Revision 3** (2026-07-18): single-writer journal + ack-tombstones, split I/O + priority lanes, subscriber-set acks, retained class B, storm control |
| [camtek-toolhost-design.md](01-proposal/camtek-toolhost-design.md) | **ToolHost supervisor design**: one Windows service supervising the tool's headless processes (3 services → 1); job objects, restart policy, health API :5100 | Current — read with the quarantine-class amendments in the fabric doc / concurrency review (broker & gateway = `quarantine: never`) |

## 2. Review records (the audit trail — read-only history)

| Document | What it records |
|---|---|
| [aoi-main-design-review.md](02-reviews/aoi-main-design-review.md) | Iterative review of the AOI design: 3 reviewers → 20 major-or-worse findings + resolutions → round-2 verification (10 propagation fixes) → round-3 **READY** verdict with 5 standing conditions |
| [camtek-fabric-concurrency-review.md](02-reviews/camtek-fabric-concurrency-review.md) | Concurrency / connectivity / load review of the whole chain: 5 reviewers, 16 CRITICAL + 18 MAJOR findings + resolutions, the V1/V2 verification, **final verdict: all resolved**. Also §5: the **five live bugs found in today's shipped code** (ADO-ready) |
| [a3-fused-design-review.md](02-reviews/a3-fused-design-review.md) | The first adversarial review (of the fused design): baseline fact corrections T1–T10 (real SECS/GEM stack, FalconWrapper), criticals C1–C6, majors M1–M10 |
| [bus-verification-vs-aoi-analysis.md](02-reviews/bus-verification-vs-aoi-analysis.md) | Cross-check of the bus design against the independent AOI communication census — gaps found (multi-PC question, gRPC ground-rule correction) and the bus-vs-gRPC scope split that became the four-lane ADR |

## 3. Input analysis (independent source material)

| Document | What it is |
|---|---|
| [aoi-main-communication-analysis.md](03-inputs/aoi-main-communication-analysis.md) | The independent census of AOI_Main's full communication surface (~21 COM links, gRPC, MMF, files, inbound) with its own alternative evaluation — the source inventory the AOI complete design dispositions. Not authored in this program; treated as input evidence |

## 4. Historical / superseded (kept as the decision record — do not implement from these)

| Document | What it was | Superseded by |
|---|---|---|
| [a3-fused-bus-gateway-design.md](04-history/a3-fused-bus-gateway-design.md) | The fused A3 design, Revision 2 — first full bus+gateway design | Fabric complete design + bus Rev 3 (**supersession banner** in the header for its §4/§6 bus mechanics) |
| [aoi-client-architecture-alternatives.md](04-history/aoi-client-architecture-alternatives.md) | The alternatives study: baseline, A1 (edge gateway), A2 (local bus), F1/F2 analyses, risk register, mitigation input | The fused design (F2) → fabric doc. Carries a **revision banner** for the corrected SECS/GEM facts |
| [architecture-review-and-toolgateway-investigation.md](04-history/architecture-review-and-toolgateway-investigation.md) | The original architecture review + ToolGateway consolidation investigation (Options A–D, service inventory, R0–R3, frmProduction impact §5) | Its conclusions flowed into ToolHost + the alternatives; **corrections banner** in header |

## 5. Reference documents (pre-existing, describe TODAY's system)

| Document | What it covers |
|---|---|
| [frmProduction.md](05-reference/frmProduction.md) | Deep reference for `frmProduction` (the invisible COM controller) — with a dated **corrections note** (real GEM stack, FalconWrapper host, real hook lines, async-void reality) |
| [falcon-aoi-architecture-reference.md](05-reference/falcon-aoi-architecture-reference.md) | Original system block diagrams and flows (AOI ↔ host ↔ ToolGateway ↔ Fleet/TSMC) — same corrections note |
| [frmProduction-architecture.svg](05-reference/frmProduction-architecture.svg) / `.mmd` / `.png` | Rendered architecture diagram of frmProduction (pre-dates this program) |
| [CLAUDE.md](CLAUDE.md) | Workspace guide for Claude Code sessions — corrected key-files table |
| LEGEND.md | This file |

---

## Conventions

- **Normative vs historical:** when two documents cover the same topic, the §1 doc is normative and others cross-reference it. Historical docs keep their original text under a dated **⚠ banner** — never silently rewritten.
- **Consistency rule:** the §1 set was verified mutually consistent (AOI round-3 + concurrency V1/V2 sweeps). If you edit any §1 document, sweep the companions for the same fact.
- **Finding IDs:** `C*/M*/T*` = first review · `R1-*/R2-*` = AOI review rounds · `CC*/CM*` = concurrency review criticals/majors · `LB*` = live bugs in shipped code · `A-*` = AOI design risk register rows.
- **Phase vocabulary:** `P0–P5` = fabric roadmap phases · `Waves 0–2 + Deferred` = the funded execution plan (AOI design §5) · `B/S/C/F` = the four migration tracks (bus/services/consolidation/frozen).
