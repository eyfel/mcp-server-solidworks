# adapters/claude — Architecture

Role: **MCP Adapter / Protocol Bridge (Claude)**

The communication bridge between the AI host (Claude) and the system. Pure translation/routing — no CAD logic, no planning logic.

## The MCP boundary sits at the TOP

This is the key architectural decision: **MCP is the boundary where the AI host meets the system — not an internal transport.**

- **Target (Feature Graph architecture):** the adapter exposes **one** high-level tool, `submit_feature_graph(graph)` (plus a few read-only tools like `verify_state`). The host model (Claude) emits the CAD-neutral IR; everything below the IR (compiler → resolver → execution) is deterministic and is reached over plain REST, **not** MCP.
- **Phase 1 (current, transitional):** the adapter still exposes the low-level tools directly (38 today, plus adapter-only orchestration tools like `save_analysis` / `rebuild_from_ir` / `get_recipe`) so the working end-to-end path keeps running while the IR vocabulary grows. The low-level surface collapses into `submit_feature_graph` once the vocabulary and resolver are ready.

```
Target:   Claude ──MCP: submit_feature_graph(graph)──▶ [adapter] ──REST──▶ compiler ──▶ execution
Phase 1:  Claude ──MCP: 38 low-level tools──────────▶ [adapter] ──REST──▶ execution
          Claude ──MCP: rebuild_from_ir──────────────▶ [adapter] ─(in-process pycompiler)─REST──▶ execution
```

## Responsibilities

- Implement the MCP protocol server (FastMCP, stdio).
- Translate host tool calls into Execution Layer REST requests.
- Generate `operation_id` (UUID4) per call; track `state_version`.
- Map `ExecutionResponse` → MCP result.

## Constraints

- No CAD logic, no planning logic, no COM.
- Provider-specific shell only — the reusable bridge core (id/version/mapping/client) should be factored out so additional adapters (OpenAI, etc.) reuse it instead of re-implementing.

## Model-facing guidance lives HERE, not in the contracts

The host model only ever sees the **MCP tool definition** (docstring → description, type hints → input schema). It does **not** see `tool-schemas.json`. Therefore tool-usage guidance that prevents the model from sending bad data (enums, units, required params, value ranges) must be encoded in the MCP layer — via `Literal` types and Pydantic `Field` constraints — and `tool-schemas.json` must be kept in sync by a contract test, not hand-maintained in parallel (see `tests/test_schema_contract.py`; it runs in CI on every push).
