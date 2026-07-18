# 4 — Alt 1 Design Review Record

> Level: **review record** — adversarial review of Alternative 1.
> Up-link: design reviewed → [03-alt1-complete-design.md](03-alt1-complete-design.md).
>
> Four reviewers (feasibility, consistency, operations, security), all grounded in `C:\CamtekGit\BIS\Sources` with `file:line` evidence.
> **Unanimous verdict: NOT-READY — but the strategy is sound.** All four agree the lifecycle-promotion (G2) and reporting-single-surface (G1) reasoning is correct; the problems concentrate in three places: the **§3.6 COM adapter** (mechanism wrong + a security escalation path), the **CommandIntake / status surface** (unauthenticatable + info-disclosure), and the **spool drain** (broken in exactly the way the always-on service amplifies).
> Round 1: 2026-07-18.

---

## The headline

The always-on-service promotion is the whole point of Alt 1 — and it is *also* what breaks two things that "worked" only by accident before:
1. The spool drains **only at process start**; an AOI child restarted often, so the backlog drained by accident. A Windows service runs for weeks → a recovered Fleet/TSMC outage strands the backlog indefinitely. **The change that delivers G2 removes the crutch that hid the bug.**
2. AOI's startup process-sweep (`KillStaleToolGatewayProcesses`) kills *any* `ToolGateway.Endpoint` by name — including the SCM-managed service — and double-binds `:5005`.

Neither was visible from the design alone; both are code-verified.

## CRITICAL findings & resolutions

| # | Finding (reviewer, evidence) | Resolution |
|---|---|---|
| A1-C1 | **§3.6 adapter mechanism is wrong on every specific** (feasibility): the ROT moniker binds a **`SingletonHolder`, not `IToolManager`** (`SingletonUtils.cs:52-76`, `SingletonHolder.cs:24,57-71`) — needs a two-step (holder → `GetSingleton(assembly,type)` → cast); the feared `Marshal.GetActiveObject` was **never used** (real code uses manual `GetRunningObjectTable` P/Invoke, `MSDev.cs:16-20`, which *is* net7-portable); the true risk is **cross-process managed-COM marshaling to a net7/CoreCLR client with no proxy/stub/typelib**; "in-process COM interop" is a misnomer (ToolManager is out-of-process `ToolManager.exe`) | **Rewrite §3.6.** Drop the `GetActiveObject` framing; state the real holder→GetSingleton→cast sequence; **make Option B (net48 shim) the DEFAULT** (feasibility + security both demand it); re-scope the P0 spike's pass/fail to *"a net7 process reads `IToolManager.ToolState` cross-process against a live net48 `ToolManager.exe`"* |
| A1-C2 | **CommandIntake authorization is structurally impossible** (security): `RelayExternalCommand(ExternalCommand cmd)` (design L176) carries **no caller identity** — only a content allow-list is expressible, never "is *this caller* allowed"; no authn mechanism named anywhere; `_tm.InvokeExternal(...)` is **invented** — no such method (`IToolManager.cs:22-43`); the real command path is `ICarrierExecuter` control-plane *mutations* (`RemoteControl.cs:232,354-359`), contradicting the "read-mostly, control-protected" principle | **Drop CommandIntake / external-command relay from Alt 1 entirely.** It is a control-plane entry point that cannot be safely built inside a facade. Alt 1 becomes **read-only** (status observation only). If external commands are ever needed, they belong in a later, properly threat-modeled phase (or Alt 3 / the bus) with an identity-bearing interface + named authn (mTLS) — not here |
| A1-C3 | **Unauthenticated `0.0.0.0:5005` promoted to "the single door"** (security): the existing intake is plaintext h2c on all interfaces with **no auth** (`appsettings.json:43`, `App.cs:94`), gRPC reflection enabled (`Program.cs:51`), controllers/Swagger globally routed. Concentrating trust on an unhardened door is the core E187 gap | **Add a security-hardening prerequisite to U0/U1:** TLS + client auth on :5005 before it becomes the permanent surface; disable gRPC reflection + Swagger in production (`IsDevelopment()` gate); bind diagnostics to loopback. State that "single door" requires a *hardened* door |
| A1-C4 | **Spool drains only at process start** (operations + feasibility): `RestoreFailedMessages`'s only caller is `SinkDispatcher.StartAsync` (`SinkDispatcher.cs:89`) — no runtime replay. The AOI-child restarted often (draining by accident); the always-on service removes that, so a recovered outage strands the backlog. **G2 is undermined by the very change that delivers it** | **U0 prerequisite (before U1):** add a runtime/on-reconnect spool replay independent of `StartAsync`. Non-negotiable ordering — the drain fix ships *before* the service promotion, not alongside |
| A1-C5 | **Overflow spool file is write-only → unbounded silent loss** (feasibility + operations): beyond the 10k cap, `RestoreFailedMessages` **overwrites** `FailedMessages-{sink}.overflow.txt` (`FailedMessagesHandler.cs:142-143`) and nothing ever reads it (`OverflowPath` referenced only by the writer, `:191`). A multi-hour Fleet outage >10k events = permanent loss, each restore clobbering the last | **U0 prerequisite, CRITICAL:** overflow must be drained (loop restore until spool+overflow empty, batched) or bounded with an explicit dead-letter + alarm; add a per-message attempt counter to break the poison-replay cycle |
| A1-C6 | **AOI process-sweep kills the service** (operations): `KillStaleToolGatewayProcesses` kills every `ToolGateway.Endpoint` by name (`clsInitAOI.cs:478`) incl. the SCM service, and the child-launch double-binds `0.0.0.0:5005`; during the "flag-selected fallback" overlap the two modes are **not mutually exclusive in code** | **Redesign the U1 coexistence:** make service-vs-child **strictly mutually exclusive**; when service mode is on, AOI must not sweep by name (exclude the service PID, or skip the sweep entirely). Verify before any overlap-release ships |

