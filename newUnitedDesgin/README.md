# newUnitedDesgin — Fresh Design Alternatives for Tool-Gateway Unification

> **Status: EXPLORATORY DRAFT.** These designs complement — and are deliberately different from — the reviewed set in
> [../tool-gateway-unification/01-alternatives.md](../tool-gateway-unification/01-alternatives.md) (Alt 1 facade · Alt 2 merge · Alt 3 unified service).
> They answer the same problem statement ([00-problem-and-current-state.md](../tool-gateway-unification/00-problem-and-current-state.md))
> and are scored against the same **six success criteria** (§0.4). They have **not** been through adversarial review.

---

## The angle: four *different axes* of unification

The existing alternatives all unify along the **hosting axis** (how many processes / who hosts what):
surface (Alt 1) → process (Alt 2) → service (Alt 3).

These four designs unify along **other axes** entirely:

| # | Design | Unifies by… | One-line idea |
|---|--------|-------------|---------------|
| D1 | [01-design-event-spine.md](01-design-event-spine.md) | **Contract** | A localhost streaming **event spine** (mini-bus precursor) — every producer publishes one envelope; every external system is a subscriber. The gateway *is* the spine host. |
| D2 | [02-design-cqrs-projection-gateway.md](02-design-cqrs-projection-gateway.md) | **Data model** | **CQRS split**: commands stay on the GEM wire, untouched; the gateway becomes the single durable **read-model / projection** of tool state that every non-host consumer reads from. |
| D3 | [03-design-microkernel-connectors.md](03-design-microkernel-connectors.md) | **Plugin kernel** | A tiny supervisor **kernel** (lifecycle, health, spool, routing) + **process-isolated connector plugins** (Fleet, TSMC, MES…). Adding an integration = dropping a connector, zero core change. |
| D4 | [04-design-com-tap-bridge.md](04-design-com-tap-bridge.md) | **Existing rails** | **Zero-touch tap**: the gateway subscribes to the COM event hub that already exists (`FalconWrapper` / `ToolEvents`) — ToolManager is not modified at all, yet its events reach the single surface. |

They are **composable, not mutually exclusive**: D4 is a Wave-0 tactic that feeds D1 or D2; D3 is a hosting pattern that D1/D2 can adopt for their sink side. A pragmatic program is **D4 → D1 (+D3 hosting)**, with D2's projections added where consumers need state-as-of-now rather than event streams.

## Scoring snapshot vs the six criteria (§0.4)

| Criterion | D1 spine | D2 CQRS | D3 kernel | D4 tap |
|---|---|---|---|---|
| 1. Single non-host surface | ✅ (the spine) | ✅ (the read model) | ✅ (the kernel) | ✅ (gateway, sooner) |
| 2. Single lifecycle, GUI-independent | ✅ service | ✅ service | ✅ service | ✅ (requires service promotion, as Alt 1 U0) |
| 3. Control core / GEM untouched | ✅ (tap-side only) | ✅ (observe-only) | ✅ (egress only) | ✅✅ (zero code change in TM) |
| 4. Native-DLL blast radius | ✅ (sink worker) | ✅ (sink worker) | ✅✅ (core of the design) | ➖ inherits today's in-proc sink unless combined with D3 |
| 5. Reversible / flagged | ✅ | ✅ | ✅ | ✅✅ (unplug the tap) |
| 6. Forward-compatible with the bus | ✅✅ (envelope = bus subset) | ✅ (journal = bus journal shape) | ✅ (connectors = bus consumers) | ✅ (tap becomes a bus publisher) |

## Diagram marking convention

Every architecture diagram (and "what moves / what stays" table) in this folder tags each component, both by **color** and by a **text label** (so it survives renderers without color):

| Mark | Meaning |
|---|---|
| 🟩 **NEW** (green fill) | Component that does not exist today |
| 🟨 **MODIFIED** (yellow fill) | Existing component that is re-homed, extracted, extended, or promoted |
| ⬜ **EXISTING** (gray fill) | Untouched component — exactly as it runs today |

A yellow/green **subgraph border** means the *hosting* is new/changed even where the contents are existing code (e.g. today's tested sink pipeline promoted into a Windows service).

## Boundaries all four respect (project invariants)

- The **factory host keeps the fab-qualified GEM wire** — "two doors" per criterion 1's stated exception. No design routes the host through the gateway.
- **ToolManager stays net48/COM**; anything in-proc with it is C# 7.3-compatible. New services are net8.
- **No fab re-qualification** in the core change: nothing alters GEM wire content or timing.
- Everything ships **behind `system.ini` flags** with rollback to today's child-process gateway.
- Nothing contradicts the funded bus target in [../stage/](../stage/) — each design names its bus-migration story explicitly.
