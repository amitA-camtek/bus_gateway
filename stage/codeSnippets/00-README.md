# codeSnippets — Design-level C# sketches for Falcon Tool Fabric

> Consolidated and expanded from the inline snippets in the stage design set.
> Every file cites the design section(s) it realizes.
> Status: **design sketches** — enough to drive implementation, not production-ready code.
>
> **Sketch status.** The 5th-cycle sketch bugs **S-1..S-18 are fixed** in these files (verified — [../stage-review.md](../stage-review.md) "Applied in this pass"). Deliberate divergences from contested mechanisms are marked `TODO(R-x)`/`TODO(X7-x)` in-file and the design docs remain **normative** where they differ. The 7th cycle (gateway focus) further updated the contracts sketches (dedup key `(source,epoch,topic,seq)`, `durableSubscribers`, deny-default ACL, `PrevState`, the `tool.state.replay` ring, the fan-out-outside-lock pattern in snippet 09); the gateway sketch (12) still carries the ack-coupled `OnScanCommitted` under an explicit `TODO(R-4/X7-1..3)` pointing to [07 §7.4–7.5](../07-toolconnect-design.md), and the GEM sketch (13) carries the HCACK-0-style async accept under `TODO(X7-8)` pointing to [§1.3.4](../01-system-architecture.md) (the real accept path is HCACK=4 / `eCmdPerformLater`) — **build the gateway from doc 07 and the GEM accept path from §1.3.4, not from the sketches.**

## Contents

| File | Component | Target | Design sections |
|------|-----------|--------|-----------------|
| [01-bus-contracts.cs](01-bus-contracts.cs) | `Camtek.Messaging.Contracts` | net48;net8 | §1.4, §6.2, §6.3, §03-lanes Lane-A |
| [02-bus-client-api.cs](02-bus-client-api.cs) | `Camtek.Messaging` public API | net48;net8 | §6.2 |
| [03-bus-client-internals.cs](03-bus-client-internals.cs) | `Camtek.Messaging` internals | net48;net8 | §6.4, §6.5, §6.7 |
| [04-broker.cs](04-broker.cs) | `Camtek.Messaging.Broker` | net8 | §1.3.1, §6.6 |
| [05-bus-adapter.cs](05-bus-adapter.cs) | AOI_Main `BusAdapter` | net48 | §2.2 |
| [06-ui-marshaller.cs](06-ui-marshaller.cs) | AOI_Main `UiMarshaller` | net48 | §2.3 |
| [07-tool-state-reactions.cs](07-tool-state-reactions.cs) | AOI_Main `ToolStateReactions` + `stateSeq` | net48 | §2.4 |
| [08-service-clients-seam.cs](08-service-clients-seam.cs) | AOI_Main `ServiceClients` seam | net48 | §2.5 |
| [09-toolmanager-producer.cs](09-toolmanager-producer.cs) | `ToolEvents.cs` dual-publish + `stateSeq` lock | net48 | §03-lanes Lane-A P3, §04 §4.2 |
| [10-frm-scan-tab-publish.cs](10-frm-scan-tab-publish.cs) | `frmScanTab` publish hooks + `clsInitAOI` startup | net48 | §2.6, §2.7 |
| [11-external-control-p4.cs](11-external-control-p4.cs) | `ExternalControlCbUiWrapper` in-proc dispatch + compensation | net48 | §03-lanes Lane-D, §04 P4 |
| [12-gateway-additions.cs](12-gateway-additions.cs) | Gateway `BusSource` + `CommandPublisher` + spool fixes | net8 | §1.3.2, **07-toolconnect-design.md** (normative for internals), §05 LB1/LB2/LB5 |
| [13-gem-shim.cs](13-gem-shim.cs) | GEM bus shim + degraded contract | net48 | §1.3.4 |
| [14-toolhost.cs](14-toolhost.cs) | `Camtek.ToolHost` supervisor + health API | net8 | §1.3.3 |
| [15-lane-c-absorption.cs](15-lane-c-absorption.cs) | Lane C: RobotUI absorption seam + event bridge | net48 | §03-lanes Lane-C |
| [16-live-bug-fixes.cs](16-live-bug-fixes.cs) | LB1–LB5 minimal fixes (independent of the program) | net48/net8 | §05 §5.5 |

## Framework idiom split

- **`net48` files** — C# 7.3: no records, no `new()` target-typed, no switch expressions, no nullable annotations, no file-scoped namespaces. `default(T)` explicit. ValueTuples OK.
- **`net8` files** — C# 12: modern idioms allowed (records, nullable refs, primary constructors, switch expressions).

## Intentionally omitted

Two new projects are mentioned in the design but have no dedicated snippet file — they are gRPC-host boilerplate or assertion lists, not design-critical sections:

- **`Camtek.ToolServices.Host` (:5060)** — gRPC host wrapping existing ToolManager services; a thin `GenericHost` + service registration, no novel design contract to snapshot.
- **`Camtek.Messaging.TestKit`** — its component design (`BusHarness` / `TopicCaptor` / `ReplyStub` / `FaultScript`, with a class diagram and usage flow) is specified in [06 §6.10](../06-bus-implementation.md); no snippet duplicates it.

## Relationship to stage docs

These files consolidate the inline `csharp` blocks scattered through docs 02, 03, and 06.
Where the design specifies a contract exactly, these files reproduce it verbatim.
Where detail is unspecified, minimal `// …` stubs preserve the shape without inventing machinery.
