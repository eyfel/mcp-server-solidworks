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
from .resolver import (resolve_center_on_face, resolve_component_entity_by_anchor,
                       resolve_edges_by_anchor, resolve_face_by_anchor, resolve_face_on_plane,
                       resolve_top_face)


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

    # 3) Run nodes in order. node_features records each node's CREATED feature name (the last
    # non-empty cadState.features[0] among its ops) so later nodes can reference it by node id
    # (lowering.node_feature — e.g. a circular_pattern's seed), localization-proof.
    node_features = {}
    for node in nodes:
        nid = node.get("id")
        ntype = node.get("type")
        rec = {"id": nid, "type": ntype, "status": "PENDING", "ops": []}
        try:
            ops = _plan_node(port, node, sv, node_features)  # may do a live (read-only) resolve
            last_resp = None
            frame_fn = None  # 2D coord transform for this node's entities (sketch frame fix)
            frame_mirror = False
            for op in ops:
                params = _fill_placeholders(op, last_resp, node_features, nid, ntype)
                if frame_fn is not None and op.tool == "add_sketch_entity":
                    params = _transform_entity_params(params, frame_fn, frame_mirror)
                resp = port.execute(op.tool, params, sv)
                result.exec_calls += 1
                sv = _advance(resp, sv, nid, ntype, op)
                last_resp = resp
                feats = ((resp.get("cadState") or {}).get("features")) or []
                if nid and feats and feats[0]:
                    node_features[nid] = feats[0]
                if op.tool == "create_sketch":
                    # A sketch's runtime NAME (renumbered/localized on the rebuild) lives in
                    # cadState.activeSketch, not features — record it so a later loft can reference
                    # this sketch node BY NAME (like circular_pattern's seed). Harmless for sketches
                    # consumed by an immediately-following extrude/revolve/rib (nothing reads it).
                    ask = (resp.get("cadState") or {}).get("activeSketch")
                    if nid and ask:
                        node_features[nid] = ask
                if _echoes_frame(op) and node.get("frame"):
                    # The node records the ORIGINAL sketch's frame; the response carries the
                    # frame the op just MEASURED (create_sketch — or edge_flange_sketch, whose
                    # generated profile sketch has an UNPREDICTABLE frame). Coordinates are
                    # transformed between them — correctness never depends on the support
                    # (face/plane/normal sign) or on how the API oriented the generated sketch.
                    # NOTE: the wire key is snake_case "result_geometry" (ADR-023's explicit
                    # JsonProperty), unlike the camelCase rest of the response.
                    measured = ((resp.get("result_geometry") or {}).get("frame"))
                    if not measured:
                        raise FeatureError(
                            "the node declares a sketch frame but the sketch-creating op "
                            "echoed none (execution layer too old?)",
                            code="FRAME_UNAVAILABLE", node_id=nid, node_type=ntype,
                            step=op.tool)
                    frame_fn, frame_mirror = _sketch_frame_transform(node["frame"], measured)
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


