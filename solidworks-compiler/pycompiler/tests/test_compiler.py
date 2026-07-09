"""Offline unit tests for pycompiler — a FAKE ExecutionPort, NO live SolidWorks.

Proves the lowering order, the reference resolver (picks the +Y top face), structural-validation
rejection (no execution calls), and partial-progress reporting on an injected sub-op failure.

Run two ways:
  - standalone:  python solidworks-compiler/pycompiler/tests/test_compiler.py
  - pytest:      pytest solidworks-compiler/pycompiler/tests/test_compiler.py
"""
import json
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_COMPILER_ROOT = os.path.dirname(os.path.dirname(_HERE))  # solidworks-compiler/
if _COMPILER_ROOT not in sys.path:
    sys.path.insert(0, _COMPILER_ROOT)

from pycompiler.compiler import compile_and_run          # noqa: E402
from pycompiler.execution_port import ExecutionPort       # noqa: E402

# A canonical box's 6 planar faces. The +Y face at y=0.05 is the unambiguous "top".
_BOX_FACES = json.dumps({
    "face_count": 6,
    "faces": [
        {"i": 0, "planar": True, "area": 0.0025, "normal": [0, 1, 0],  "point": [0, 0.05, 0]},
        {"i": 1, "planar": True, "area": 0.0025, "normal": [0, -1, 0], "point": [0, 0.0, 0]},
        {"i": 2, "planar": True, "area": 0.0025, "normal": [1, 0, 0],  "point": [0.025, 0.025, 0]},
        {"i": 3, "planar": True, "area": 0.0025, "normal": [-1, 0, 0], "point": [-0.025, 0.025, 0]},
        {"i": 4, "planar": True, "area": 0.0025, "normal": [0, 0, 1],  "point": [0, 0.025, 0.025]},
        {"i": 5, "planar": True, "area": 0.0025, "normal": [0, 0, -1], "point": [0, 0.025, -0.025]},
    ],
})

EXAMPLE = {
    "schema_version": "0.0.1-draft-ir-exp",
    "units": "meters",
    "nodes": [
        {"id": "n1", "type": "box", "ref": {"datum": "top"},
         "width": 0.05, "depth": 0.05, "height": 0.05},
        {"id": "n2", "type": "hole",
         "ref": {"node_face": {"node": "n1", "selector": "top"}, "position": "center"},
         "diameter": 0.01, "depth": "through_all"},
    ],
}


class FakePort(ExecutionPort):
    """Records calls, bumps state_version on writes, returns canned payloads for analyze_model
    (per analysis_type: faces / edges). add_reference_geometry returns a LOCALIZED created-plane
    name ('Düzlem1' — the Turkish install) so the FROM_PREV_FEATURE substitution is proven
    localization-independent. fail_on=(tool, nth) injects a FAILED response on the nth call."""

    def __init__(self, faces=_BOX_FACES, edges=None, fail_on=None, sketch_frame=None):
        self._sv = 0
        self._faces = faces
        self._edges = edges
        self._fail_on = fail_on
        self._sketch_frame = sketch_frame  # echoed by create_sketch (the MEASURED rebuild frame)
        self._counts = {}
        self.calls = []  # list of (tool, params)

    def get_state(self):
        return self._sv

    def execute(self, tool, params, state_version):
        self.calls.append((tool, dict(params)))
        self._counts[tool] = self._counts.get(tool, 0) + 1
        if self._fail_on and self._fail_on[0] == tool and self._counts[tool] == self._fail_on[1]:
            return {"status": "FAILED", "stateVersion": self._sv,
                    "error": {"code": "EXTRUSION_FAILED", "message": "injected failure"}}
        if tool == "analyze_model":
            payload = self._edges if params.get("analysis_type") == "edges" else self._faces
            return {"status": "COMPLETED", "stateVersion": self._sv,
                    "cadState": {"features": [payload] if payload else []}}
        self._sv += 1  # a write op bumps state_version
        # Feature-creating tools echo a LOCALIZED created-feature name (mirrors the live layer:
        # cadState.features = [feature.Name]) so the FROM_PREV_FEATURE / node_feature
        # substitutions are proven localization-independent.
        _names = {"add_reference_geometry": "Düzlem1",
                  "extrude_feature": "Ekstrüzyon%d" % self._sv,
                  "add_edge_feature": "Radyus%d" % self._sv,
                  "create_rib": "Feder%d" % self._sv,
                  "create_pattern": "DairPatern%d" % self._sv,
                  "sheet_metal_feature": "SacFeature%d" % self._sv}
        feats = [_names[tool]] if tool in _names else []
        resp = {"status": "COMPLETED", "stateVersion": self._sv, "cadState": {"features": feats}}
        if tool == "create_sketch":
            # A created sketch echoes its runtime NAME in cadState.activeSketch (localized here) so
            # the compiler can reference it later by name (a loft's profiles) — localization-proof.
            resp["cadState"]["activeSketch"] = "Çizim%d" % self._sv
            if self._sketch_frame is not None:
                resp["result_geometry"] = {"frame": self._sketch_frame}  # snake_case wire (ADR-023)
        elif tool == "sheet_metal_feature" and params.get("feature_type") == "edge_flange_sketch":
            # The generated edge-flange profile sketch: no feature created yet (mirrors the live
            # layer), active sketch name + the MEASURED frame echoed like create_sketch.
            resp["cadState"]["features"] = []
            resp["cadState"]["activeSketch"] = "Çizim%d" % self._sv
            if self._sketch_frame is not None:
                resp["result_geometry"] = {"frame": self._sketch_frame}
        return resp


def _tools(port):
    return [t for (t, _p) in port.calls]


