"""Adapter-side bridge from the deterministic pycompiler to the LIVE execution layer.

This is the only place that wires the two together. It:
  - puts solidworks-compiler/ on sys.path (the dir is hyphenated, so it can't be imported by name)
    and imports the pure pycompiler package;
  - implements pycompiler's ExecutionPort over the EXISTING execution_client — the SAME
    /api/tool/execute + /state endpoints and the SAME idempotency / state_version machinery the
    low-level tools use (nothing forked, per the additive constraint).

pycompiler itself imports nothing from the adapter (no FastMCP / httpx / server.py) — that one-way
dependency is what keeps the compiler relocatable to a standalone service later (IR-ADR-003).
"""
import os
import sys
import uuid

_HERE = os.path.dirname(os.path.abspath(__file__))
_COMPILER_ROOT = os.path.normpath(os.path.join(_HERE, "..", "..", "solidworks-compiler"))
if _COMPILER_ROOT not in sys.path:
    sys.path.insert(0, _COMPILER_ROOT)

from pycompiler.compiler import compile_and_run          # noqa: E402
from pycompiler.execution_port import ExecutionPort       # noqa: E402

from execution_client import call_tool, get_state          # noqa: E402


def _is_state_mismatch(resp):
    if resp.get("status") != "FAILED":
        return False
    return (resp.get("error") or {}).get("code") == "INVALID_STATE_VERSION"


class AdapterExecutionPort(ExecutionPort):
    """Concrete ExecutionPort over the live execution REST layer.

    Owns transport robustness: one resync+retry on INVALID_STATE_VERSION (mirrors server._call),
    then returns the final response so the compiler threads the resulting state_version. A fresh
    operation_id per sub-op keeps each individually idempotent on the server's OperationGuard.
    """

    def execute(self, tool, params, state_version):
        resp = call_tool(tool, str(uuid.uuid4()), state_version, params)
        if _is_state_mismatch(resp):
            fresh = get_state()
            resp = call_tool(tool, str(uuid.uuid4()), fresh, params)
        return resp

    def get_state(self):
        return get_state()


def run_feature_graph(graph):
    """Compile + run a Feature Graph IR (dict) against the live execution layer.

    Returns a pycompiler CompileResult. Does NOT touch the adapter's state_version global — the
    caller (server.submit_feature_graph) resyncs that afterwards via GET /state (first-class).
    """
    return compile_and_run(AdapterExecutionPort(), graph)
