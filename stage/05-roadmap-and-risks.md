# 5 — Roadmap, Governance & Risks

> Level: **program**. The execution plan (waves), the per-edge quality gate, rollback and fleet-configuration governance, the consolidated risk list, open questions, and the live-bug work items.
> Up-links: what changes → [04-impact-analysis.md](04-impact-analysis.md); how each edge migrates → [03-appendix-four-lanes.md](03-appendix-four-lanes.md).

---

## 5.1 Program-shape rules

1. **Max two concurrent code streams in AOI_Main:** (i) B-infra (bus/ToolHost/gateway — barely touches AOI until P2) and (ii) exactly **one** AOI-heavy stream per release (a CONS absorption *or* an SVC service *or* a P2 tranche — never P2 concurrent with an absorption; they edit the same files).
2. **Fleet configuration is a P0 deliverable:** ≤5 signed canonical profiles (arbitrary flag combinations refused at startup); a config fingerprint (edge flags + versions + **endpoint-config hash**) in every log header and on `tool.telemetry`; a fleet dashboard before the first production flag flip. Test matrix = per-profile suites + each profile's single-flag rollback neighbors.
3. **Rollback-validity matrix:** every edge classified *flag* (dual path shipped, exercised in CI) / *redeploy* / *reinstall*. Any retirement (CONS step 5, P1b) only after the **last** fleet tool has run the new path for a full release cycle (N-release retention). A rollback that is never drilled is a rumor.
4. **Named owner** for bus/ToolHost infrastructure — pre-P0 entry criterion.
5. **Calendar honesty:** full scope = 10–20 release cycles; the wave plan exists because most value ships in the first 3–4.

## 5.2 The wave plan

| Wave | Content | Teams | Delivers |
|---|---|---|---|
| **0** | Gateway spool bug fixes (live today); ToolHost + broker + comparator qualification (exit criterion with evidence); configuration manifest/fingerprint; degraded-startup contracts implemented; wrapper call-frequency telemetry; censuses (multi-PC A-1, sole-consumer A-3, SVC-pilot selection); **P0 measurements with acceptance bounds (M-4):** group-commit interval under co-load; single-instance ceilings; GEM pre-Ttl hop latencies; **FleetSink ≥ max(60 msg/s storm, 7 msg/s T-L4-drain)**; **TsmcSink ≤ 8.5 s/wafer (≥ 420/h) — else T-L4 is re-scoped per sink**; **`scan.dds-node-status` emit rate** (blocks its P2–P3 registration); **max tool-state transitions per outage window** (justifies the N≈16 replay ring) | infra owner + gateway team | Foundation + the live bugs |
| **1** | P1a dual-run (`scan.committed` + `tool.telemetry`; gateway BusSource beside :5005; shadow compare) → hold → P1b retire :5005 after retention. In parallel (different files): CONS-standalone — RobotUI with `UiMarshaller`, then 1–2 census-passing modules | + 1 AOI dev | Scan-thread protection, zero silent loss, tool-down telemetry, :5005 gone, first process reduction |
| **2** | SVC pilot (census winner) proves seam + client stack; **CMM gateway proxy** (contains the EOL Grpc.Core runtime + gives the external CMM caller an authenticated, per-op-authorized :5007 door — :50055 was already loopback-bound, so this is runtime-containment, not "closing an external surface"; no P4 dependency); remaining census-passing CONS modules, one per release | + ToolServices owner | Runtime containment, process-count complete, SVC pattern proven |
| **Deferred — trigger-based, unscheduled** | P2 `Fire*` sweep (triggers: A-1 + A-2 resolved AND a concrete consumer need) → P3 `tool.state` → P4 GUI commands (trigger: a customer/MES requirement COM cannot meet) → P5 production control (per customer, re-qual budgeted) → CMM per-op split → JobProvider via compatibility connector → GEM shim full adoption | per trigger | Full decoupling — majority of cost/risk, spent only against a named trigger |

**Per-edge gate (all waves):** call-frequency telemetry reviewed → contract/regression tests green → (bus edges) zero *unexplained* shadow divergence over the **R-TS-2 event-count gate — ≥ 10 000 `scan.committed` pairs; ≥ 500 `tool.state` transitions incl. scripted storms (not calendar days, which have near-zero power at ~10 tool.state/day)** → rollback drill executed → FlaUI external-behavior suite green on the affected profile → (host-visible edges) GEM record-replay diff clean.

