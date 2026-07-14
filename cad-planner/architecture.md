# cad-planner — Architecture

Role: **AI Planning / Intent Layer (CAD-NEUTRAL)**

> Renamed from `solidworks-planner`. It is deliberately **CAD-neutral** — it knows feature *intent*, not SolidWorks. The SolidWorks-specific work lives in `solidworks-compiler` + `solidworks-execution`.

This layer turns user intent into a **CAD-neutral Feature Graph IR**. It operates at the **CAD feature level, NOT the tool level** — the shift that defines this architecture:

- ❌ old: "which tool do I call?"
- ✅ new: "which CAD intent am I realizing?"

```
User intent ─▶ Planner (intent → Feature Graph IR) ─▶ [IR] ─▶ solidworks-compiler ─▶ execution ─▶ SolidWorks
```

## Phase 1 vs Phase 2 (important)

- **Phase 1 (now): the Planner is the host model (Claude) + the IR schema + a planning prompt.** There is **no separate running planner service.** Claude does intent→IR reasoning itself and submits the graph through the single MCP tool `submit_feature_graph(graph)`. This repo therefore holds a *contract + instructions*, not a server.
- **Phase 2 (later, after local infra): a dedicated/local-LLM planner** for near-zero usage cost. Same IR contract, different runtime.

## Responsibilities

- Convert user intent into CAD-neutral **feature plans** (Feature Graph IR).
- Decompose complex design goals into feature-level operations.
- Resolve required capabilities from the **capability registry** — which is the **same artifact as the IR schema** (`contracts/feature-graph.schema.json`); no separate discovery protocol.
- Validate feature dependencies **structurally** (valid types, params present, node references resolvable).
- Handle ambiguity and planning-level recovery.

## Constraints

- Does NOT interact with SolidWorks directly.
- Does NOT execute CAD operations.
- Does NOT generate raw API/tool sequences (that is the compiler's job).
- Does NOT perform **geometric** validation (face existence, fit, selector uniqueness) — that needs live B-rep state and is owned by the compiler/resolver/execution.
- CAD-neutral: no SolidWorks-specific assumptions leak into the IR (backend-specific params go in an explicit `backend_ext` escape hatch, not the neutral core).

## Output

- A structured **Feature Graph / CAD execution plan** conforming to `contracts/feature-graph.schema.json`.

## Contracts

- `contracts/feature-graph.schema.json` — the neutral IR **and** capability registry (single source of truth, DRAFT v0).
- The SolidWorks-specific low-level contracts (`tool-schemas.json`, `state-format.json`, `execution-response.json`) now live under `solidworks-execution/contracts/` — the Planner does not depend on them.

## Open design question (flagged, not solved)

The **reference model** (how nodes name/refer to geometry produced by earlier nodes — the persistent/topological naming problem) is the hardest part of the IR. v0 supports datums, simple semantic selectors, and geometric anchors (replay-exact, edit-fragile). **Grow the vocabulary from real production designs, not ahead of them.**
