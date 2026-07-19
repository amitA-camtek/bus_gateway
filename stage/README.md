# Stage — Comprehensive Design Package (self-contained)

> **⚠ REVIEW STATUS — DESIGN READY (bus core); gateway/GEM specification-complete pending 3 owner sign-offs (7th cycle).** Cycles 1–4 (13 reviewers) settled the bus core; the 5th added security/data-integrity/test-strategy (≈8 gaps, resolved); the 6th was a consistency + Mermaid-lint sweep (17 findings, 39/39 diagrams clean). The **7th cycle re-grounded the ToolConnect gateway (doc 07, authored after the 5th-cycle verdict and never multi-dimension-reviewed) and the GEM seam in the shipped code** and found **9 gateway-level specification gaps** — WAL entry ownership/in-flight/durability, a poison-vs-outage flag that misclassifies both ways, an unspecified P1a dual-run, missing CMM per-op authorization, and two GEM mechanisms with no registered topic / no driver primitive — **all now resolved in the design** ([stage-review-cycle7.md](stage-review-cycle7.md)). **Three items are flagged for owner ratification, not silently decided:** CMM per-operation authorization + cert scoping, the host-visible HCACK-4 GEM accept path, and the Fleet-egress cleartext posture. **"Design READY" ≠ "cleared to fund/build."** Read [stage-review-cycle7.md](stage-review-cycle7.md) and [stage-review.md](stage-review.md) before implementing — and build the gateway from **doc 07**, not the sketches.
>
> **Fully self-contained** design set for the Falcon Tool Fabric architecture: this folder can be copied or transferred anywhere and loses nothing — **no link points outside it**.
> Structured **top-down with no duplication**: every fact lives in exactly one document; the others cross-reference it.
> Provenance: distilled from the reviewed proposal workspace (repo `bus_gateway`, folders `01-proposal` / `02-reviews` — named here as plain text only, for citation). The underlying design passed 4 adversarial review cycles (13 reviewers: consistency, feasibility-vs-code, operations, concurrency, connectivity, load) with **all critical/major findings resolved and independently verified**.

## Reading order (high level → low level)

| # | Document | Level | Contents |
|---|---|---|---|
| — | [executive-summary.md](executive-summary.md) | **Leadership** | Business case, what the migration buys, costs/trade-offs, design confidence (5 review cycles), recommendation — for CTO / architects / management |
| 0 | [00-context-and-case.md](00-context-and-case.md) | **Decision** | Today's architecture (verified baseline + the seven pain points), what the migration buys, costs/trade-offs, recommendation |
| 1 | [01-system-architecture.md](01-system-architecture.md) | **System** | Context / process / component views, system communication flows, complete design of each **system-level new component** (bus broker, ToolConnect gateway, ToolHost, GEM shim) with block diagrams + flows, cross-cutting contracts |
| 2 | [02-aoi-architecture.md](02-aoi-architecture.md) | **Process (AOI_Main)** | Drill-down into the hub: current vs target internals, complete design of each **AOI-level new component** with block diagrams + flows + **code snippets of the critical sections**, and the **complete link-disposition table** (§2.9 — every one of the ~21 links → lane) |
| 3 | [03-appendix-four-lanes.md](03-appendix-four-lanes.md) | **Method** | The four migration lanes (BUS / SVC / CONS / KEEP) — per lane: complete design, block diagram, migration flow, code snippet of the lane's key mechanism |
| 4 | [04-impact-analysis.md](04-impact-analysis.md) | **Projects** | What is created, modified, and retired — per repo project (`C:\CamtekGit\BIS\Sources\...`), per phase, with blast radius |
| 5 | [05-roadmap-and-risks.md](05-roadmap-and-risks.md) | **Program** | Wave plan + per-edge gate, rollback/fleet-configuration governance, degraded-operation rules, consolidated risks, open questions, and the **five live bugs** found in shipped code |
| 6 | [06-bus-implementation.md](06-bus-implementation.md) | **Implementation** | The complete bus build spec: projects, API, envelope, wire protocol, journal mechanics, broker algorithms, request/reply, security, load model, and the 14-group test kit (incl. class diagrams for the client library, broker, and TestKit) |
| 7 | [07-toolconnect-design.md](07-toolconnect-design.md) | **Implementation** | The complete ToolConnect gateway build spec: internal architecture, class design, the WAL entry state machine (R-3/R-4), BusSource intake contract, CommandPublisher :5007 pipeline, CMM proxy, threading model, failure matrix, exists-vs-built map |

## Supporting documents

| Document | Contents |
|---|---|
| [stage-review.md](stage-review.md) | 5th/6th-cycle adversarial review record: 9 reviewers, B1–B3 bucket classification, all CRITICAL/MAJOR findings and their resolutions, round-2 DESIGN READY verdict, + the 6th-cycle (CON6) consistency landing |
| [stage-review-cycle7.md](stage-review-cycle7.md) | **7th-cycle** record: 9 lenses re-grounded on the ToolConnect gateway (doc 07) + GEM seam in the shipped code; 9 distinct CRITICALs (WAL ownership/dedup/poison-taxonomy/dual-run/CMM-authz/GEM ring+accept) + MAJORs, resolutions, and 3 owner-ratification standing conditions |
| [stage-decision-briefs.md](stage-decision-briefs.md) | Decision briefs for every fork resolved in the 5th cycle (R-1..R-8, R-OPS-1..6, R-TS-1..3) |
| [codeSnippets/](codeSnippets/) | 16 design-level C# sketches — see `00-README.md` for known sketch bugs (S-1..S-18); prose is normative where they diverge |

## Audience map

- **Management / approval:** [executive-summary.md](executive-summary.md) → doc 5 §5.1–5.2 (the plan).
- **Architecture review:** docs 0 → 1 → 2 → 3 → [stage-review.md](stage-review.md).
- **Program / PM:** docs 4 + 5.
- **Implementers:** docs 1–2 for orientation, 3 for the method, 6 to build the bus, 7 to build the gateway, 4 for the project map.

## No-duplication rules of this set

- Business case & current-architecture baseline: **only** doc 0.
- System diagrams and system-component designs: **only** doc 1.
- AOI internals, AOI-internal code snippets, and the link-disposition table: **only** doc 2. (Lane and bus code sketches also appear in docs 3 and 6, and are consolidated in `codeSnippets\`.)
- Lane rules, migration patterns, lane flows: **only** doc 3.
- Project/file impact: **only** doc 4.
- Waves, gates, governance, risks, open items, live bugs: **only** doc 5.
- Bus wire/journal/broker/test detail: **only** doc 6.
- ToolConnect gateway internals (WAL state machine, :5007 pipeline, CMM proxy): **only** doc 7 (doc 1 §1.3.2 keeps the system-altitude view; doc 6 §6.5 keeps the bus-side contract).