## MAJOR findings & resolutions

| # | Finding | Resolution |
|---|---|---|
| A1-M1 | **"Single external surface" is an overclaim** (consistency): the factory host — the largest external actor — keeps its own door (the GEM wire), never through the gateway. G1/crit-1 as worded is false; `02`'s matrix gives Alt 1 and Alt 3 the same ✅, failing to discriminate | Adopt the **"two doors" framing** (GEM for the host, gateway for everything else — verbatim from `../stage/01`). Reword G1/crit-1 to "single external surface **for reporting egress + status** (non-host)"; mark Alt 1 **partial** on crit-1 in `02` and `03` |
| A1-M2 | **StatusEndpoint source (ToolManager) can be dead/stale exactly when the GUI is closed** (operations): ToolManager is an unsupervised weak-ROT singleton (`SingletonHolder.cs:24`) — survives AOI close (good) but nothing restarts it if it crashes; AOI startup force-kills it (`clsInitAOI.cs:665`) and a reconnect spawns a fresh `NotInitialized` instance → the adapter's RCW gets `RPC_E_DISCONNECTED`; StatusEndpoint could report stale/`NotInitialized` state to Fleet | Adapter must treat ToolManager as **volatile** (rebind-on-disconnect); StatusEndpoint must distinguish **"unavailable" vs "NotInitialized"** and never emit a stale cached state. Right-size G2: with the GUI closed there is **no new scan data** (it originates in `frmScanTab`), so "reporting survives" = *drain the spool* + *answer status* — which ties G2 directly to the C4/C5 spool fixes |
| A1-M3 | **Service identity unspecified → LocalSystem default → max blast radius** (security): `UseWindowsService()` with no account (`Program.cs:19`); one identity then holds the network listener + native TSMC DLL + COM handle to control + Fleet egress | **Mandate a dedicated low-privilege service account** (virtual account / gMSA) as a *requirement*, not "consider"; documented least-privilege ACLs; no interactive/admin rights |
| A1-M4 | **COM adapter is a network→control escalation path** (security): "read-mostly" is a *reliability* isolation claim, not a security boundary; a compromised network-facing gateway holding an in-process COM RCW just calls control directly | Reinforces A1-C1's resolution: **Option B (net48 shim) as a security boundary** — the network gateway talks to the shim over an authenticated localhost channel; only the shim holds COM and enforces its own authz. Never co-locate the raw COM reference with the network listener |
| A1-M5 | **Service-account ACL transition can fail service startup on upgrade** (operations): `EnsureSpoolDirectory` re-applies a *protected* ACL and `SetAccessControl` on every construction, no try/catch (`FailedMessagesHandler.cs:53,197-224`); on an upgraded tool the dir is owned by the operator → a service account may lack WRITE_DAC → throws → DI singleton fails → **service won't start** | Installer resets ownership/ACL of pre-existing `C:\Fleet\ToolGateway\{FailedMessages,Logs}` to the service account; wrap `EnsureSpoolDirectory` to log-and-continue on ACL failure |
| A1-M6 | **No tamper-evident audit** (security): audit = a plain Serilog file writable by the service's own identity (`Program.cs:16`) — a LocalSystem compromise edits it | Emit security-relevant events (status queries; any future command) to an append-only, **off-host** sink (syslog/SIEM) under a distinct identity. (Scope shrinks now that CommandIntake is dropped — but status-query audit still applies) |
| A1-M7 | **Criterion-6 glyph mismatch** (consistency): `03` scores forward-compat ✅, `02`/README score ⚠️ neutral; ✅ erases the Alt 1-vs-Alt 3 thesis ("Alt 3 *is* the bus citizen") | Reconcile `03` to **⚠️ neutral** — "U1/U2 are groundwork toward, not delivery of, the ToolConnect citizen (that is Alt 3)" |
| A1-M8 | **`01`'s Alt-1 diagram claims "host-command coordination"** the complete design contradicts (consistency) | Fix `01`'s Alt-1 diagram/prose: drop "host-command coordination" and the `HOST -.-> IN` edge — match `03` |
| A1-M9 | **ToolId=0 fleet identity** (all): `int.TryParse("BH01")→0` (`FleetMainServerClientImpl.cs:117`). CRITICAL *if* Fleet.Main keys on ToolId; the code comment suggests it may key on machineName/IP (unverified) | U0 prereq: **verify Fleet.Main's tool key**; if numeric ToolId matters, derive a stable int or change the proto to string. Live bug regardless |

