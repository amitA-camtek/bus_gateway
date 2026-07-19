# Open Decisions — Human Sign-off Required

> **Purpose:** the single list of items that a design review **cannot** close — decisions that belong to a named owner, a security lead, a customer, or a P0 measurement gate. Everything here is *governance*, not unfinished design: every design fork is decided in the docs; these are the ratifications, owner assignments, and measured numbers that must land before the affected phase is **cleared to fund/build**.
>
> **"Design READY" ≠ "cleared to build."** The bus core is DESIGN READY across seven adversarial cycles; the gateway (doc 07) and GEM seam are specification-complete pending items **D1–D3** below.
>
> Sources: [stage-review.md](stage-review.md) (cycles 5–6), [stage-review-cycle7.md](stage-review-cycle7.md) (cycle 7), [stage-decision-briefs.md](stage-decision-briefs.md), [05-roadmap-and-risks.md §5.6](05-roadmap-and-risks.md).
> Legend — **Blocks:** the earliest phase that cannot proceed until this is signed. **Owner:** who decides (not who implements).

---

## A. Cycle-7 decisions — genuine forks with owner/security/customer implications

These three are **not drafting**. The design records the recommended answer, but each has a consequence a review must not decide unilaterally.

### D1 — CMM proxy: per-operation authorization + certificate operation-scoping
- **Finding:** X7-6 / SEC7-1 (CRITICAL). As originally designed, the CMM proxy authenticated but did **not** authorize per operation, and `:5007` certs were not scoped to an operation class — so **any accepted `:5007` certificate (including an MES command cert) could reach the full loopback `:50055` surface**: `UploadBinCodesToSecsIIWaferMap` (writes the SECS-II wafer map the factory host reads — yield/bin falsification), operator modal/alarm injection, attacker-controlled setup-path loads. `:50055` is loopback-only today, so the proxy would *create* new authenticated external reach.
- **Recommended resolution (in [07 §7.7](07-toolconnect-design.md), [06 §6.8.3](06-bus-implementation.md)):** default-deny per-identity operation allowlist on the proxy; certs scoped to an operation class (CMM ≠ MES); high-impact ops individually authorized + audited; "CMM proxy refuses un-allowlisted operations" = a Wave-2 exit criterion with a test.
- **Decision needed:** ratify the allowlist model + the cert-class scoping (does one CMM cert grant all CMM ops, or a finer split?).
- **Owner:** Security (Ofek Harel). **Blocks:** Wave 2 (CMM proxy) — treat as a P1a-class security gate.

### D2 — GEM host-command accept path: HCACK=4, and the "byte-identical" retraction
- **Finding:** X7-8 / GS7-2 (CRITICAL). The design promised host commands "accepted, completed asynchronously within the E30 window." The **untouched Cimetrix driver has no deferred-reply primitive** — `IE30CommandCB.CommandCalled([in,out] eCommandResults)` derives HCACK from the value written *before* the callback returns. The only real async primitive is `eCmdPerformLater` (**HCACK=4**, then the true outcome as a later collection event), which is a **different host-visible wire sequence** than today's HCACK-0-on-completion.
- **Recommended resolution (in [01 §1.3.4](01-system-architecture.md)):** map the accept path to `eCmdPerformLater` (HCACK=4) + a named completion CEID; **retract the "byte-identical host wire" claim for host commands** (it still holds for events and state); move the change into the **P4/P5 host-requalification budget**. The denial path (reader-thread HCACK denial) is unaffected.
- **Decision needed:** accept that P4 host commands are a host-visible change requiring GEM re-qualification (previously scoped as byte-identical); confirm the completion CEID with the host integration.
- **Owner:** GEM / host-qualification owner + PM (re-qual budget). **Blocks:** P4 (`gui.commands`) — not the funded Waves 0–2.

### D3 — Gateway→Fleet `:5050` egress remains cleartext in funded scope
- **Finding:** SEC7-7 / M-15 (MAJOR). The design retires the inbound `:5005` door and hardens all inbound surfaces, but **gateway→Fleet `:5050` stays cleartext with no credentials** — it carries `scan.committed` customer IP (WaferId/LotId/ResultsPath). The `§6.8` net-surface "one audited door at :5007" phrasing counted only *inbound* surfaces.
- **Recommended resolution (in [06 §6.8](06-bus-implementation.md) net-surface paragraph):** either bring `:5050` under TLS + credentials in funded scope, **or** record the residual (on-LAN customer-IP exposure / Fleet spoofing) as an **accepted risk with a named owner** — do not let "one door" imply the egress is covered.
- **Decision needed:** in-scope TLS for `:5050`, or accept the residual risk (and who owns it)?
- **Owner:** Security. **Blocks:** nothing hard; should be decided before the security work-stream sign-off.

---

## B. Standing conditions carried from cycles 5–6 (still open)

### D4 — Name the infrastructure + security work-stream owner
- Pre-P0 entry criterion ([05 §5.1](05-roadmap-and-risks.md) rule 4). The bus/ToolHost/gateway infrastructure needs a named owner, who **also owns the R-7 security work-stream** (cert/key management + rotation, threat-model sign-off, pen-test). No design fork is open here — the skeleton is complete ([06 §6.8](06-bus-implementation.md)); the work-stream must be staffed. **Owner:** Engineering management. **Blocks:** Wave 0 start.

