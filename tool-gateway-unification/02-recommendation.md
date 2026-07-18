# 2 — Comparison & Recommendation

> Level: **decision**. Scored comparison of the three alternatives and the recommended path.
> Up-link: alternatives → [01-alternatives.md](01-alternatives.md).
> Down-links: Alt 1 complete design → [03-alt1-complete-design.md](03-alt1-complete-design.md) · Alt 3 complete design → [05-alt3-complete-design.md](05-alt3-complete-design.md).

---

## 2.1 Scored against the six success criteria

(Criteria from [00-problem-and-current-state.md §0.4](00-problem-and-current-state.md).)

(Criteria from [00-problem-and-current-state.md §0.4](00-problem-and-current-state.md). Alt 1 column reflects the post-review Rev 2 design — see [04-alt1-review.md](04-alt1-review.md).)

| Criterion | Alt 1 — Facade | Alt 2 — Co-hosted merge | Alt 3 — Unified service |
|---|---|---|---|
| 1. Single non-host external surface | ⚠️ **partial — "two doors"**: single *non-host* surface; host keeps the GEM door | ✅ yes (one process) | ⚠️ **closer — "two doors at the wire, one brain behind them"**: one supervisor owns all non-host I/O + coordination, but the host keeps the GEM wire (hosted in-process, not re-routed through the service) — [05 §5.2](05-alt3-complete-design.md) |
| 2. Single lifecycle & supervision | ✅ (hardened service) — **conditional on the U0 spool-drain fix** | ✅ | ✅ one supervisor owns both domains; egress GUI-independent, control supervised in the interactive session (not a Session-0 boot service — [05 §5.4](05-alt3-complete-design.md)) |
| 3. Control core protected | ✅ untouched; read-only coupling via a separate shim | ❌ state machine re-hosted/ported | ✅ carried **in-process, unchanged internally, not re-sliced**; no new cross-CLR control boundary ([05 §5.2/§5.6](05-alt3-complete-design.md)) |
| 4. Native-DLL blast radius contained | ✅ (separate process) | ❌ shares the control crash domain | ✅ (child/sandbox) |
| 5. Reversible | ⚠️ flag during overlap, redeploy after | ❌ low (redeploy) | ⚠️ medium |
| 6. Forward-compatible with the bus | ⚠️ neutral (groundwork toward, not delivery of, the ToolConnect citizen) | ❌ merged lump to later un-pick | ✅ **becomes** the bus's gateway citizen |
| **Effort** | **S–M** (+ U0 prerequisites) | L | M–L |
| **Fab re-qualification** | **none** | likely | low (validate) |

Note criterion 1 discriminates Alt 1 (partial — two doors) from Alt 3 (closer — one brain behind the wire, though the host still speaks GEM to the qualified wire hosted in-process): both were mis-scored ✅ before review. Alt 3's own review ([06](06-alt3-review.md)) further downgraded its crit-2 from a Session-0 "one service" claim to split hosting (egress GUI-independent, control interactive-session).

## 2.2 Recommendation

**Adopt Alternative 1 (Unified Gateway Facade) now; treat Alternative 3 (Unified Tool Gateway Service) as the target. Reject Alternative 2.**

