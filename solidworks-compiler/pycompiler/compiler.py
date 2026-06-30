"""Compiler orchestrator: validate -> (per node) resolve -> lower -> execute, threading
state_version through the run.

Failure model (CAD ops are NOT transactional): nodes run in declaration order; on the first
failure the run STOPS, no rollback is attempted, and the result reports how far it got plus a
feature-level error. The caller (the adapter tool) resyncs state_version afterwards either way.

Public API (consumed by pycompiler/__init__.py and the adapter):
    compile_and_run(port, graph) -> CompileResult
"""
from . import lowering
from .errors import FeatureError
from .ir_schema import validate
from .resolver import resolve_center_on_face, resolve_top_face


class CompileResult(object):
    """Outcome of one compile+run. Plain data so the adapter can render it however it likes."""

    def __init__(self):
        self.status = "COMPLETED"          # COMPLETED | FAILED
        self.nodes_total = 0
        self.nodes_completed = 0
        self.exec_calls = 0                # state-bumping ops actually executed
        self.node_log = []                 # [{id, type, status, ops:[{tool, note, state_version}]}]
        self.final_state_version = None
        self.error = None                  # {code, message, [node_id, node_type, step]}

    def to_dict(self):
        return {
            "status": self.status,
            "nodes_total": self.nodes_total,
            "nodes_completed": self.nodes_completed,
            "exec_calls": self.exec_calls,
            "final_state_version": self.final_state_version,
            "error": self.error,
            "node_log": self.node_log,
        }

    def summary(self):
        """One-line human-readable summary for the MCP result string."""
        if self.status == "COMPLETED":
            return ("COMPLETED | IR built %d/%d nodes | exec_calls=%d"
                    % (self.nodes_completed, self.nodes_total, self.exec_calls))
        err = self.error or {}
        if err.get("code") == "VALIDATION_FAILED":
            return "FAILED | VALIDATION_FAILED | %s | no execution performed" % err.get("message")
        node = err.get("node_id")
        step = err.get("step")
        return ("FAILED | %s | node %s (%s)%s: %s | completed %d/%d nodes before failure "
                "(partial geometry may remain — CAD ops are not transactional)"
                % (err.get("code"), node, err.get("node_type"),
                   (" step=%s" % step) if step else "", err.get("message"),
                   self.nodes_completed, self.nodes_total))


def compile_and_run(port, graph):
    result = CompileResult()

    # 1) Structural validation. If it fails, NO execution call is made.
    errors = validate(graph)
    if errors:
        result.status = "FAILED"
        result.error = {"code": "VALIDATION_FAILED", "message": "; ".join(errors)}
        return result

    nodes = graph["nodes"]
    result.nodes_total = len(nodes)

    # 2) Seed state_version from the authoritative source (also the basis for the post-run resync).
    try:
        sv = port.get_state()
    except Exception as ex:
        result.status = "FAILED"
        result.error = {"code": "STATE_SEED_FAILED", "message": str(ex)}
        return result

    # 3) Run nodes in order.
    for node in nodes:
        nid = node.get("id")
        ntype = node.get("type")
        rec = {"id": nid, "type": ntype, "status": "PENDING", "ops": []}
        try:
            ops = _plan_node(port, node, sv)  # may do a live (read-only) resolve for 'hole'
            for op in ops:
                resp = port.execute(op.tool, op.params, sv)
                result.exec_calls += 1
                sv = _advance(resp, sv, nid, ntype, op)
                rec["ops"].append({"tool": op.tool, "note": op.note, "state_version": sv})
            rec["status"] = "COMPLETED"
            result.node_log.append(rec)
            result.nodes_completed += 1
        except FeatureError as fe:
            rec["status"] = "FAILED"
            result.node_log.append(rec)
            result.status = "FAILED"
            result.error = {
                "code": fe.code, "message": fe.message,
                "node_id": fe.node_id or nid, "node_type": ntype, "step": fe.step,
            }
            result.final_state_version = sv
            return result
        except Exception as ex:  # never let a bug crash the host; report it as a clean failure
            rec["status"] = "FAILED"
            result.node_log.append(rec)
            result.status = "FAILED"
            result.error = {"code": "NODE_UNEXPECTED", "message": str(ex),
                            "node_id": nid, "node_type": ntype}
            result.final_state_version = sv
            return result

    result.final_state_version = sv
    return result


def _plan_node(port, node, state_version):
    ntype = node["type"]
    if ntype == "box":
        return lowering.lower_box(node)
    if ntype == "sketch":
        return lowering.lower_sketch(node)
    if ntype == "extrude":
        return lowering.lower_extrude(node)
    if ntype == "hole":
        face_index, info = resolve_top_face(port, state_version, node_id=node.get("id"))
        center2d = resolve_center_on_face(info)
        return lowering.lower_hole(node, face_index, center2d)
    raise FeatureError("unknown node type '%s'" % ntype, code="UNKNOWN_NODE_TYPE", node_id=node.get("id"))


def _advance(resp, sv, nid, ntype, op):
    """Read the resulting state_version, or map a low-level FAILED to a feature-level error."""
    status = resp.get("status")
    if status == "COMPLETED":
        nv = resp.get("stateVersion")
        return nv if nv is not None else sv
    if status == "DUPLICATE":
        nv = resp.get("last_known_state_version")
        return nv if nv is not None else sv
    err = resp.get("error") or {}
    raise FeatureError("low-level tool '%s' failed: %s" % (op.tool, err.get("message")),
                       code=err.get("code") or "TOOL_FAILED",
                       node_id=nid, node_type=ntype, step=op.tool)
