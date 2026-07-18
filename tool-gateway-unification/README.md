# Tool Gateway Unification — Plan

> **Scope:** the *current* architecture only — **no message bus**. How to unify the tool's two external-facing components — **ToolManagement** (the COM control/command plane) and **ToolGateway** (the .NET reporting/egress plane) — into **one "tool gateway."**
> This is a deliberately smaller, near-term problem than the fabric redesign in [../stage/](../stage/); it can ship independently and (in the recommended form) becomes a clean stepping-stone toward it.
> Grounding: verified component facts from this session's code investigations of `C:\CamtekGit\BIS\Sources` (`ToolManagement\ToolManager`, `Utilities\ToolGateway`). A fresh code check should re-confirm the wiring in §0 before implementation.

## Documents

| # | Doc | Contents |
|---|---|---|
| — | [executive-summary.md](executive-summary.md) | **Leadership entry point** — the problem, the two designed options, live bugs, recommended path, and relationship to the bus program |
| 0 | [00-problem-and-current-state.md](00-problem-and-current-state.md) | What "unify" means here, today's two components (verified), why they're separate, and the six success criteria |
| 1 | [01-alternatives.md](01-alternatives.md) | Three alternative designs — facade unification / co-hosted merge / unified service — each with a block diagram, what moves vs. stays, pros, cons, effort, reversibility |
| 2 | [02-recommendation.md](02-recommendation.md) | Comparison matrix and the recommended path, with the phased steps U0–U4 |
| 3 | [03-alt1-complete-design.md](03-alt1-complete-design.md) | **Complete design of Alternative 1 (Rev 2, post-review)** — "two doors" architecture, the three sub-designs (lifecycle promotion / single non-host surface / read-only status shim), flows, code sketches, migration phases U0–U2, risks |
| 4 | [04-alt1-review.md](04-alt1-review.md) | **Adversarial review of Alt 1** (feasibility / consistency / operations / security, grounded in `C:\CamtekGit`) — findings + resolutions that produced Rev 2; and the live shipped-code bugs |
| 5 | [05-alt3-complete-design.md](05-alt3-complete-design.md) | **Complete design of Alternative 3 — the target (Rev 2, post-review)** — one supervisor owning split hosting: a GUI-independent net7 egress service + a supervised interactive-session net48 control process; out-of-proc TSMC; enforced fire-and-forget coordination↔egress API; the interactive-session-ROT / Cimetrix-under-restricted-account spikes; phases U0–U4 |
| 6 | [06-alt3-review.md](06-alt3-review.md) | **Adversarial review of Alt 3** (feasibility / consistency / operations / security, grounded in `C:\CamtekGit`) — findings + resolutions, and the (carried-forward) shipped-code bugs |

## Audience map

- **Management / approval:** [executive-summary.md](executive-summary.md) → [02-recommendation.md](02-recommendation.md) §2.3 (the phased path).
- **Architecture review:** docs 0 → 1 → 2 → 3 + 4 → 5 + 6.
- **Implementers:** doc 0 (wiring baseline) → doc 3 (Alt 1, build first) or doc 5 (Alt 3, the target).

## TL;DR

- **The problem:** two components own the tool's outside world on two disconnected planes — ToolManager (COM state machine + host commands) and ToolGateway (gRPC reporting to Fleet/TSMC). They don't talk to each other today; adding an external integration touches both worlds.
- **Alternative 1 — Unified Gateway Facade:** the gateway becomes the tool's single *non-host* door (reporting + read-only status); the host keeps the GEM door ("two doors"); ToolManager stays the internal control engine, observed read-only via a separate least-privilege shim. Low risk, reversible. *(Adversarially reviewed → Rev 2: external command relay removed, control coupling made a separate read-only shim, spool-drain/security fixes elevated to prerequisites — [04-alt1-review.md](04-alt1-review.md).)*
- **Alternative 2 — Co-Hosted Merge:** physically merge both into one process. Maximum unification, but the control plane and the native TSMC upload share a crash domain — **not recommended**.
- **Alternative 3 — Unified Tool Gateway Service:** one supervisor owning both the tool's coordination and all non-host external I/O, with the fab-qualified GEM/motion internals carried in-process **unchanged** (the code is too entangled to re-slice — [05](05-alt3-complete-design.md) §5.2), native TSMC pushed out-of-proc, and the coordination↔egress internal API kept to the *reporting* direction only. The review forced **split hosting** — a GUI-independent egress service plus a supervised **interactive-session** control process (the control unit is UI-bearing and per-session-ROT-bound, so it is *not* a Session-0 boot service). More work, genuinely unified, forward-compatible with the bus — feasibility turns on two U0 spikes (interactive-session ROT bind, Cimetrix under a restricted account). *(Complete design [05](05-alt3-complete-design.md), taken as the selected target; adversarially reviewed → [06](06-alt3-review.md).)*
- **Recommendation:** **Alternative 1 now** (achieves "one tool gateway" cheaply and safely) → **Alternative 3 as the target** when a real service consolidation is funded. **Alternative 2 rejected** on crash-domain and framework grounds.