## 5.3 Degraded-operation governance (summary)

- **AOI:** bus dark at P1 = degrade loudly, production continues; already-in-Production = operator banner + pause at next wafer boundary; gateway reports *stale-since* to Fleet. **At P4+** (`gui.commands` authoritative), the behavior is a **per-customer commissioning policy** with **two designed options, shipped conservative:** (1) *default — refuse Production entry* (the safe choice: a dark bus can silently lose the authoritative host command path, so a healthy-hardware tool is held rather than risk a lost command); (2) *opt-in — supervised ONLINE-LOCAL override*, where Production continues with the host notified via the GEM degraded contract (§1.3.4) and commands driven by the operator, bus-independent. **This is not a design gap and not something the design decides for the fab** (R-OPS-5): it is a **commissioning-time deployment config the customer sets**, because whether availability or command-integrity wins on a dark bus is a fab safety/business judgement, not an engineering one. The design ships option 1 as the default so no customer is silently opted into the weaker guarantee; a customer selects option 2 explicitly at commissioning. Both paths are built and tested; the *selection* is theirs.
- **GEM process:** bus dark = the 4-state degraded machine (§1.3.4) — ONLINE-LOCAL + alarm, REMOTE refused, deliberate HCACK denial decided on the reader thread and returned immediately (never a reader-thread `Wait`); the fab never discovers the outage via timeouts.
- **Gateway :5007:** immediate "fabric unavailable" to external callers when the bus is down; a REQ to a dead command target gets `REPLY(rejected:no-server)`, distinct from a timeout.
- **ToolHost:** broker and gateway are `quarantine: never` children (infinite max-backoff restarts + escalating alarm); leaf children quarantine normally; **graceful stop** on `SERVICE_CONTROL_STOP`/DeployUI tears children down in reverse start-order with a per-child drain timeout (job-object kill is the backstop, not the primary path — R-OPS-3), so OS-servicing windows don't hard-kill broker/gateway mid-write.
- **Config (R-OPS-2):** an invalid or unverified profile at startup **boots the last-known-good signed profile** + loud alarm + fingerprint-mismatch flag — a config typo is never an un-startable $2M tool. Signed profiles are retained on disk for exactly this fallback.
- **Alarm routing (M-16/OPS7-1):** every alarm has one stated surface + operator action ([07 §7.13](07-toolconnect-design.md) table). **Gateway/WAL/journal alarms reach Fleet via the ToolHost `:5100` health push** (survives broker death) — never solely via `tool.telemetry`, which is backpressured by the very condition it would report. New alarms (epoch-regression, WAL 50/80 % + I/O-failure, poison dead-letter) land on the operator banner/light tower locally and :5100 for the fleet.
- **Dead-letter recovery (OPS7-7):** a `ToolConnect.exe --deadletter list|inspect|reinject <entryId>` CLI (re-inject = a new WAL entry, same messageId) is a **P1a deliverable** — a poison dead-letter that no tool can re-inject recreates today's "files no code reads." The dead-letter dir sits under the WAL root ([06 §6.8.5](06-bus-implementation.md) ACL).
- **ToolHost self-update (OPS7-6):** stage side-by-side → `sc stop` graceful drain → swap → start → **post-start :5100 health gate within T** → on failure **auto-revert to the retained N-1 binary** (binary last-known-good, mirroring config LKG) → re-enable SCM failure-actions. Listed in the §5.1 rule-3 rollback-drill set.

## 5.4 Consolidated top risks (all resolved to mitigations; full registers in the source proposal set)