**Why Alt 1 first.** It delivers the two wins that actually hurt today — a **single non-host external surface** and a **single supervised lifecycle** (spool draining + status survive the operator closing the GUI) — at the lowest risk, **without touching the control plane or the fab-qualified GEM wire**, and therefore with **no fab re-qualification**. It reuses the tested gateway pipeline. The four-reviewer adversarial review ([04-alt1-review.md](04-alt1-review.md)) confirmed the strategy is sound and narrowed the design to its safe core: it **removed external command relay** (a control entry point that can't be safely authorized in a facade), made the control coupling a **separate read-only least-privilege shim**, and elevated the **spool-drain/overflow fixes and :5005 hardening to U0 prerequisites**. With those in place it is the change you can ship inside the current architecture with confidence.

**Why Alt 3 is the target, not Alt 1 forever.** (Complete design, taken as the selected target: [05-alt3-complete-design.md](05-alt3-complete-design.md); adversarially reviewed in [06-alt3-review.md](06-alt3-review.md).) Alt 1 unifies the *non-host surface* but leaves two engines behind it and leaves the host on its own door; Alt 3 produces a *genuinely* unified, independently-supervised tool-gateway service with a clean internal API and the strongest maintainability outcome (**refined in [05](05-alt3-complete-design.md) §5.2/§5.6:** the buildable internal API is the *reporting* boundary, not a re-slice "in front of" the entangled PM/GEM wire — the control unit is carried in-process unchanged) — and **it is the exact component shape the later bus architecture needs** as its "ToolConnect citizen." **Honest accounting (per the earlier "why staged?" analysis):** Alt 1's lifecycle promotion + non-host single-surface groundwork carries forward, but Alt 1's read-only shim is *superseded* by Alt 3's internal control API — so the staged path is a **hedge against Alt 3's funding uncertainty**, not zero-rework. If Alt 3 is committed (funded + owner + appetite for the control-path change), consider going **straight to Alt 3** (still phased internally) and skipping the intermediate shim; if Alt 3 is uncertain, Alt 1 banks the value cheaply first.

**Why Alt 2 is rejected.** Merging the tool state machine and the native TSMC upload into one process couples the tool's most-critical function to its least-critical one (criteria #3 and #4 both fail), forces a COM/net48 ↔ net7 collision or a safety-relevant port, and is hard to reverse — the same reasoning that rejected the "merge into ToolManager" option in the earlier ToolGateway investigation. The maximum-unification prize is not worth the crash-domain and re-qualification costs.

## 2.3 Phased path (Alt 1 → Alt 3)

| Phase | Change | Risk | Reversible by |
|---|---|---|---|
| **U0 — Prep (hard prerequisites)** | Confirm §0 wiring; **fix spool drain (runtime replay) + overflow drain + poison counter** (gate G2); verify/fix **Fleet `ToolId=0`**; **harden :5005** (TLS/auth, reflection off); make AOI's process-sweep service-exclusive; installer ACL/ownership transition; spike the net48 status shim; name an owner | none | n/a |
| **U1 — Promote to a hardened supervised service** | ToolGateway as a least-privilege Windows service, always-restart, GUI-independent; spool drains + status survive GUI close. Child-launch kept as a **strictly-exclusive** fallback one release | low | flag → back to AOI child (overlap only) |
| **U2 — Declare the single *non-host* surface (Alt 1)** | Gateway = the one place non-host integrations attach; add the **read-only `ToolStatusShim` + :5007 status endpoint** (TLS+auth, minimized); route any new consumer as a sink. **No external command relay** | low-med | flag / remove the shim + endpoint |
| **U3 — Define the internal coordination↔egress API (Alt 3 groundwork)** | Introduce the *reporting-direction* internal API: the control process publishes tool-state events to egress (bounded, deadlined, fire-and-forget). **Not** a re-slice "in front of" PM/GEM — the code is too entangled ([05 §5.2](05-alt3-complete-design.md)); the control unit is carried in-process unchanged. Validated by GEM record-replay | med | dual-path behind flag |
| **U4 — Unify ownership + isolate native TSMC (Alt 3)** | One supervisor owns both the (interactive-session) control process and the (GUI-independent) egress service; tool-client watchdog added; native TSMC DLL isolated out-of-proc. Motion/GEM untouched in-process | med | atomic reconfiguration; keep the old path one release |

U0–U2 = Alternative 1, shippable on its own and delivering the headline value. U3–U4 = the promotion to Alternative 3, taken only when a full service consolidation is funded and an owner exists. Each phase is independently reversible.

## 2.4 Open items to confirm before U1

1. **Re-verify the §0 wiring in code** — ToolManager and ToolGateway have no direct link today; the exact host-command intake path (`SecsGemObjects\...\RemoteControl.cs`). *(The Alt 1 review verified much of this — see [04-alt1-review.md](04-alt1-review.md).)*
2. **Supervision mechanism — resolved in [03](03-alt1-complete-design.md):** Windows Service near-term (it already calls `UseWindowsService()`), least-privilege account, always-restart; a ToolHost child later if that program starts.
3. **`ToolGatewayEnabled` opt-in** — the unified surface implies it becomes standard. Confirm per-customer profiles; note `appsettings` currently bakes one tool's identity (needs templated config).
4. **The live gateway bugs (now U0 prerequisites, not just "fix first"):** the **spool overflow overwrite → unbounded silent loss** (raised to CRITICAL by the review) and the **missing runtime spool drain** (the always-on service defeats the accidental restart that hid it) — both **gate G2**; plus Fleet `ToolId=0` (verify Fleet's key first) and the unauthenticated `0.0.0.0:5005` + gRPC reflection (a security ship-blocker). Full list: [04-alt1-review.md](04-alt1-review.md) §"Live bugs".
5. **Fleet.Main tool key** — verify whether it keys on ToolId (then `=0` is CRITICAL) or machineName/IP (then cosmetic).

## 2.5 Relationship to the bus program

This unification is **not** a competitor to the fabric redesign in [../stage/](../stage/) — it is a safe, current-architecture step that *reduces* the eventual bus migration's risk: Alt 3's unified service is precisely the "ToolConnect gateway" the bus design already assumes. If the bus program is funded later, this work is absorbed, not discarded. If it is not, the tool still gets a single, well-supervised gateway. Either way it stands on its own.
