# solidworks-execution — Architecture

Role: **Execution Layer / Truth Engine (SolidWorks-specific)**

Tech: C# .NET Framework 4.8, ASP.NET Web API 2 (OWIN self-host)

The deterministic execution core. **The only layer allowed to touch the SolidWorks COM API.**

Position in the system:
```
… ─▶ solidworks-compiler ──REST──▶ solidworks-execution ──COM──▶ SolidWorks
```
The compiler (deterministic) drives this layer over plain REST. In Phase 1 the MCP adapter also drives it directly with the low-level tools while the IR vocabulary grows. All COM work runs serialized on a single dedicated STA thread (`StaExecutor`).

## Responsibilities

- Direct interaction with the SolidWorks API.
- Execution of CAD operations.
- State management (documents, sketches, features) — single source of truth for CAD state.
- Input validation and safety checks.
- REST endpoints for all tool operations (`POST /api/tool/execute`).
- Idempotency + `state_version` optimistic concurrency (`OperationGuard`).

## Constraints

- Contains NO AI logic and NO planning logic.
- Must be fully deterministic; all operations synchronous and verified before response.
- Adapter-agnostic and compiler-agnostic — zero knowledge of who is calling it.

## Contracts

The SolidWorks-specific low-level contracts now live **here** (moved from the old `solidworks-planner/`):
```
solidworks-execution/contracts/
  tool-schemas.json        ← input/output schema for every low-level CAD tool
  state-format.json        ← CadState structure
  execution-response.json  ← COMPLETED / FAILED / DUPLICATE shapes
```
The CAD-neutral Feature Graph IR (`cad-planner/contracts/feature-graph.schema.json`) compiles **down** to these tools — the execution layer itself is unaware of the IR.