def test_example_box_hole_lowering_order():
    port = FakePort()
    r = compile_and_run(port, EXAMPLE)
    assert r.status == "COMPLETED", r.to_dict()
    assert r.nodes_completed == 2 and r.nodes_total == 2
    assert _tools(port) == [
        "create_sketch", "add_sketch_entity", "extrude_feature",   # box
        "analyze_model",                                           # resolve top face
        "create_sketch", "add_sketch_entity", "extrude_feature",   # hole
    ]
    # box on Top Plane, centred rectangle
    assert port.calls[0][1]["plane"] == "Top Plane"
    assert port.calls[1][1] == {"entity_type": "rectangle", "x1": -0.025, "y1": -0.025, "x2": 0.025, "y2": 0.025}
    assert port.calls[2][1] == {"feature_type": "boss", "depth": 0.05}
    # hole: sketch on the resolved face_index 0, circle Ø10 at centre, through cut
    assert port.calls[4][1] == {"on_face": True, "face_index": 0}
    assert port.calls[5][1] == {"entity_type": "circle", "cx": 0.0, "cy": 0.0, "radius": 0.005}
    assert port.calls[6][1] == {"feature_type": "cut", "through": True}
    # 6 write ops (analyze is read-only) -> final state_version 6
    assert r.exec_calls == 6
    assert r.final_state_version == 6


def test_resolver_picks_highest_upward_face():
    # Shuffle face order + add a higher DOWNWARD face that must be ignored.
    faces = json.loads(_BOX_FACES)
    faces["faces"].append({"i": 6, "planar": True, "area": 0.01, "normal": [0, -1, 0], "point": [0, 0.09, 0]})
    port = FakePort(faces=json.dumps(faces))
    r = compile_and_run(port, EXAMPLE)
    assert r.status == "COMPLETED", r.to_dict()
    assert port.calls[4][1]["face_index"] == 0  # still the +Y face at y=0.05, not the down-facing y=0.09


def test_validation_rejects_with_no_execution():
    bad = {"units": "millimeters", "nodes": [{"id": "n1", "type": "frobnicate"}]}
    port = FakePort()
    r = compile_and_run(port, bad)
    assert r.status == "FAILED"
    assert r.error["code"] == "VALIDATION_FAILED"
    assert port.calls == []  # nothing executed
    assert "units must be 'meters'" in r.error["message"]


def test_unsupported_selector_rejected():
    g = json.loads(json.dumps(EXAMPLE))
    g["nodes"][1]["ref"]["node_face"]["selector"] = "bottom"
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED"
    assert port.calls == []


def test_partial_progress_on_cut_failure():
    # Fail the 2nd extrude_feature (the hole's cut; the 1st is the box boss).
    port = FakePort(fail_on=("extrude_feature", 2))
    r = compile_and_run(port, EXAMPLE)
    assert r.status == "FAILED"
    assert r.nodes_completed == 1  # box succeeded, hole failed
    assert r.error["code"] == "EXTRUSION_FAILED"
    assert r.error["node_id"] == "n2"
    assert r.error["step"] == "extrude_feature"


def test_no_upward_face_unresolved():
    faces = {"face_count": 1, "faces": [{"i": 0, "planar": True, "area": 1.0, "normal": [0, -1, 0], "point": [0, 0, 0]}]}
    port = FakePort(faces=json.dumps(faces))
    r = compile_and_run(port, EXAMPLE)
    assert r.status == "FAILED"
    assert r.error["code"] == "REFERENCE_UNRESOLVED"
    assert r.nodes_completed == 1  # box built, hole could not resolve a top face


def test_sketch_extrude_boss_path():
    g = {
        "schema_version": "0.0.1-draft-ir-exp", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
             "profile": [{"kind": "rectangle", "width": 0.04, "height": 0.03}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.02},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    assert _tools(port) == ["create_sketch", "add_sketch_entity", "extrude_feature"]
    assert port.calls[0][1]["plane"] == "Front Plane"


# ---------------------------------------------------------------------------
# v0.5 vocabulary (path profile / construction / offset plane / up_to_surface / fillet anchors)
# ---------------------------------------------------------------------------

# A stepped part: base top ring at y=0.01 (i=0) and a pedestal top at y=0.02 (i=6) — the
# up_to_surface fixture (mirrors the live Test-0 pedestal part).
_STEP_FACES = json.dumps({
    "face_count": 7,
    "faces": [
        {"i": 0, "planar": True, "area": 0.0012, "normal": [0, 1, 0],  "point": [0.015, 0.01, 0.0]},
        {"i": 1, "planar": True, "area": 0.0004, "normal": [-1, 0, 0], "point": [-0.02, 0.005, 0]},
        {"i": 2, "planar": True, "area": 0.0004, "normal": [1, 0, 0],  "point": [0.02, 0.005, 0]},
        {"i": 3, "planar": True, "area": 0.0016, "normal": [0, -1, 0], "point": [0, 0.0, 0]},
        {"i": 4, "planar": False, "area": 0.0002},
        {"i": 5, "planar": True, "area": 0.0002, "normal": [0, 0, 1],  "point": [0, 0.015, 0.01]},
        {"i": 6, "planar": True, "area": 0.0004, "normal": [0, 1, 0],  "point": [0, 0.02, 0]},
    ],
})

_EDGES = json.dumps({
    "edge_count": 4,
    "edges": [
        {"i": 0, "start": [0, 0, 0], "end": [0.01, 0, 0], "mid": [0.005, 0.0, 0.0]},
        {"i": 1, "start": [0, 0, 0], "end": [0, 0.01, 0], "mid": [0.0, 0.005, 0.0]},
        {"i": 2, "start": [0, 0, 0], "end": [0, 0, 0.01], "mid": [0.0, 0.0, 0.005]},
        {"i": 3, "start": [0.01, 0, 0], "end": [0.01, 0.01, 0], "mid": [0.01, 0.005, 0.0]},
    ],
})


def test_offset_plane_sketch_uses_created_plane_name():
    # A sketch on Front Plane offset +3mm: add_reference_geometry first, then create_sketch on
    # the RETURNED (localized!) plane name — never a guessed 'Plane1'.
    g = {
        "schema_version": "0.5.0-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front", "offset": 0.003},
             "profile": [{"kind": "circle", "diameter": 0.008, "cx": 0.0, "cy": 0.0138}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "cut", "depth": 0.003},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    # Face preference: the offset sketch first LOOKS for an existing face on that plane
    # (analyze_model); none of the box faces sits at front+0.003 -> reference-plane fallback.
    assert _tools(port) == ["analyze_model", "add_reference_geometry", "create_sketch",
                            "add_sketch_entity", "extrude_feature"]
    assert port.calls[1][1] == {"type": "plane", "ref_plane_name": "Front Plane", "offset": 0.003}
    assert port.calls[2][1] == {"plane": "Düzlem1", "on_face": False}  # runtime name, not a guess
    assert port.calls[3][1] == {"entity_type": "circle", "cx": 0.0, "cy": 0.0138, "radius": 0.004}


def test_path_profile_line_arc_construction_params():
    g = {
        "schema_version": "0.5.0-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
             "profile": [
                 {"kind": "line", "x1": -0.0065, "y1": 0.0, "x2": 0.0065, "y2": 0.0},
                 {"kind": "arc", "cx": 0.0, "cy": 0.0, "x1": 0.0065, "y1": 0.0,
                  "x2": -0.0065, "y2": 0.0, "dir": -1},
                 {"kind": "line", "x1": 0.0, "y1": -0.01, "x2": 0.0, "y2": 0.01, "construction": True},
             ]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.003},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    assert port.calls[1][1] == {"entity_type": "line", "x1": -0.0065, "y1": 0.0, "x2": 0.0065, "y2": 0.0}
    assert port.calls[2][1] == {"entity_type": "arc_center", "cx": 0.0, "cy": 0.0,
                                "x1": 0.0065, "y1": 0.0, "x2": -0.0065, "y2": 0.0, "direction": -1}
    assert port.calls[3][1] == {"entity_type": "line", "x1": 0.0, "y1": -0.01, "x2": 0.0, "y2": 0.01,
                                "construction": True}


def test_extrude_up_to_surface_resolves_face_anchor():
    # Anchor on the step-ring plane (y=0.01) -> face 0; the pedestal top (y=0.02) must NOT match.
    g = {
        "schema_version": "0.5.0-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "top", "offset": 0.02},
             "profile": [{"kind": "circle", "diameter": 0.008}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "cut",
             "end": "up_to_surface",
             "up_to": {"face": {"near": [0.012, 0.01, 0.003], "hint": "step ring"}}},
        ],
    }
    port = FakePort(faces=_STEP_FACES)
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    # Face preference kicks in here too: the pedestal top (i=6, y=0.02) IS on the sketch plane,
    # so the sketch lands ON the face — no scaffolding plane at all.
    assert _tools(port) == ["analyze_model", "create_sketch", "add_sketch_entity",
                            "analyze_model", "extrude_feature"]
    assert port.calls[1][1] == {"on_face": True, "face_index": 6}
    assert port.calls[4][1] == {"feature_type": "cut", "up_to_face_index": 0}


