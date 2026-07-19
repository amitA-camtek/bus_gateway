---
description: Run a multi-reviewer adversarial architecture review, grounded in real code, then fix and re-verify until no critical/major findings remain.
argument-hint: <path(s) to design doc(s)> [| codebase root] [| dimensions]
allowed-tools: Read, Grep, Glob, Bash, Edit, Write, Agent, TodoWrite
---

# Adversarial Architecture Review

You are running a rigorous, evidence-based adversarial review of a design document (or a set of them), the way a strong architecture board plus a red team would. Grounding in the **real codebase** is mandatory — findings must cite `file:line`, not opinion.

## Inputs (from `$ARGUMENTS`)

Parse arguments, separated by `|`:
1. **Target** (required): path(s) or glob to the design document(s) under review.
2. **Codebase root** (optional): where to verify claims (e.g. `C:\CamtekGit\BIS\Sources`). If omitted, ask once, then proceed read-only.
3. **Dimensions** (optional): comma-separated subset of the review dimensions below. If omitted, auto-select the dimensions that fit the design (state which and why).

If the target is missing, ask for it and stop. Do not invent a target.

## Review dimensions (pick the relevant ones)

| Dimension | Reviewer lens |
|---|---|
| **consistency** | Internal contradictions, cross-doc drift, diagrams vs tables vs prose, undefined terms |
| **feasibility** | Every load-bearing claim checked against real code (`file:line`); WRONG/SHAKY/HOLDS verdicts |
| **operations** | 3am-in-production: failure modes, rollout/rollback realism, config/fleet management, survivability |
| **concurrency** | Deadlock, starvation, races, reentrancy, latency bounds, ordering, teardown |
| **connectivity** | Every connection's lifecycle, outage behavior, reconnect, partial-connectivity states, endpoint config |
| **load** | Load model, burst/storm behavior, buffer-sizing coherence, throughput ceilings, backpressure |
| ~~**security**~~ | **EXCLUDED — never spawn a security reviewer.** Security-relevant observations that surface incidentally in other dimensions may still be recorded as findings |
| **data-integrity** | Loss/duplication windows, provenance/traceability, ordering guarantees end to end |
| **test-strategy** | Is the stated test coverage sufficient? Composite-fault scenarios, qualification of test tooling |

## Protocol (run in rounds until clean)

1. **Orient.** Read the target doc(s) fully. Check codebase connectivity. Build a TodoWrite list. Do NOT start reviewing before reading.

2. **Fan out — one reviewer agent per dimension, in parallel** (single message, multiple `Agent` calls). **All reviewer agents run with `model: fable`.** Never include the security dimension in the fan-out (even if requested in the arguments — state that it was skipped). Each agent prompt must:
   - Name the dimension and its adversarial lens.
   - Point at the target doc(s) AND the codebase root — instruct it to **verify claims against real code with `file:line` evidence**, not to trust the doc.
   - Demand findings **ranked CRITICAL / MAJOR / MINOR**, each with: a concrete failure scenario (inputs/interleaving → wrong outcome), the doc section it applies to, and a **specific suggested fix**.
   - Ask for a top-3 must-fix list and a READY / NOT-READY verdict.
   - Be adversarial and specific — reject generic advice.

3. **Consolidate.** Write a **review record** markdown file (`<target-dir>/<name>-review.md`): every CRITICAL and MAJOR finding with an ID, the reviewer, and the **resolution you decide** for each. Deduplicate overlaps. Record any reviewer conflicts and how you broke the tie. List separately any **live bugs found in shipped code** (these are work items independent of the design).

4. **Fix.** Apply every critical/major resolution to the design doc(s). Keep companion docs consistent (if two docs cover one topic, one is normative, the other cross-references). Preserve a "superseded" banner on any historical doc rather than silently rewriting it.

5. **Verify (new round).** Spawn a verification agent (`model: fable`): confirm each recorded resolution actually landed, and hunt for NEW contradictions the fixes introduced. Fix what it finds.

6. **Iterate** steps 4–5 until the verifier returns no critical/major findings. Record the final verdict and any **standing conditions** (things only a human/experiment can close) in the review record.

## Rules

- **Evidence over assertion.** A finding without a concrete failure scenario or a code citation is a comment, not a finding — downgrade it.
- **Diagrams:** if the design uses Mermaid, remember sequence-diagram messages/notes cannot contain `;`, `<`, or `\n` (use `<br/>`); flowchart node labels may use `\n`. Fix parse-breakers you introduce.
- **Don't touch production code.** This is a *design* review — verify against code read-only; never edit the codebase.
- **Report faithfully.** If a claim in the design is contradicted by the code, say so plainly with the citation, even if it weakens the design.
- **Scope the fan-out to the design's size.** A small design gets 2–3 reviewers; a large multi-doc architecture gets one agent per relevant dimension.

## Output

At the end, report: the dimensions run, counts of CRITICAL/MAJOR/MINOR found and resolved, any live-code bugs discovered, the final READY/NOT-READY verdict, and the standing conditions that need human sign-off. Point to the review-record file.

---

### Example invocations

```
/adversarial-arch-review stage/01-system-architecture.md
/adversarial-arch-review stage/*.md | C:\CamtekGit\BIS\Sources
/adversarial-arch-review stage/06-bus-implementation.md | C:\CamtekGit\BIS\Sources | concurrency,security,load
```