| Risk | Mitigation (designed-in) |
|---|---|
| Sync→async semantic drift during dual-run (the biggest hidden risk) | Shadow comparator (qualified: fail-open, bounded, mode/thread/skew-aware; evictions are an *explained* category); atomic reaction-block rule (never split a state's reactions across disciplines); `stateSeq` pairing |
| Class-A durability hand-offs | Journal-writer thread + ack-tombstones; WAL-before-ack at the gateway; poison/outage-split retry; composite-fault contract tests |
| Deadlock/starvation | `Invoke` banned (BeginInvoke-only + two-stage Ttl gates + compensations); split-I/O + priority lanes; NACK redelivery schedule + `RESUME`; single-STA rule for absorbed modules |
| Storms & herds | Error-telemetry coalescing/token bucket in the library; jittered reconnect + paced replay; Fleet-side registration/drain jitter and caps |
| Fleet configuration chaos | ≤5 profiles + fingerprint + dashboard (rule 2) |
| Program stall ("not survivable as four parallel tracks") | Two-stream rule + waves + trigger-gated deferrals (rule 1, §5.2) |
| SVC lane UI freezes | Mandatory client policy: deadline, circuit breaker, per-service fallback |
| Endpoint config drift | ToolHost-owned endpoint manifest; hash in the fingerprint; DNS for Fleet |

## 5.5 Live bugs in TODAY's shipped code (work items independent of the program)

| # | Bug | Location |
|---|---|---|
| LB1 | Spool poison loop — no retry cap/age limit; deterministic-fail message cycles forever | `ToolGateway.BL\EventMessages\FailedMessagesHandler.cs:98-153` |
| LB2 | `ToolApiPublisher`: `Thread.Sleep(1000)` + process spawn on the scan thread, no gRPC deadline, write-only dead-letter file, stale self-restart exe name | `system\CamtekSystem\PubSub\ToolApi\ToolApiPublisher.cs` |
| LB3 | **Fleet ToolId identity collapse** — `int.TryParse("BH01") → 0`; every alphanumeric tool registers as ToolId 0 (fleet-wide collision) | `ToolGateway.Endpoint\Services\FleetMainServerClientImpl.cs:115-125` |
| LB4 | **`NonBlockingUITask` timeout bug** — `timeout?.Milliseconds` (0–999 component) instead of `TotalMilliseconds`; any whole-second timeout ⇒ 0 ms ⇒ spurious cancellation | `system\CamtekSystem\AsyncTask\NonBlockingUITask.cs:24,41` |
| LB5 | **No spool drain loop exists** — restore only at process start, capped 10,000 (excess to a never-read overflow file), `TryWrite` into a 1,000-capacity channel re-spools ~9k of 10k | `ToolGateway.BL\Sinks\SinkDispatcher.cs` + `FailedMessagesHandler.cs:49-52,108-109` |

LB3 and LB4 are recommended for immediate filing regardless of the program decision.

## 5.6 Open questions / standing conditions (gate their dependent steps)

1. **Multi-PC topology census (A-1)** — do any bus-relevant counterparts run off-box? Blocks P2+ and the `machine.*`/`dds.*` fabric-wide adoption (the bus is same-PC named pipes by design; off-box = gateway/gRPC territory).
2. **ScanManager host reconciliation (A-2)** — ScenarioManager.exe vs FalconWrapper.exe; blocks P2 planning.
3. **Sole-consumer censuses (A-3)** — gate each CONS absorption (2 of the first 3 candidates already failed).
4. **Dependency approvals** — `System.Threading.Channels` (net48); broker build-vs-embed decided at P0 with the torture test.
5. **Comparator qualification evidence** — P0 exit criterion, before P1a entry.
6. **Fleet telemetry when the AOI is closed** — answered by design (gateway runs from boot under ToolHost); noted because it also settles the older ToolGateway-lifecycle question.
7. **:5007 authn/authz mechanism** — **DECIDED: mTLS** (Windows-auth fallback for fully domain-joined sites), see [§6.8.3](06-bus-implementation.md); config in the endpoint manifest. Remaining work is certificate/key implementation, not a mechanism choice — no longer an open question.
8. **P5 funding trigger** — which customer first requires production control on the modern path, re-qualification priced in.

### 5.6.1 5th-cycle decisions — RESOLVED; design is READY

The three dimensions the earlier four cycles omitted (security / data-integrity / test-strategy) surfaced 8 design-decision gaps + operational/test decisions. **Every one is now resolved in the design** (R-1..R-8 → §6.3/§6.5/§6.6/§6.8, §1.2/§1.3, §2.4; decision briefs in [stage-decision-briefs.md](stage-decision-briefs.md)), and where a resolution had depended on an external input the design was **strengthened so it no longer does** — the gateway is idempotent on its own (R-3), the GEM shim recovers missed transitions itself (R-6), `:5007` authn is decided (R-7, mTLS), and the R-8 lock is code-verified against the real ToolManager. What **remains** is not design work — it is normal program governance: ratify the decisions, name owners (an existing pre-P0 criterion), take routine P0 measurements, execute Wave-0 builds, run the P3 code spike, one *optional* cross-team hardening (R-3), and one *customer* commissioning choice (R-OPS-5).

| # | Gap | Design resolution (where) | What remains (NOT design) | Class |
|---|---|---|---|---|
| A-4 | Durable-subscriber protocol (R-1) | Declared durable subscribers as a static topic property; disconnect ≠ deregistration (§6.6, §1.3.1) | Ratify | ✅ design-complete |
| A-5 | Publisher `sourceEpoch` (R-2) | Epoch in envelope+HELLO; dedup `(source,epoch,topic,seq)` (§6.3, §6.5) | Ratify | ✅ design-complete |
| A-6 | Gateway WAL + idempotency (R-3, R-4; **hardened cycle 7**) | Ack-on-WAL-only, per-sink state machine with `InFlight` + single WAL-state actor, backpressure-at-quota (per-topic floor), **gateway-side dedup on `(source,epoch,topic,seq)` with a durable high-water** (§6.5, [07 §7.4–7.5](07-toolconnect-design.md)) | **Cycle-7 correction:** the ambiguous-outcome (deadline-after-send) leg is routine, so **Fleet/TSMC downstream idempotency is *required*, not optional** — a Fleet ingestion dedup key is a **P1a cross-team dependency** (the shipped `PushEventAsync` carries none); TSMC same-`UniqueId`-overwrite is an asserted cloud contract | ✅ design-complete; downstream dedup upgraded to a required dependency |
| A-7 | Retained class-B after restart (R-5) | Publishers re-publish on reconnect; broker serves `sourceConnected` liveness (§6.6, SYS-4) | Ratify | ✅ design-complete |
| A-8 | GEM degraded contract + supervision (R-6) | 4-state machine, async-off-reader HCACK, ToolHost-supervised, **bounded last-N transition ring so no E30 transition is missed** (§1.3.4) | E30 Ttl-margin numbers are a **normal P0 measurement** (same class as group-commit interval / ceilings), asserted-and-fail-fast at config load. **Per-site host sign-off no longer needed** (the ring always delivers transitions) | ✅ design-complete (residual = a routine P0 gate) |
| A-9 | Security work-stream (R-7) | OS-account-bound ACLs, signed manifest, **mTLS on `:5007` (decided)**, at-rest ACLs, off-bus audit (§6.8) | Named owner (folds into §5.1 rule-4 pre-P0 criterion) + work-stream **implementation detail** (cert/key mgmt, threat-model sign-off, pen-test). No design fork open | ✅ design-complete (work-stream to execute) |
| A-10 | ToolManager transition lock (R-8) | **Code-verified**: complete grep census, COM-singleton/sync-fan-out confirmed; lock-stamp + fan-out-outside-lock design + deadlock-audit method (§2.4) | The P3 **implementation + test-run** on the real build | ✅ design-complete + code-verified |
| A-11 | Test/CI instruments (R-TS-1..3) | net48×2-bitness test target, CI tier table, instrument-qualification method (§4.4) | Build + qualify GEM record-replay and the comparator — a **Wave-0 effort** | ✅ plan-complete (Wave-0 build) |
| A-12 | Operational deliverables (R-OPS-1..4) | Rollback-class column (§4.3), last-known-good boot + graceful-stop + spool migrator (§5.3, §4.4), named dashboard owner | Execute the **Wave-0 deliverables** | ✅ design-complete (Wave-0 build) |
| A-12b | P4 refuse-Production (R-OPS-5) | **Both options built + tested**; default = refuse-entry (conservative), opt-in = supervised override (§5.3) | The **selection is a commissioning-time customer config** — a fab safety/business judgement the design deliberately does not make | ✅ design-complete (customer config, by design) |
| A-13 | Runtime target (R-OPS-6) | Target **.NET 10 LTS**, self-contained runtime servicing (§4.4) | Ratify at P0 | ✅ design-complete |

**Reading it — the design is READY; nothing on the "what remains" column is design work.** Every fork is decided. What is left is exactly what a *design* review cannot and should not do: ratify the decisions, name owners (an existing pre-P0 criterion), take routine P0 measurements, execute Wave-0 builds, run the P3 code spike, obtain one optional cross-team hardening confirmation (R-3), and let each customer set the one commissioning policy that is theirs to set (R-OPS-5). **"Design READY" is not the same as "cleared to fund/build"** — the latter still needs the named owners and the P0 gates, which is normal program governance, not an unresolved design defect.