def test_face_anchor_unresolved():
    g = {
        "schema_version": "0.5.0-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "top"},
             "profile": [{"kind": "circle", "diameter": 0.008}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "cut",
             "end": "up_to_surface", "up_to": {"face": {"near": [0.0, 0.5, 0.0]}}},
        ],
    }
    port = FakePort(faces=_STEP_FACES)
    r = compile_and_run(port, g)
    assert r.status == "FAILED" and r.error["code"] == "REFERENCE_UNRESOLVED"
    assert r.error["step"] == "resolve_face_by_anchor"


def test_fillet_edge_anchors_resolve_to_indices():
    g = {
        "schema_version": "0.5.0-draft", "units": "meters",
        "nodes": [
            {"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.05},
            {"id": "f1", "type": "fillet", "radius": 0.00035,
             "edges": [{"near": [0.01, 0.005, 0.0], "hint": "right vertical"},
                       {"near": [0.005, 0.0, 0.0]}]},
        ],
    }
    port = FakePort(edges=_EDGES)
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    fillet_call = port.calls[-1]
    assert fillet_call[0] == "add_edge_feature"
    assert fillet_call[1] == {"feature_type": "fillet", "radius_or_distance": 0.00035,
                              "edge_indices": "[0,3]"}  # sorted, JSON-string per the tool contract


def test_fillet_anchor_ambiguous_and_unresolved_and_duplicate():
    # Two mids within the tolerance ball of one anchor -> AMBIGUOUS.
    tight = json.dumps({"edge_count": 2, "edges": [
        {"i": 0, "start": [0, 0, 0], "end": [0, 0, 0], "mid": [0.0, 0.0, 0.0]},
        {"i": 1, "start": [0, 0, 0], "end": [0, 0, 0], "mid": [0.00005, 0.0, 0.0]},
    ]})
    g = {"schema_version": "0.5.0-draft", "units": "meters",
         "nodes": [{"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.05},
                   {"id": "f1", "type": "fillet", "radius": 0.001,
                    "edges": [{"near": [0.0, 0.0, 0.0]}]}]}
    r = compile_and_run(FakePort(edges=tight), g)
    assert r.status == "FAILED" and r.error["code"] == "REFERENCE_AMBIGUOUS"
    # No mid within the ball -> UNRESOLVED (with the nearest distance reported).
    g["nodes"][1]["edges"] = [{"near": [0.5, 0.5, 0.5]}]
    r = compile_and_run(FakePort(edges=_EDGES), g)
    assert r.status == "FAILED" and r.error["code"] == "REFERENCE_UNRESOLVED"
    assert "nearest was" in r.error["message"]
    # Two anchors landing on the SAME edge -> duplicate = AMBIGUOUS.
    g["nodes"][1]["edges"] = [{"near": [0.005, 0.0, 0.0]}, {"near": [0.005, 0.0, 0.00005]}]
    r = compile_and_run(FakePort(edges=_EDGES), g)
    assert r.status == "FAILED" and r.error["code"] == "REFERENCE_AMBIGUOUS"
    assert "SAME edge" in r.error["message"]


def test_chamfer_distance_angle_lowering():
    # level-2-2 Chamfer1: distance-angle setback + angle. IR carries the angle in RADIANS (recipe
    # SI) -> the tool boundary takes DEGREES. Edge anchors resolve exactly like fillet.
    g = {
        "schema_version": "0.5.3-draft", "units": "meters",
        "nodes": [
            {"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.05},
            {"id": "c1", "type": "chamfer", "chamfer_type": "distance_angle",
             "distance": 0.01, "angle": 0.785398,
             "edges": [{"near": [0.005, 0.0, 0.0], "hint": "front-bottom edge"}]},
        ],
    }
    port = FakePort(edges=_EDGES)
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    cham = port.calls[-1]
    assert cham[0] == "add_edge_feature"
    assert cham[1]["feature_type"] == "chamfer" and cham[1]["chamfer_type"] == "distance_angle"
    assert cham[1]["radius_or_distance"] == 0.01
    assert cham[1]["edge_indices"] == "[0]"
    assert abs(cham[1]["angle"] - 45.0) < 1e-3   # 0.785398 rad -> ~45°
    assert "distance2" not in cham[1] and "chamfer_flip" not in cham[1]  # dist-angle omits both


def test_chamfer_distance_distance_lowering_and_flip():
    # level-2-2 Chamfer2: two-distance chamfer (D1 × D2). Directional -> 'flip' emitted only when
    # set; edge anchors resolve to sorted indices like fillet.
    g = {
        "schema_version": "0.5.3-draft", "units": "meters",
        "nodes": [
            {"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.05},
            {"id": "c1", "type": "chamfer", "chamfer_type": "distance_distance",
             "distance": 0.003, "distance2": 0.005, "flip": True,
             "edges": [{"near": [0.0, 0.005, 0.0]}, {"near": [0.01, 0.005, 0.0]}]},
        ],
    }
    port = FakePort(edges=_EDGES)
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    cham = port.calls[-1][1]
    assert cham["feature_type"] == "chamfer" and cham["chamfer_type"] == "distance_distance"
    assert cham["radius_or_distance"] == 0.003 and cham["distance2"] == 0.005
    assert cham["chamfer_flip"] is True
    assert cham["edge_indices"] == "[1,3]"       # anchors -> edges 1 and 3, sorted
    assert "angle" not in cham                    # angle is meaningless in this mode


def test_chamfer_validation_rejections():
    # bad chamfer_type; distance_distance without distance2; chamfer without edges — all rejected
    # at plan time with ZERO execution calls.
    bads = [
        {"id": "c1", "type": "chamfer", "chamfer_type": "bevel", "distance": 0.01,
         "edges": [{"near": [0, 0, 0]}]},
        {"id": "c1", "type": "chamfer", "chamfer_type": "distance_distance", "distance": 0.003,
         "edges": [{"near": [0, 0, 0]}]},
        {"id": "c1", "type": "chamfer", "distance": 0.01, "edges": []},
    ]
    for bad in bads:
        port = FakePort()
        r = compile_and_run(port, {"schema_version": "0.5.3-draft", "units": "meters", "nodes": [bad]})
        assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED", r.to_dict()
        assert port.calls == []


def test_loft_multi_profile_by_runtime_name():
    # level-2-2 Loft2: a boss loft over TWO earlier circle sketches on offset planes. The sketches
    # are NOT immediately followed by the loft (grammar relaxation) — the loft references them by
    # RUNTIME name, resolved from node_features (localization/numbering-proof).
    g = {
        "schema_version": "0.5.4-draft", "units": "meters",
        "nodes": [
            {"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.045},
            {"id": "s_lo", "type": "sketch", "ref": {"datum": "front", "offset": 0.045},
             "profile": [{"kind": "circle", "diameter": 0.025}]},
            {"id": "s_hi", "type": "sketch", "ref": {"datum": "front", "offset": 0.065},
             "profile": [{"kind": "circle", "diameter": 0.01}]},
            {"id": "l1", "type": "loft", "profiles": ["s_lo", "s_hi"]},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    loft = port.calls[-1]
    assert loft[0] == "extrude_feature" and loft[1]["feature_type"] == "loft"
    names = json.loads(loft[1]["profiles"])           # JSON-array string per the tool contract
    assert len(names) == 2 and all(n.startswith("Çizim") for n in names)  # runtime names, not ids
    assert names[0] != names[1]                        # two distinct sketches, in profile order


def test_loft_validation_rejections():
    # < 2 profiles; a profile referencing a non-sketch node — both rejected at plan time, no exec.
    bads = [
        [{"id": "s1", "type": "sketch", "ref": {"datum": "front"},
          "profile": [{"kind": "circle", "diameter": 0.02}]},
         {"id": "l1", "type": "loft", "profiles": ["s1"]}],
        [{"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.01},
         {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
          "profile": [{"kind": "circle", "diameter": 0.02}]},
         {"id": "l1", "type": "loft", "profiles": ["s1", "b1"]}],  # b1 is a box, not a sketch
    ]
    for nodes in bads:
        port = FakePort()
        r = compile_and_run(port, {"schema_version": "0.5.4-draft", "units": "meters", "nodes": nodes})
        assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED", r.to_dict()
        assert port.calls == []


def test_revolve_lowering_axis_and_angle():
    # The 1-2 flange idiom: half-section profile + construction centerline as the axis;
    # IR angle in RADIANS (recipe SI) -> tool boundary DEGREES.
    g = {
        "schema_version": "0.5.1-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"}, "profile": [
                {"kind": "line", "x1": 0.0, "y1": 0.0254, "x2": -0.0389, "y2": 0.0254},
                {"kind": "line", "x1": -0.0389, "y1": 0.0254, "x2": -0.0389, "y2": 0.0},
                {"kind": "line", "x1": -0.0389, "y1": 0.0, "x2": 0.0, "y2": 0.0},
                {"kind": "line", "x1": 0.0, "y1": 0.0, "x2": 0.0, "y2": 0.0254},
                {"kind": "line", "x1": 0.0, "y1": 0.0254, "x2": 0.0, "y2": 0.0, "construction": True},
            ]},
            {"id": "r1", "type": "revolve", "sketch": "s1", "axis": {"x1": 0.0, "y1": 0.0254, "x2": 0.0, "y2": 0.0},
             "angle": 6.283185},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    rev = port.calls[-1]
    assert rev[0] == "extrude_feature"
    assert rev[1]["feature_type"] == "revolve"
    assert abs(rev[1]["angle"] - 359.999971) < 1e-3  # 6.283185 rad; execution snaps to exact 360
    assert (rev[1]["axis_x1"], rev[1]["axis_y1"], rev[1]["axis_x2"], rev[1]["axis_y2"]) == (0.0, 0.0254, 0.0, 0.0)


def test_circular_pattern_axis_and_seed_name_substitution():
    # Pattern seeds on the CUT node's runtime feature name; the axis feature's runtime name flows
    # from the immediately preceding add_reference_geometry response. Both localized in FakePort.
    g = {
        "schema_version": "0.5.1-draft", "units": "meters",
        "nodes": [
            {"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.01},
            {"id": "s2", "type": "sketch", "ref": {"datum": "top", "offset": 0.01},
             "profile": [{"kind": "circle", "diameter": 0.008, "cx": 0.015, "cy": 0.0}]},
            {"id": "e2", "type": "extrude", "sketch": "s2", "operation": "cut", "end": "through_all"},
            {"id": "p1", "type": "circular_pattern", "feature": "e2",
             "axis": {"datums": ["front", "right"]}, "count": 4, "angle_deg": 360.0, "equal_spacing": True},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    axis_call = port.calls[-2]
    assert axis_call[0] == "add_reference_geometry"
    assert axis_call[1] == {"type": "axis", "entity1_name": "Front Plane", "entity1_type": "PLANE",
                            "entity2_name": "Right Plane", "entity2_type": "PLANE"}
    pat = port.calls[-1]
    assert pat[0] == "create_pattern"
    assert pat[1]["pattern_type"] == "circular"
    assert pat[1]["axis_name"] == "Düzlem1"            # runtime name from the axis op's response
    assert pat[1]["feature_name"].startswith("Ekstrüzyon")  # the CUT node's recorded runtime name
    assert pat[1]["count"] == 4 and pat[1]["angle"] == 360.0 and pat[1]["equal_spacing"] is True


def test_sketch_frame_transform_mirror():
    # The 2-1 Sketch5 failure class: the ORIGINAL sketch sat on the part's BOTTOM face
    # (normal -Y, frame x=[1,0,0], y=[0,0,1]); the rebuild's support measures a MIRRORED frame
    # (y=[0,0,-1]). The compiler must remap coordinates (v -> -v) and flip arc sweep senses.
    declared = {"origin": [0.0, -0.007, 0.0], "xdir": [1.0, 0.0, 0.0], "ydir": [0.0, 0.0, 1.0]}
    measured = {"origin": [0.0, -0.007, 0.0], "xdir": [1.0, 0.0, 0.0], "ydir": [0.0, 0.0, -1.0]}
    g = {
        "schema_version": "0.5.2-draft", "units": "meters",
        "nodes": [
            {"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.007},
            {"id": "s1", "type": "sketch", "ref": {"datum": "top", "offset": -0.007},
             "frame": declared,
             "profile": [
                 {"kind": "line", "x1": -0.016, "y1": 0.003, "x2": 0.016, "y2": 0.003},
                 {"kind": "arc", "cx": 0.0, "cy": 0.003, "x1": -0.016, "y1": 0.003,
                  "x2": 0.016, "y2": 0.003, "dir": 1},
             ]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "cut", "depth": 0.003},
        ],
    }
    port = FakePort(sketch_frame=measured)  # no face on the plane in _BOX_FACES -> plane path
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    line = next(p for (t, p) in port.calls if t == "add_sketch_entity" and p.get("entity_type") == "line")
    assert (line["x1"], line["y1"], line["x2"], line["y2"]) == (-0.016, -0.003, 0.016, -0.003)
    arc = next(p for (t, p) in port.calls if t == "add_sketch_entity" and p.get("entity_type") == "arc_center")
    assert (arc["cy"], arc["y1"], arc["y2"]) == (-0.003, -0.003, -0.003)
    assert arc["direction"] == -1  # mirror flips the sweep sense


def test_sketch_frame_identity_no_transform_and_missing_frame_error():
    ident = {"origin": [0.0, 0.0, 0.0], "xdir": [1.0, 0.0, 0.0], "ydir": [0.0, 1.0, 0.0]}
    g = {
        "schema_version": "0.5.2-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"}, "frame": ident,
             "profile": [{"kind": "line", "x1": 0.001, "y1": 0.002, "x2": 0.003, "y2": 0.004}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.01},
        ],
    }
    port = FakePort(sketch_frame=ident)
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    line = next(p for (t, p) in port.calls if t == "add_sketch_entity")
    assert (line["x1"], line["y1"]) == (0.001, 0.002)  # identity -> coordinates untouched
    # Node declares a frame but the execution echoes none -> loud FRAME_UNAVAILABLE.
    port2 = FakePort(sketch_frame=None)
    r2 = compile_and_run(port2, g)
    assert r2.status == "FAILED" and r2.error["code"] == "FRAME_UNAVAILABLE"


def test_offset_sketch_prefers_existing_face():
    # A face EXISTS on the sketch plane (pedestal top y=0.02 in _STEP_FACES) -> sketch ON it,
    # no add_reference_geometry op at all.
    g = {
        "schema_version": "0.5.2-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "top", "offset": 0.02},
             "profile": [{"kind": "circle", "diameter": 0.008}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "cut", "depth": 0.003},
        ],
    }
    port = FakePort(faces=_STEP_FACES)
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    assert _tools(port) == ["analyze_model", "create_sketch", "add_sketch_entity", "extrude_feature"]
    assert port.calls[1][1] == {"on_face": True, "face_index": 6}


def test_mid_plane_and_rib_lowering():
    # The 2-1 angle bracket idioms: a mid-plane symmetric base extrude + a single-line rib.
    g = {
        "schema_version": "0.5.2-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "right"},
             "profile": [{"kind": "line", "x1": 0.0, "y1": 0.0, "x2": 0.04, "y2": 0.0},
                         {"kind": "line", "x1": 0.04, "y1": 0.0, "x2": 0.0, "y2": 0.04},
                         {"kind": "line", "x1": 0.0, "y1": 0.04, "x2": 0.0, "y2": 0.0}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss",
             "end": "mid_plane", "depth": 0.04, "reversed": True},
            {"id": "s2", "type": "sketch", "ref": {"datum": "right"},
             "profile": [{"kind": "line", "x1": 0.03, "y1": 0.005, "x2": 0.005, "y2": 0.03}]},
            {"id": "r1", "type": "rib", "sketch": "s2", "thickness": 0.005},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    mid = port.calls[4][1]
    assert mid == {"feature_type": "boss", "depth": 0.04, "mid_plane": True, "reverse": True}
    rib = port.calls[-1]
    assert rib[0] == "create_rib" and rib[1] == {"thickness": 0.005}
    # Validation rejects: mid_plane without depth; rib not following its sketch.
    bad = {"schema_version": "0.5.2-draft", "units": "meters", "nodes": [
        g["nodes"][0], {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "end": "mid_plane"}]}
    r2 = compile_and_run(FakePort(), bad)
    assert r2.status == "FAILED" and "mid_plane" in r2.error["message"]
    bad2 = {"schema_version": "0.5.2-draft", "units": "meters", "nodes": [
        g["nodes"][2], {"id": "x", "type": "box", "width": 0.01, "depth": 0.01, "height": 0.01},
        {"id": "r1", "type": "rib", "sketch": "s2", "thickness": 0.005}]}
    r3 = compile_and_run(FakePort(), bad2)
    assert r3.status == "FAILED" and "immediately follow" in r3.error["message"]


def test_v051_validation_rejections():
    # revolve without axis; pattern seeding on a sketch node; pattern with identical datums.
    bads = [
        [{"id": "s1", "type": "sketch", "ref": {"datum": "front"},
          "profile": [{"kind": "circle", "diameter": 0.02}]},
         {"id": "r1", "type": "revolve", "sketch": "s1"}],
        [{"id": "s1", "type": "sketch", "ref": {"datum": "front"},
          "profile": [{"kind": "circle", "diameter": 0.02}]},
         {"id": "p1", "type": "circular_pattern", "feature": "s1",
          "axis": {"datums": ["front", "right"]}, "count": 4, "angle_deg": 360.0}],
        [{"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.01},
         {"id": "p1", "type": "circular_pattern", "feature": "b1",
          "axis": {"datums": ["front", "front"]}, "count": 4, "angle_deg": 360.0}],
    ]
    for nodes in bads:
        port = FakePort()
        r = compile_and_run(port, {"schema_version": "0.5.1-draft", "units": "meters", "nodes": nodes})
        assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED", r.to_dict()
        assert port.calls == []


def test_extrude_reversed_flag_maps_to_reverse_param():
    g = {
        "schema_version": "0.5.0-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
             "profile": [{"kind": "circle", "diameter": 0.02}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss",
             "depth": 0.003, "reversed": True},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    assert port.calls[-1][1] == {"feature_type": "boss", "depth": 0.003, "reverse": True}


def test_v05_validation_rejections():
    # arc without dir; fillet without edges; up_to_surface without up_to.face.near — all
    # rejected at plan time with ZERO execution calls.
    bads = [
        {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
         "profile": [{"kind": "arc", "cx": 0, "cy": 0, "x1": 1, "y1": 0, "x2": -1, "y2": 0}]},
        {"id": "f1", "type": "fillet", "radius": 0.001, "edges": []},
        {"id": "e1", "type": "extrude", "sketch": "s0", "operation": "cut", "end": "up_to_surface"},
    ]
    for bad in bads:
        port = FakePort()
        r = compile_and_run(port, {"schema_version": "0.5.0-draft", "units": "meters", "nodes": [bad]})
        assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED", r.to_dict()
        assert port.calls == []


def test_extrude_must_follow_its_sketch():
    g = {
        "schema_version": "0.0.1-draft-ir-exp", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
             "profile": [{"kind": "circle", "diameter": 0.02}]},
            {"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.05},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.02},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED"
    assert "immediately follow" in r.error["message"]


def test_sheet_metal_base_and_sketched_bend_lowering():
    # 4-1 idiom: base flange from the profile sketch, then a bend-line sketch + sketched_bend.
    # Angle RADIANS -> DEGREES at the boundary (C4); radius OMITTED => use_default_radius=true;
    # the fixed anchor's 3D point passes straight through as fixed_x/y/z (the point IS the side
    # selector — no index resolution). position 'centerline' (the default) is not emitted.
    g = {
        "schema_version": "0.5.5-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
             "profile": [{"kind": "rectangle", "width": 0.1, "height": 0.06}]},
            {"id": "sm1", "type": "sheet_metal", "sketch": "s1",
             "thickness": 0.002, "bend_radius": 0.002, "k_factor": 0.5},
            {"id": "s2", "type": "sketch", "ref": {"datum": "front", "offset": 0.002},
             "profile": [{"kind": "line", "x1": -0.06, "y1": 0.01, "x2": 0.06, "y2": 0.01}]},
            {"id": "sb1", "type": "sketched_bend", "sketch": "s2", "angle": 1.570796,
             "flip": True,
             "fixed": {"near": [0.0, -0.02, 0.002], "hint": "the half that stays put"}},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    base = [c for c in port.calls if c[0] == "sheet_metal_feature"][0][1]
    assert base["feature_type"] == "base_flange" and base["thickness"] == 0.002
    assert base["bend_radius"] == 0.002 and base["k_factor"] == 0.5
    assert "reverse_thickness" not in base                # default direction not emitted
    bend = port.calls[-1]
    assert bend[0] == "sheet_metal_feature" and bend[1]["feature_type"] == "sketched_bend"
    assert abs(bend[1]["angle"] - 90.0) < 1e-3            # rad -> deg
    assert bend[1]["use_default_radius"] is True          # radius omitted in the node
    assert "bend_radius" not in bend[1]
    assert "bend_position" not in bend[1]                 # centerline default not emitted
    assert bend[1]["flip"] is True
    assert (bend[1]["fixed_x"], bend[1]["fixed_y"], bend[1]["fixed_z"]) == (0.0, -0.02, 0.002)


def test_sketched_bend_explicit_radius_and_position():
    g = {
        "schema_version": "0.5.5-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
             "profile": [{"kind": "rectangle", "width": 0.1, "height": 0.06}]},
            {"id": "sm1", "type": "sheet_metal", "sketch": "s1", "thickness": 0.002,
             "reverse_thickness": True},
            {"id": "s2", "type": "sketch", "ref": {"datum": "front", "offset": 0.002},
             "profile": [{"kind": "line", "x1": -0.06, "y1": 0.01, "x2": 0.06, "y2": 0.01}]},
            {"id": "sb1", "type": "sketched_bend", "sketch": "s2",
             "radius": 0.01, "position": "material_inside",
             "fixed": {"near": [0.0, -0.02, 0.002]}},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    base = [c for c in port.calls if c[1].get("feature_type") == "base_flange"][0][1]
    assert "bend_radius" not in base and "k_factor" not in base   # omitted -> tool defaults
    assert base["reverse_thickness"] is True                      # explicit direction emitted
    bend = port.calls[-1][1]
    assert abs(bend["angle"] - 90.0) < 1e-3               # angle omitted -> 90° default
    assert bend["bend_radius"] == 0.01 and "use_default_radius" not in bend
    assert bend["bend_position"] == "material_inside"
    assert "flip" not in bend


def test_mirror_runtime_name_substitution():
    # 4-1's Mirror1 idiom: mirror EARLIER features (by node id) about a canonical datum. The
    # compiler substitutes each node's RUNTIME feature name (localized fake port) and the plane
    # lowers to the canonical English name (language-independent selection downstream).
    g = {
        "schema_version": "0.5.5-draft", "units": "meters",
        "nodes": [
            {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
             "profile": [{"kind": "rectangle", "width": 0.1, "height": 0.06}]},
            {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss",
             "end": "blind", "depth": 0.01},
            {"id": "m1", "type": "mirror", "plane": {"datum": "right"}, "features": ["e1"]},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    mir = port.calls[-1]
    assert mir[0] == "create_pattern" and mir[1]["pattern_type"] == "mirror"
    assert mir[1]["plane"] == "Right Plane"
    names = json.loads(mir[1]["features_json"])
    assert names == ["Ekstrüzyon2"] or (len(names) == 1 and names[0].startswith("Ekstrüzyon"))


def test_sketch_face_ref_resolves_by_anchor():
    # v0.5.5: a sketch on a NON-axis-aligned support resolves its face ANCHOR to a face index
    # (plane containment — same resolver as extrude's up_to) -> create_sketch(on_face). Anchor
    # near the -X face's plane of the canned box fixture.
    g = {
        "schema_version": "0.5.5-draft", "units": "meters",
        "nodes": [
            {"id": "b1", "type": "box", "width": 0.05, "depth": 0.05, "height": 0.05},
            {"id": "s1", "type": "sketch", "ref": {"face": {"near": [-0.025, 0.01, 0.01],
                                                            "hint": "the -X side wall"}},
             "profile": [{"kind": "circle", "diameter": 0.01}]},
        ],
    }
    port = FakePort()
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    cs = [c for c in port.calls if c[0] == "create_sketch"][-1][1]
    assert cs.get("on_face") is True and cs.get("face_index") == 3   # the -X face (i=3)


def test_v055_validation_rejections():
    # Structural rejects, zero execution: bad bend position; missing fixed anchor; mirror with a
    # non-canonical datum; mirror referencing a non-feature-producer (a bare sketch); a sketch ref
    # mixing face + datum; a sheet_metal not immediately following its sketch.
    sk = {"id": "s1", "type": "sketch", "ref": {"datum": "front"},
          "profile": [{"kind": "rectangle", "width": 0.1, "height": 0.06}]}
    line_sk = {"id": "s2", "type": "sketch", "ref": {"datum": "front"},
               "profile": [{"kind": "line", "x1": 0, "y1": 0, "x2": 1, "y2": 0}]}
    bads = [
        [sk, {"id": "sm1", "type": "sheet_metal", "sketch": "s1", "thickness": 0.002},
         line_sk, {"id": "sb1", "type": "sketched_bend", "sketch": "s2", "position": "diagonal",
                   "fixed": {"near": [0, 0, 0]}}],
        [sk, {"id": "sm1", "type": "sheet_metal", "sketch": "s1", "thickness": 0.002},
         line_sk, {"id": "sb1", "type": "sketched_bend", "sketch": "s2"}],
        [sk, {"id": "e1", "type": "extrude", "sketch": "s1", "operation": "boss", "depth": 0.01},
         {"id": "m1", "type": "mirror", "plane": {"datum": "diagonal"}, "features": ["e1"]}],
        [sk, {"id": "m1", "type": "mirror", "plane": {"datum": "right"}, "features": ["s1"]}],
        [{"id": "s1", "type": "sketch",
          "ref": {"face": {"near": [0, 0, 0]}, "datum": "front"},
          "profile": [{"kind": "circle", "diameter": 0.01}]}],
        [sk, line_sk, {"id": "sm1", "type": "sheet_metal", "sketch": "s1", "thickness": 0.002}],
    ]
    for nodes in bads:
        port = FakePort()
        r = compile_and_run(port, {"schema_version": "0.5.5-draft", "units": "meters", "nodes": nodes})
        assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED", r.to_dict()
        assert port.calls == []


_EF_EDGES = json.dumps({
    "edges": [
        {"i": 3, "start": [0, 0, 0], "end": [0, 0, 0.1], "mid": [0.0, 0.0, 0.0]},
        {"i": 170, "start": [-0.208219, 0.236714, -0.015], "end": [-0.208219, 0.236714, -0.286779],
         "mid": [-0.208219, 0.236714, -0.15089]},
    ],
})


def test_edge_flange_custom_profile_lowering_and_frame():
    # SM-3 (4-1 Edge-Flange1 class): SELF-CONTAINED node -> resolve the attach edge BY INDEX
    # (the coordinate pick misses real edges — proven live on 4-1's slanted EF2 edge), then
    # [edge_flange_sketch, entities..., edge_flange_finish]. The API-generated profile sketch's
    # frame is unpredictable — here it measures with a MIRRORED ydir vs the original's, so
    # v -> -v and arc sweeps flip. radius/position replay the reader's values verbatim
    # (IR-ADR-014); the flange feature name comes from the FINISH op (mirror-seedable).
    declared = {"origin": [0.0, 0.2, 0.0], "xdir": [0.0, 0.0, -1.0], "ydir": [-1.0, 0.0, 0.0]}
    measured = {"origin": [0.0, 0.2, 0.0], "xdir": [0.0, 0.0, -1.0], "ydir": [1.0, 0.0, 0.0]}
    g = {
        "schema_version": "0.5.6-draft", "units": "meters",
        "nodes": [
            {"id": "ef1", "type": "edge_flange",
             "edge": {"near": [-0.208219, 0.236714, -0.15089], "hint": "left wall top edge"},
             "angle": 1.570796, "radius": 0.01, "position": "bend_outside",
             "frame": declared,
             "profile": [
                 {"kind": "line", "x1": 0.041779, "y1": 0.0, "x2": 0.251779, "y2": 0.0},
                 {"kind": "arc", "cx": 0.221779, "cy": 0.08356, "x1": 0.221779, "y1": 0.11356,
                  "x2": 0.251779, "y2": 0.08356, "dir": -1},
                 {"kind": "circle", "cx": 0.086779, "cy": 0.06856, "diameter": 0.02},
             ]},
            {"id": "m1", "type": "mirror", "plane": {"datum": "right"}, "features": ["ef1"]},
        ],
    }
    port = FakePort(edges=_EF_EDGES, sketch_frame=measured)
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    assert _tools(port) == ["analyze_model", "sheet_metal_feature", "add_sketch_entity",
                            "add_sketch_entity", "add_sketch_entity", "sheet_metal_feature",
                            "create_pattern"]
    gen = port.calls[1][1]
    assert gen["feature_type"] == "edge_flange_sketch"
    assert gen["edge_index"] == 170 and "ex" not in gen
    assert abs(gen["angle"] - 90.0) < 1e-4  # radians -> tool-boundary degrees (C4)
    line = port.calls[2][1]
    assert line["entity_type"] == "line" and (line["y1"], line["y2"]) == (0.0, 0.0)
    assert (line["x1"], line["x2"]) == (0.041779, 0.251779)  # u unchanged
    arc = port.calls[3][1]
    assert arc["entity_type"] == "arc_center"
    assert (arc["cy"], arc["y1"], arc["y2"]) == (-0.08356, -0.11356, -0.08356)  # v mirrored
    assert arc["direction"] == 1  # mirror flips the recorded dir=-1
    circ = port.calls[4][1]
    assert (circ["cx"], circ["cy"]) == (0.086779, -0.06856)
    fin = port.calls[5][1]
    assert fin["feature_type"] == "edge_flange_finish"
    assert fin["edge_index"] == 170
    assert fin["bend_radius"] == 0.01 and "use_default_radius" not in fin
    assert fin["bend_position"] == "bend_outside"
    # the mirror seeds on the FINISH op's created feature name
    pat = port.calls[6][1]
    assert pat["pattern_type"] == "mirror" and "SacFeature" in pat["features_json"]


def test_edge_flange_default_radius_and_frame_required():
    ident = {"origin": [0.0, 0.0, 0.0], "xdir": [1.0, 0.0, 0.0], "ydir": [0.0, 1.0, 0.0]}
    node = {"id": "ef1", "type": "edge_flange",
            "edge": {"near": [0.0, 0.0, 0.0]}, "frame": ident,
            "profile": [{"kind": "line", "x1": 0, "y1": 0, "x2": 0.1, "y2": 0}]}
    g = {"schema_version": "0.5.6-draft", "units": "meters", "nodes": [node]}
    port = FakePort(edges=_EF_EDGES, sketch_frame=ident)
    r = compile_and_run(port, g)
    assert r.status == "COMPLETED", r.to_dict()
    fin = port.calls[-1][1]
    assert fin["edge_index"] == 3
    # radius omitted -> the sheet's default; position omitted -> the tool default (not sent)
    assert fin["use_default_radius"] is True and "bend_radius" not in fin
    assert "bend_position" not in fin
    # execution echoes NO frame -> loud FRAME_UNAVAILABLE (the transform is mandatory here)
    port2 = FakePort(edges=_EF_EDGES, sketch_frame=None)
    r2 = compile_and_run(port2, g)
    assert r2.status == "FAILED" and r2.error["code"] == "FRAME_UNAVAILABLE"


def test_edge_flange_validation_rejections():
    # Structural rejects, zero execution: missing frame; missing edge anchor; empty profile;
    # unknown position.
    ident = {"origin": [0, 0, 0], "xdir": [1, 0, 0], "ydir": [0, 1, 0]}
    prof = [{"kind": "line", "x1": 0, "y1": 0, "x2": 0.1, "y2": 0}]
    bads = [
        [{"id": "ef1", "type": "edge_flange", "edge": {"near": [0, 0, 0]}, "profile": prof}],
        [{"id": "ef1", "type": "edge_flange", "frame": ident, "profile": prof}],
        [{"id": "ef1", "type": "edge_flange", "edge": {"near": [0, 0, 0]}, "frame": ident,
          "profile": []}],
        [{"id": "ef1", "type": "edge_flange", "edge": {"near": [0, 0, 0]}, "frame": ident,
          "profile": prof, "position": "diagonal"}],
    ]
    for nodes in bads:
        port = FakePort()
        r = compile_and_run(port, {"schema_version": "0.5.6-draft", "units": "meters", "nodes": nodes})
        assert r.status == "FAILED" and r.error["code"] == "VALIDATION_FAILED", r.to_dict()
        assert port.calls == []


_TESTS = [v for k, v in sorted(globals().items()) if k.startswith("test_") and callable(v)]


if __name__ == "__main__":
    failed = 0
    for t in _TESTS:
        try:
            t()
            print("ok   -", t.__name__)
        except AssertionError as ex:
            failed += 1
            print("FAIL -", t.__name__, "::", ex)
        except Exception as ex:  # noqa
            failed += 1
            print("ERR  -", t.__name__, "::", type(ex).__name__, ex)
    print("\n%d/%d passed" % (len(_TESTS) - failed, len(_TESTS)))
    sys.exit(1 if failed else 0)