def _plan_node(port, node, state_version, node_features):
    ntype = node["type"]
    if ntype == "box":
        return lowering.lower_box(node)
    if ntype == "sketch":
        ref = node.get("ref") or {}
        face_ref = ref.get("face")
        if face_ref is not None:
            # v0.5.5: NON-axis-aligned support (a bent flange's face) — the anchor resolves to a
            # concrete face index (plane-containment, same resolver as extrude's up_to). Unlike
            # the datum/offset path this MUST resolve (there is no plane to fall back to).
            face_index, _info = resolve_face_by_anchor(port, state_version, face_ref["near"],
                                                       node_id=node.get("id"))
            return lowering.lower_sketch(node, face_index=face_index)
        offset = float(ref.get("offset") or 0.0)
        face_index = None
        if offset != 0.0:
            # Face preference (2026-07-05): sketch ON an existing face at that plane when one
            # exists — matches original authoring; a scaffolding plane only as the fallback.
            face_index = resolve_face_on_plane(port, state_version,
                                               ref.get("datum", "top"), offset,
                                               node_id=node.get("id"))
        return lowering.lower_sketch(node, face_index=face_index)
    if ntype == "extrude":
        if node.get("end") == "up_to_surface":
            near = ((node.get("up_to") or {}).get("face") or {}).get("near")
            face_index, _info = resolve_face_by_anchor(port, state_version, near,
                                                       node_id=node.get("id"))
            return lowering.lower_extrude(node, up_to_face_index=face_index)
        return lowering.lower_extrude(node)
    if ntype == "revolve":
        return lowering.lower_revolve(node)
    if ntype == "rib":
        return lowering.lower_rib(node)
    if ntype == "circular_pattern":
        return lowering.lower_circular_pattern(node)
    if ntype == "hole":
        face_index, info = resolve_top_face(port, state_version, node_id=node.get("id"))
        center2d = resolve_center_on_face(info)
        return lowering.lower_hole(node, face_index, center2d)
    if ntype == "fillet":
        edge_indices, _info = resolve_edges_by_anchor(port, state_version, node["edges"],
                                                      node_id=node.get("id"))
        return lowering.lower_fillet(node, edge_indices)
    if ntype == "chamfer":
        edge_indices, _info = resolve_edges_by_anchor(port, state_version, node["edges"],
                                                      node_id=node.get("id"))
        return lowering.lower_chamfer(node, edge_indices)
    if ntype == "sheet_metal":
        return lowering.lower_sheet_metal(node)
    if ntype == "sketched_bend":
        return lowering.lower_sketched_bend(node)
    if ntype == "edge_flange":
        # The attach edge resolves BY INDEX (fillet/chamfer's anchor resolver) — the coordinate
        # pick misses real edges (KNOWN-LIMITATIONS #6; live on 4-1: EF2's slanted edge existed
        # EXACTLY at the pick point yet SelectByID2 returned nothing).
        edge_indices, _info = resolve_edges_by_anchor(port, state_version, [node["edge"]],
                                                      node_id=node.get("id"))
        return lowering.lower_edge_flange(node, edge_indices[0])
    if ntype == "mirror":
        # Like loft's profiles: the mirrored features ran earlier, so their runtime names are
        # already in node_features. Resolve now; the tool selects by name (localization-proof).
        names = []
        for fid in node["features"]:
            nm = node_features.get(fid)
            if not nm:
                raise FeatureError("mirror feature '%s' has no recorded runtime feature name "
                                   "(it must be an earlier feature-producing node)" % fid,
                                   code="NODE_FEATURE_UNAVAILABLE", node_id=node.get("id"))
            names.append(nm)
        return lowering.lower_mirror(node, names)
    if ntype == "component":
        return lowering.lower_component(node)
    if ntype == "mate":
        # Each side: the component's RUNTIME Name2 comes from node_features (insert_component's
        # response — instance-numbered by SolidWorks, never guessed), and the COMPONENT-LOCAL
        # anchor resolves to a face/edge INDEX on that component (live read-only reads).
        sides = []
        for side in node["sides"]:
            cid = side["component"]
            comp_name = node_features.get(cid)
            if not comp_name:
                raise FeatureError("mate side references component node '%s' with no recorded "
                                   "runtime component name" % cid,
                                   code="NODE_FEATURE_UNAVAILABLE", node_id=node.get("id"))
            kind, index = resolve_component_entity_by_anchor(port, state_version, comp_name,
                                                             side["anchor"], node_id=node.get("id"))
            sides.append((comp_name, kind, index))
        return lowering.lower_mate(node, sides)
    if ntype == "loft":
        # The profile sketches ran earlier (loft comes after them in tree order), so each one's
        # runtime name is already recorded in node_features. Resolve them now; the loft selects
        # by name (localization/numbering-proof).
        names = []
        for pid in node["profiles"]:
            nm = node_features.get(pid)
            if not nm:
                raise FeatureError("loft profile '%s' has no recorded runtime sketch name "
                                   "(it must be an earlier sketch node)" % pid,
                                   code="NODE_FEATURE_UNAVAILABLE", node_id=node.get("id"))
            names.append(nm)
        return lowering.lower_loft(node, names)
    raise FeatureError("unknown node type '%s'" % ntype, code="UNKNOWN_NODE_TYPE", node_id=node.get("id"))


