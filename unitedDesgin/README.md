# unitedDesgin — Out-of-the-Box Unification Design Studies

> ⚠ **Status: exploratory design studies — not normative.** The decided path for tool-gateway
> unification remains [../tool-gateway-unification/02-recommendation.md](../tool-gateway-unification/02-recommendation.md)
> (Alt 1 now → Alt 3 target). These four designs answer a different question: *are there
> unification axes the three reviewed alternatives did not explore?* None has been through
> adversarial review. If one is adopted, it must go through the same review cycle as Alt 1/Alt 3
> and be reconciled with the normative set.

---

## The premise

The three reviewed alternatives ([../tool-gateway-unification/01-alternatives.md](../tool-gateway-unification/01-alternatives.md))
all unify along the **process/component** axis — they differ only in *how much code moves*:

| Existing alternative | Axis | How much moves |
|---|---|---|
| Alt 1 — Facade | surface | nothing (a shim is added) |
| Alt 2 — Co-hosted merge | process | everything (rejected) |
| Alt 3 — Unified service | service + coordination | the reporting boundary |

But "the tool has two disconnected external-facing components" can be attacked on axes that
don't move control code at all. Each design here unifies on **one different axis**, and each
respects the same project boundaries as the reviewed set: the fab-qualified GEM wire is
untouched, the control core (state machine, ProductionManager, EFEM/motion) is not
destabilized, `TsmcClientShim.dll` stays out of the control crash domain, everything ships
behind a flag, and nothing paints us into a corner versus the later bus architecture.

## Reading the diagrams — NEW vs existing

Every architecture diagram color-codes and text-tags its nodes so new work is easy to spot:

| Marker | Colour | Meaning |
|--------|--------|---------|
| 🟩 **NEW** | green | New component built by this design |
| 🟧 **REUSED / CHANGED** | amber | Existing code re-hosted unchanged, or an existing component with a behavior-only change |
| 🟥 **RETIRED** | red | Existing component removed at the end state (Design D only) |
| ⬜ (no tag) | default | Unchanged / external / future |

The tag is written **inline in each node's label** as well as coloured, so it still reads in a
terminal that doesn't render mermaid `classDef` colours. In every design's "What moves / what
stays" table, the **"Moves / is added"** column is the exhaustive list of 🟩 NEW items.

## The four designs

| # | Design | Unification axis | One-line idea |
|---|--------|------------------|---------------|
| A | [01-journal-first-gateway.md](01-journal-first-gateway.md) | **Data plane** | The gateway is not a process — it is a durable local journal. All producers append; one pump service drains to the world. |
| B | [02-semantic-model-unification.md](02-semantic-model-unification.md) | **Semantic plane** | Unify the *vocabulary*, not the processes: every externally-visible event is declared once in one equipment model and emitted to both wires. |
| C | [03-toolio-supervisor.md](03-toolio-supervisor.md) | **Ops plane** | Accept two engines; unify everything the outside world *operates*: one supervisor service, one installer, one config root, one health endpoint. |
| D | [04-strangler-toolconnect.md](04-strangler-toolconnect.md) | **Target-first** | Don't design another interim thing — stand up the bus program's ToolConnect gateway *now*, fed directly, and strangle ToolGateway lane by lane. |

## Scored against the six success criteria

(Criteria from [../tool-gateway-unification/00-problem-and-current-state.md §0.4](../tool-gateway-unification/00-problem-and-current-state.md).)

| Criterion | A — Journal | B — Semantic | C — Supervisor | D — Strangler ToolConnect |
|---|---|---|---|---|
| 1. Single non-host external surface | ✅ one pump owns all non-host egress + status ("two doors" — host keeps GEM) | ⚠️ no — transports unchanged; the *definition point* becomes single | ⚠️ partial — one advertised unit, but still ToolGateway's ports behind it | ✅ ToolConnect is the single non-host surface at end state ("two doors") |
| 2. Single lifecycle & supervision | ✅ pump is a GUI-independent service | ❌ not addressed (pair with U1 or Design C) | ✅ this *is* the criterion, delivered directly | ✅ ToolConnect is a supervised service |
| 3. Control core protected | ✅ control gains only an optional fire-and-forget file append | ⚠️ facade wraps qualified call sites — pass-through, byte-identical wire, but it *touches* the path (record-replay gate) | ✅ untouched entirely | ✅ untouched (intake adapter only) |
| 4. Native-DLL blast radius | ✅ TSMC in an isolated child of the pump | ⚠️ unchanged from today (inherit U-phase fix) | ✅ TSMC isolate is a first-class supervised child | ✅ per stage/07 failure matrix |
| 5. Reversible | ✅ flag: append vs gRPC push; stop pump → old gateway resumes | ✅ per-event flags; facade is a wrapper, removable | ✅✅ highest of all — config change only | ⚠️ shadow phase reversible; post-cutover = redeploy |
| 6. Forward-compatible with bus | ✅✅ the journal *is* the bus envelope + durability, delivered early | ✅ canonical event schema is exactly what bus messages need | ✅ the supervisor is the stage ToolHost pattern, delivered early | ✅✅ maximal — the component *is* the bus citizen; swap intake source |
| **Effort** | M | S–M | **S** | M–L |
| **Fab re-qual** | none | none claimed — but the facade wraps GEM call sites, so **must be proven** by record-replay | none | none |

## How they compose (they are not all rivals)

- **C (Supervisor) composes with everything.** It is the smallest shippable step and is the
  natural host for A's pump, D's ToolConnect, and today's ToolGateway alike. C first is a
  no-regret move.
- **B (Semantic) composes with everything.** It fixes the "integrations touch two worlds"
  pain at the *definition* level regardless of which egress engine wins. It is the only design
  here that attacks pain point "two mental models" at its root.
- **A vs D are rivals** — both replace ToolGateway's engine. A builds the *durability* of the
  bus early (journal-first); D builds the *component* of the bus early (ToolConnect-first).
  Choose one.
- Suggested out-of-the-box composite: **C → B → D** (supervise, unify vocabulary, then
  strangle toward the bus citizen) — or **C → A** if the bus program stalls and journal
  durability is the more urgent win.

## Relationship to the decided path

These studies do not overturn [../tool-gateway-unification/02-recommendation.md](../tool-gateway-unification/02-recommendation.md).
Mapping: Design C ≈ a leaner U1 (lifecycle win without promoting ToolGateway itself);
Design D ≈ going "straight to Alt 3" but reusing the already-reviewed stage/07 design instead
of writing a new one; Designs A and B have no equivalent in the reviewed set — they are the
genuinely new axes.
