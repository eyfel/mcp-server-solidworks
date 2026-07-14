# solidworks-compiler — Architecture

Role: **Deterministic CAD Compiler (SolidWorks-specific)**

This layer sits **between the Feature Graph IR and the Execution Layer**. It is **deterministic — it contains NO LLM and NO AI calls.** It is the "second planner," but a deterministic, reproducible one (this is intentional, not the thing we were avoiding — what we avoid is a *second LLM* re-doing the host model's reasoning).

```
Feature Graph IR  ──▶  solidworks-compiler  ──▶  Execution Layer (REST)  ──▶ SolidWorks
                       (compiler + resolver)
```

## Responsibilities

- Lower a validated CAD-neutral **Feature Graph IR** into an ordered sequence of concrete execution-layer tool calls (`create_sketch`, `add_sketch_entity`, `extrude_feature`, …).
- **Reference resolution** (the hard, make-or-break part): turn semantic references — `top_face`, `center`, `concentric to edge X` — into concrete coordinates / selectors against the **live B-rep state**, by querying `verify_state` / `analyze_model`.
- **Geometric validation** at compile/exec time: referenced face exists after prior features, hole fits, a selector resolves to exactly one entity.
- Map low-level execution failures back up to a **feature-level** error the Planner/host can reason about (so recovery isn't lost below the IR).

## Constraints

- No SolidWorks COM access (only the Execution Layer touches COM).
- No MCP. The compiler calls the Execution Layer over **plain REST** — MCP is the *top* boundary (host → IR), never an internal transport.
- Deterministic: same IR + same starting state ⇒ same tool sequence.
- SolidWorks-specific. A different CAD backend = a different compiler (e.g. `fusion-compiler`), with the IR and `cad-planner` unchanged.

## Internal modules (target)

| Module | Job |
|---|---|
| Lowering | IR node → ordered tool calls for a fixed feature vocabulary |
| Reference Resolver | semantic ref → concrete selector/coords via live state queries — **isolate ruthlessly; this is where the project lives or dies** |
| Geometric Validator | compile/exec-time feasibility checks |
| Failure Mapper | low-level tool error → feature-level error |

## Status

**Implemented as `pycompiler/` (working v0).** A pure Python package behind an injected
`ExecutionPort` (no MCP, no COM, no HTTP of its own) that runs in-process in the adapter today
and is extractable to a standalone service later. Modules map to the table above:
`lowering.py`, `resolver.py` (geometric anchors v0 — replay-exact, edit-fragile),
`ir_schema.py` (structural validation), `compiler.py` (orchestration + feature-level failure
mapping), with an offline test suite (`pycompiler/tests/`, fake port — runs in CI, no
SolidWorks needed). Reached in production through the adapter's `rebuild_from_ir` tool; the
direct `submit_feature_graph` door exists as a commented-out test tool. Both doors run this
same compiler. The low-level MCP tools remain the primary build path until the vocabulary and
a durable (edit-surviving) reference resolver are ready.