def _fill_placeholders(op, last_resp, node_features, nid, ntype):
    """Substitute runtime-name sentinels in op params (returns a copy only when needed):
      - FROM_PREV_FEATURE -> the PREVIOUS op's created feature name (cadState.features[0]) —
        how an offset plane's / pattern axis's RUNTIME name (localized, 'Düzlem1'/'Eksen1')
        reaches the very next op;
      - node_feature(id) -> the feature name recorded when node <id> executed — how a
        circular_pattern references its seed feature across nodes."""
    def _fill(v):
        if v == lowering.FROM_PREV_FEATURE:
            feats = ((last_resp or {}).get("cadState") or {}).get("features") or []
            if not feats or not feats[0]:
                raise FeatureError(
                    "op '%s' needs the previous op's created feature name, but the previous "
                    "response carried none" % op.tool,
                    code="PREV_FEATURE_UNAVAILABLE", node_id=nid, node_type=ntype, step=op.tool)
            return feats[0]
        ref = lowering.parse_node_feature(v)
        if ref is not None:
            name = node_features.get(ref)
            if not name:
                raise FeatureError(
                    "op '%s' references node '%s's created feature, but no feature name was "
                    "recorded for it" % (op.tool, ref),
                    code="NODE_FEATURE_UNAVAILABLE", node_id=nid, node_type=ntype, step=op.tool)
            return name
        return v

    if not any(v == lowering.FROM_PREV_FEATURE or lowering.parse_node_feature(v) is not None
               for v in op.params.values()):
        return op.params
    return {k: _fill(v) for k, v in op.params.items()}


def _dot3(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _echoes_frame(op):
    """Ops that create/activate a sketch and echo its MEASURED frame in result_geometry:
    create_sketch, and sheet_metal_feature(edge_flange_sketch) (the generated edge-flange
    profile sketch)."""
    if op.tool == "create_sketch":
        return True
    return (op.tool == "sheet_metal_feature"
            and (op.params or {}).get("feature_type") == "edge_flange_sketch")


def _sketch_frame_transform(declared, measured):
    """Build the 2D transform ORIGINAL-sketch-frame -> MEASURED-rebuild-frame.

    Both frames lie on the SAME model plane (same support plane), so a 2D point (u,v) in the
    original frame is the model point O_d + u·X_d + v·Y_d, re-expressed in the measured frame as
    ((m-O_m)·X_m, (m-O_m)·Y_m). Handles rotation, MIRROR (support normal flipped — the 2-1
    Sketch5 failure class) and origin shift in one affine map. Returns (fn(u,v)->(u',v'),
    mirrored) — fn is None when the frames already coincide; mirrored=True flips arc sweep
    senses (a mirror reverses CCW/CW)."""
    od, xd, yd = declared["origin"], declared["xdir"], declared["ydir"]
    om, xm, ym = measured["origin"], measured["xdir"], measured["ydir"]
    d_o = [od[i] - om[i] for i in range(3)]
    tu, tv = _dot3(d_o, xm), _dot3(d_o, ym)
    a, b = _dot3(xd, xm), _dot3(yd, xm)
    c, d = _dot3(xd, ym), _dot3(yd, ym)
    if (abs(tu) < 1e-9 and abs(tv) < 1e-9
            and abs(a - 1) < 1e-9 and abs(b) < 1e-9 and abs(c) < 1e-9 and abs(d - 1) < 1e-9):
        return None, False  # identical frames — no transform needed

    def fn(u, v):
        return (round(tu + a * u + b * v, 9), round(tv + c * u + d * v, 9))

    return fn, (a * d - b * c) < 0


_ENTITY_COORD_PAIRS = (("x1", "y1"), ("x2", "y2"), ("xm", "ym"), ("cx", "cy"), ("vx", "vy"))


def _transform_entity_params(params, frame_fn, mirrored):
    """Apply the sketch-frame transform to an add_sketch_entity op's 2D coordinates.
    A mirror also flips arc_center's sweep sense (direction) — radii/distances are invariant."""
    out = dict(params)
    for kx, ky in _ENTITY_COORD_PAIRS:
        if kx in out and ky in out:
            out[kx], out[ky] = frame_fn(out[kx], out[ky])
    if mirrored and out.get("entity_type") == "arc_center" and "direction" in out:
        out["direction"] = -out["direction"]
    return out


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
