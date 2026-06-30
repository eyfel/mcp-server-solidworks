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
    """Records calls, bumps state_version on writes, returns canned faces for analyze_model.
    fail_on=(tool, nth) injects a FAILED response on the nth call to that tool."""

    def __init__(self, faces=_BOX_FACES, fail_on=None):
        self._sv = 0
        self._faces = faces
        self._fail_on = fail_on
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
            return {"status": "COMPLETED", "stateVersion": self._sv,
                    "cadState": {"features": [self._faces]}}
        self._sv += 1  # a write op bumps state_version
        return {"status": "COMPLETED", "stateVersion": self._sv, "cadState": {"features": []}}


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
