"""pycompiler — deterministic Feature Graph IR compiler (EXPERIMENTAL, P1.4/P1.7).

Lowers a CAD-neutral Feature Graph IR (cad-planner/contracts/feature-graph.schema.json,
v0-exp subset) into ordered calls to the EXISTING solidworks-execution low-level tools, and
resolves semantic references (top_face / center) against live geometry.

Architectural rules (see solidworks-compiler/architecture.md + master-architecture.md):
  - Deterministic. NO LLM, NO MCP, NO COM.
  - Reaches the execution layer ONLY through an injected ExecutionPort (plain REST behind it),
    never by importing the adapter, FastMCP, or httpx. This is what keeps the module separable:
    moving it into a standalone REST service later is a transport swap behind ExecutionPort,
    with the lowering / resolver / validation logic untouched (IR-ADR-003).

This is the experimental IR path. It coexists with the 26 low-level tools and does NOT change
them. See logs-ir.md for the ledger (IR-ADR-NNN).
"""

from .compiler import compile_and_run, CompileResult  # noqa: F401
