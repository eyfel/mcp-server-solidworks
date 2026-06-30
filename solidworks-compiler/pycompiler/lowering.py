"""Lowering — IR node -> ordered calls to the EXISTING low-level execution tools.

Pure functions: given a node (and, for 'hole', the values the resolver produced from live
geometry), return an ordered list of Op(tool, params). The params use the execution layer's own
key names (same as tool-schemas.json / the adapter), since they are POSTed straight to
/api/tool/execute. No new execution tool is introduced — a single IR node becomes many tool
calls, which is the whole point of the IR (one model call -> many ops).

Conventions reused from the low-level tools:
  - lengths in meters; box/rectangle centred on the sketch origin.
  - through-all cut relies on the universal flipped-direction auto-retry (ADR-033), so the
    compiler does not compute 'reverse'.
"""
from collections import namedtuple

Op = namedtuple("Op", ["tool", "params", "note"])

_PLANE = {"top": "Top Plane", "front": "Front Plane", "right": "Right Plane"}


def _datum(node):
    ref = node.get("ref") or {}
    return ref.get("datum", "top")


def _rectangle_op(width, height, note):
    w = float(width) / 2.0
    h = float(height) / 2.0
    return Op("add_sketch_entity",
              {"entity_type": "rectangle", "x1": -w, "y1": -h, "x2": w, "y2": h}, note)


def lower_box(node):
    """box -> create_sketch(plane) + centred rectangle + boss extrude."""
    plane = _PLANE[_datum(node)]
    return [
        Op("create_sketch", {"plane": plane, "on_face": False}, "box: sketch on " + plane),
        _rectangle_op(node["width"], node["depth"], "box: centred rectangle"),
        Op("extrude_feature", {"feature_type": "boss", "depth": float(node["height"])}, "box: boss extrude"),
    ]


def lower_sketch(node):
    """sketch -> create_sketch(plane) + one entity per profile primitive (leaves it active)."""
    plane = _PLANE[_datum(node)]
    ops = [Op("create_sketch", {"plane": plane, "on_face": False}, "sketch on " + plane)]
    for prim in node.get("profile", []):
        kind = prim.get("kind")
        if kind == "rectangle":
            ops.append(_rectangle_op(prim["width"], prim["height"], "rectangle"))
        elif kind == "circle":
            r = float(prim["diameter"]) / 2.0
            ops.append(Op("add_sketch_entity",
                          {"entity_type": "circle", "cx": 0.0, "cy": 0.0, "radius": r}, "circle"))
    return ops


def lower_extrude(node):
    """extrude -> extrude_feature on the active sketch (boss blind, or cut through/blind)."""
    op = node["operation"]
    if op == "boss":
        return [Op("extrude_feature", {"feature_type": "boss", "depth": float(node["depth"])}, "extrude boss")]
    if node.get("through") is True or node.get("depth") in ("through_all", "through"):
        return [Op("extrude_feature", {"feature_type": "cut", "through": True}, "extrude cut (through-all)")]
    return [Op("extrude_feature", {"feature_type": "cut", "depth": float(node["depth"])}, "extrude cut (blind)")]


def lower_hole(node, face_index, center2d):
    """hole -> create_sketch(on_face, face_index) + circle at the resolved centre + through cut."""
    r = float(node["diameter"]) / 2.0
    cx, cy = center2d
    return [
        Op("create_sketch", {"on_face": True, "face_index": int(face_index)},
           "hole: sketch on top face #%d" % face_index),
        Op("add_sketch_entity", {"entity_type": "circle", "cx": cx, "cy": cy, "radius": r},
           "hole: circle at centre"),
        Op("extrude_feature", {"feature_type": "cut", "through": True}, "hole: through-all cut"),
    ]