### D5 — Ratify the R-1..R-8 resolutions as ADRs
- The 5th-cycle protocol-level resolutions (durable-subscriber set, `sourceEpoch`, WAL idempotency, ack-on-WAL, retained republish, GEM 4-state machine, mTLS, transition lock) are written into the normative docs but were resolved *at developer direction*, not board ratification. **Owner:** Chief Architects (ADR sign-off). **Blocks:** Wave 0 governance gate.

### D6 — Customer commissioning choice: P4+ dark-bus behavior (R-OPS-5)
- Two options are **both built and tested** ([05 §5.3](05-roadmap-and-risks.md)): (1) *refuse Production entry* (conservative default) vs (2) *supervised ONLINE-LOCAL override*. Which one wins on a dark bus is a **fab safety/business judgement**, set per-customer at commissioning — the design deliberately does not decide it. Shipped conservative (option 1) so no customer is silently opted into the weaker guarantee. **Owner:** each customer + PM. **Blocks:** P4+ per site.

### D7 — Optional cross-team hardening → now a **required** dependency (upgraded in cycle 7)
- Previously "optional defense-in-depth." Cycle 7 (M-3 / M-37 / DI7-3) found the ambiguous-outcome (deadline-after-send) sink leg is **routine, not a rare crash window**, so **downstream idempotency is required**: a Fleet-ingestion dedup key must be added to `ToolEventMessage` (the shipped `PushEventAsync` carries none), and TSMC same-`UniqueId`-overwrite must be an asserted cloud contract. **This is now a P1a cross-team dependency, not optional.** **Owner:** Fleet team + TSMC integration + gateway owner. **Blocks:** P1a "no unrecorded duplication" guarantee.

---

## C. P0 measurements that are now **pass/fail gates**, not just numbers

Design-complete, but the design's guarantees depend on these measured values clearing stated bounds ([05 §5.2](05-roadmap-and-risks.md), [06 §6.9](06-bus-implementation.md)). If a bound is missed, the affected guarantee is **re-scoped at Wave 0**, not discovered later.

| # | Measurement | Acceptance bound | If missed |
|---|---|---|---|
| D8 | **TsmcSink service time** (per-wafer zip upload) | **≤ 8.5 s/wafer (≥ 420/h)** | T-L4 "1-h outage drains < 10 min" is re-scoped per sink, or TSMC drain concurrency raised above 1 (M-4 / LD7-3) |
| D9 | **FleetSink throughput** | **≥ max(60 msg/s storm, 7 msg/s T-L4 drain)** | gateway-egress aggregate coalescing required (M-4 / LD7-5) |
| D10 | **`scan.dds-node-status` emit rate** (native `PizzasConnectionStatus` period) | must be measured **before** the topic is sized/registered | blocks P2–P3 registration (LD7-6) |
| D11 | **Max tool-state transitions per outage window** | justifies the N ≈ 16 replay-ring depth | resize the ring (GS7-8) |
| D12 | Group-commit interval under co-load; single-instance ceilings; GEM pre-Ttl hop latencies | existing P0 set (unchanged) | standard sizing |

---

## D. Non-blocking work items (file regardless of the program)

- **D13 — Live bugs LB3 + LB4:** recommended for **immediate ADO filing** independent of any fabric decision — LB3 (every alphanumeric tool registers with Fleet as ToolId 0, fleet-wide collision) and LB4 (`NonBlockingUITask` whole-second-timeout → 0 ms spurious cancel). LB1/LB2/LB5 (gateway spool) are fixed in Wave 0. Minimal fixes in [codeSnippets/16-live-bug-fixes.cs](codeSnippets/16-live-bug-fixes.cs). **Action:** file (a write action — not yet performed).
- **D14 — Workspace `CLAUDE.md` stale census (report-only, outside the `stage\` set):** the trigger is cited as `frmJobTab.cs:941–982` / "three callers 948/981" — the verified reality is `:1342/:1375` + the larger C-2 census; "~25 Fire*" predates the C-3 80-site/12-file count. **Action:** owner sweep of the workspace `CLAUDE.md`.

---

## Summary — what unblocks each phase

| Phase | Must be signed first |
|---|---|
| **Wave 0 start** | D4 (owner named), D5 (ADR ratification) |
| **P1a** | D7 (Fleet dedup key — cross-team), D8–D9 (P0 sink bounds), the P1a security exit criteria (`:5007` authn) |
| **Wave 2** | D1 (CMM per-op authz) |
| **P2–P3** | D10 (dds-node-status rate) |
| **P4+ (per customer)** | D2 (HCACK-4 re-qual), D6 (dark-bus commissioning choice) |
| **Anytime** | D3 (:5050 posture), D13 (file LB3/LB4), D14 (CLAUDE.md sweep) |

*No item on this list is design work. Each is a ratification, an owner assignment, a measured number, or a customer choice — the things a design review cannot and should not close on its own.*
