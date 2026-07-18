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
| **0** | Gateway spool bug fixes (live today); ToolHost + broker + comparator qualification (exit criterion with evidence); configuration manifest/fingerprint; degraded-startup contracts implemented; wrapper call-frequency telemetry; censuses (multi-PC A-1, sole-consumer A-3, SVC-pilot selection); P0 measurements (group-commit interval under co-load, single-instance ceilings, GEM pre-Ttl hop latencies, TsmcSink service time) | infra owner + gateway team | Foundation + the live bugs |
| **1** | P1a dual-run (`scan.committed` + `tool.telemetry`; gateway BusSource beside :5005; shadow compare) → hold → P1b retire :5005 after retention. In parallel (different files): CONS-standalone — RobotUI with `UiMarshaller`, then 1–2 census-passing modules | + 1 AOI dev | Scan-thread protection, zero silent loss, tool-down telemetry, :5005 gone, first process reduction |
| **2** | SVC pilot (census winner) proves seam + client stack; **CMM gateway proxy** (closes the :50055 external surface — no P4 dependency); remaining census-passing CONS modules, one per release | + ToolServices owner | Security containment, process-count complete, SVC pattern proven |
| **Deferred — trigger-based, unscheduled** | P2 `Fire*` sweep (triggers: A-1 + A-2 resolved AND a concrete consumer need) → P3 `tool.state` → P4 GUI commands (trigger: a customer/MES requirement COM cannot meet) → P5 production control (per customer, re-qual budgeted) → CMM per-op split → JobProvider via compatibility connector → GEM shim full adoption | per trigger | Full decoupling — majority of cost/risk, spent only against a named trigger |

**Per-edge gate (all waves):** call-frequency telemetry reviewed → contract/regression tests green → (bus edges) zero *unexplained* shadow divergence over N production days → rollback drill executed → FlaUI external-behavior suite green on the affected profile → (host-visible edges) GEM record-replay diff clean.

## 5.3 Degraded-operation governance (summary)

- **AOI:** bus dark at P1 = degrade loudly, production continues; at P4+ = refuse Production entry; already-in-Production = operator banner + pause at next wafer boundary; gateway reports *stale-since* to Fleet.
- **GEM process:** bus dark = host-visible control state degrades (ONLINE-LOCAL / alarm), REMOTE grant refused, commands answered with a deliberate HCACK denial — the fab never discovers the outage via timeouts.
- **Gateway :5007:** immediate "fabric unavailable" to external callers when the bus is down.
- **ToolHost:** broker and gateway are `quarantine: never` children (infinite max-backoff restarts + escalating alarm); leaf children quarantine normally.

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
7. **:5007 authn/authz mechanism** (mTLS vs Windows auth) — decided at P0; config in the endpoint manifest.
8. **P5 funding trigger** — which customer first requires production control on the modern path, re-qualification priced in.