## MINOR (folded into fixes)
Config source-of-truth (`appsettings` hardcodes one tool's `MainServerAddress`/`ToolId`/`Site` — a fleet footgun); :5007 "localhost + external" contradiction (pick one: authenticated TLS if external is real); native TSMC path resolves co-located deps so it's *not* a startup blocker but **verify end-to-end under the service account in Session 0**; SCM recovery must be set to "always restart" (default stops after 2–3); rollback is a flag only during the overlap window, a redeploy after; Mermaid verified clean; add :5007 + reflection-disable to the §3.3 component view.

## Live bugs in shipped code (independent work items)
- **LB-A (CRITICAL):** spool overflow overwrite → unbounded silent loss (`FailedMessagesHandler.cs:142-143`) + poison replay (`SinkDispatcher.cs:79,89-96`).
- **LB-B (MAJOR):** Fleet `ToolId=0` for alphanumeric names (`FleetMainServerClientImpl.cs:117`).
- **LB-C (MAJOR, security):** `:5005` unauthenticated on `0.0.0.0` + gRPC reflection + Swagger enabled in production (`appsettings.json:43`, `Program.cs:46-51`).

## Reviewer conflicts — resolved
- **Forward-compat glyph:** `03` said ✅, `02` said ⚠️. **Resolved to ⚠️** (A1-M7) — keeping the Alt 1 → Alt 3 distinction the recommendation depends on.
- **Native-DLL-under-service-account:** the ops reviewer initially feared a load failure, then self-corrected (co-located deps resolve from the exe dir) — **downgraded to "verify in Session 0"** (minor), not a blocker.

## What this does to Alt 1 (the net effect)
Alt 1 **narrows to its safe core**: (G2) promote the gateway to a supervised, hardened service **after** the spool drain/overflow are fixed and the AOI-sweep collision is resolved; (G1) declare it the single **reporting + read-only status** surface under the honest "two doors" framing. **CommandIntake and any write-relay are removed.** The COM adapter becomes a **separate least-privilege net48 shim** (security boundary + the pragmatic answer to the marshaling risk), read-only, treating ToolManager as volatile. Every headline goal survives; the risky embellishments are cut.

## Round 2 — Verification (2026-07-18)

All 6 CRITICAL (A1-C1…C6) and 9 MAJOR (A1-M1…M9) resolutions applied to `03` and cross-docs (`00`/`01`/`02`/README), independently verified: **all landed**, no new critical/major, diagrams parse-safe. The design was rewritten to Rev 2 — "two doors" framing, external command relay removed, the control coupling made a separate read-only least-privilege shim, the net48 shim promoted to the default (feasibility red herring corrected), and the spool-drain/overflow/`:5005`-hardening fixes elevated to hard U0 prerequisites. Two cosmetic MINOR nits (a self-description clause; a criterion-1 row label) fixed post-verdict.

---

# FINAL VERDICT: **READY**

The Alt 1 design ([03-alt1-complete-design.md](03-alt1-complete-design.md), Rev 2) is internally consistent, consistent with its companion docs, feasibility-grounded, and its diagrams render. The four-reviewer cycle (feasibility / consistency / operations / security, all grounded in `C:\CamtekGit`) found the *strategy* sound and narrowed the design to its safe core.

**Standing conditions (must hold before build — not document defects):**
1. **U0 prerequisites ship before U1** — spool runtime-drain + overflow-drain (both gate G2), :5005 TLS/auth hardening, AOI process-sweep service-exclusion, installer ACL/ownership transition.
2. **Re-scoped feasibility spike** — a net48 shim binds and reads `IToolManager.ToolState` cross-process against a live `ToolManager.exe`, and rebinds after an AOI-driven kill/relaunch.
3. **Verify Fleet.Main's tool key** (decides whether `ToolId=0` is critical or cosmetic).
4. **Name a least-privilege service account** and confirm per-customer `ToolGatewayEnabled` profiles.
5. **Human sign-off** — the security posture (TLS/auth, least-privilege, audit) needs a fab-cybersecurity reviewer; the "no host-visible change" claim needs the GEM/ops owners.

## Live bugs in shipped code (file independent ADO items)
- **LB-A (CRITICAL):** spool overflow overwrite → unbounded silent loss (`FailedMessagesHandler.cs:142-143`) + poison replay (`SinkDispatcher.cs:79,89-96`).
- **LB-B (MAJOR):** Fleet `ToolId=0` for alphanumeric names (`FleetMainServerClientImpl.cs:117`).
- **LB-C (MAJOR, security):** `:5005` unauthenticated on `0.0.0.0` + gRPC reflection + Swagger in production (`appsettings.json:43`, `Program.cs:46-51`).

Review trail: 4 grounded reviewers → 6 CRITICAL + 9 MAJOR + minors, all resolved → verification round → READY.
