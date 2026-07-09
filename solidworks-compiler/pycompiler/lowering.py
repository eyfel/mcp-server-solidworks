"""Lowering — IR node -> ordered calls to the EXISTING low-level execution tools.

Pure functions: given a node (and, where a reference is semantic, the values the resolver
produced from live geometry), return an ordered list of Op(tool, params). The params use the
execution layer's own key names (same as tool-schemas.json / the adapter), since they are POSTed
straight to /api/tool/execute. No new execution tool is introduced — a single IR node becomes
many tool calls, which is the whole point of the IR (one model call -> many ops).

Conventions reused from the low-level tools:
  - lengths in meters; box/rectangle centred on the sketch origin.
  - through-all cut relies on the universal flipped-direction auto-retry (ADR-033), so the
    compiler does not compute 'reverse'.
  - path profiles (line/arc) carry EXACT frozen coordinates; no constraints are added — segments
    sharing identical endpoints already close the contour (add_sketch_entity guidance).

Runtime-value placeholder: an offset-datum sketch first creates a reference plane whose NAME only
exists in that op's RESPONSE (localized, e.g. 'Düzlem1' on a Turkish install — always use the
returned name, never a guessed one). Such a param carries the FROM_PREV_FEATURE sentinel and the
compiler substitutes it with the previous response's cadState.features[0] before executing.
"""
import json
import math
from collections import namedtuple

Op = namedtuple("Op", ["tool", "params", "note"])

# Sentinel (JSON-safe string, impossible as a real plane name) — see module docstring.
FROM_PREV_FEATURE = "__FROM_PREV_FEATURE__"

# Node-scoped runtime-name sentinel: node_feature('n6') -> "__NODE_FEATURE__:n6". The compiler
# replaces it with the CREATED feature name it recorded when node n6 executed (e.g. the cut the
# circular pattern seeds on — 'Cut-Extrude1' / localized). Same principle as FROM_PREV_FEATURE,
# but reaching back to ANY earlier node instead of the immediately preceding response.
_NODE_FEATURE_PREFIX = "__NODE_FEATURE__:"


def node_feature(node_id):
    return _NODE_FEATURE_PREFIX + node_id


def parse_node_feature(value):
    """Return the node id if value is a node-feature sentinel, else None."""
    if isinstance(value, str) and value.startswith(_NODE_FEATURE_PREFIX):
        return value[len(_NODE_FEATURE_PREFIX):]
    return None


_PLANE = {"top": "Top Plane", "front": "Front Plane", "right": "Right Plane"}


def _datum(node):
    ref = node.get("ref") or {}
    return ref.get("datum", "top")


def _offset(node):
    ref = node.get("ref") or {}
    return float(ref.get("offset") or 0.0)


def _rectangle_op(width, height, note, construction=False):
    w = float(width) / 2.0
    h = float(height) / 2.0
    params = {"entity_type": "rectangle", "x1": -w, "y1": -h, "x2": w, "y2": h}
    if construction:
        params["construction"] = True
    return Op("add_sketch_entity", params, note)


def lower_box(node):
    """box -> create_sketch(plane) + centred rectangle + boss extrude."""
    plane = _PLANE[_datum(node)]
    return [
        Op("create_sketch", {"plane": plane, "on_face": False}, "box: sketch on " + plane),
        _rectangle_op(node["width"], node["depth"], "box: centred rectangle"),
        Op("extrude_feature", {"feature_type": "boss", "depth": float(node["height"])}, "box: boss extrude"),
    ]


def lower_sketch(node, face_index=None):
    """sketch -> support op(s) + create_sketch + one entity per profile primitive (leaves the
    sketch active). Support preference for offset != 0 (decided with the user, 2026-07-05):
      1. face_index given (the resolver found an EXISTING face on that plane) -> sketch ON the
         face — matches original authoring, no scaffolding plane;
      2. else create a reference plane; its runtime name flows via FROM_PREV_FEATURE
         (localization-independent — 'Düzlem1'/'Plane1').
    Either way, coordinate correctness does NOT depend on the support: when the node carries the
    ORIGINAL sketch's `frame`, the compiler transforms every 2D coordinate into the frame
    create_sketch MEASURES on the rebuild."""
    offset = _offset(node)
    ops = []
    if face_index is not None:
        ops.append(Op("create_sketch", {"on_face": True, "face_index": int(face_index)},
                      "sketch on the existing face #%d at %s %+g m"
                      % (int(face_index), _datum(node), offset)))
    elif offset != 0.0:
        plane = _PLANE[_datum(node)]
        ops.append(Op("add_reference_geometry",
                      {"type": "plane", "ref_plane_name": plane, "offset": offset},
                      "sketch: offset plane %s %+g m" % (plane, offset)))
        ops.append(Op("create_sketch", {"plane": FROM_PREV_FEATURE, "on_face": False},
                      "sketch on the created offset plane"))
    else:
        plane = _PLANE[_datum(node)]
        ops.append(Op("create_sketch", {"plane": plane, "on_face": False}, "sketch on " + plane))
    for prim in node.get("profile", []):
        ops.append(_profile_op(prim))
    return ops


def _profile_op(prim):
    kind = prim.get("kind")
    construction = prim.get("construction") is True
    if kind == "rectangle":
        return _rectangle_op(prim["width"], prim["height"], "rectangle", construction)
    if kind == "circle":
        params = {"entity_type": "circle",
                  "cx": float(prim.get("cx") or 0.0), "cy": float(prim.get("cy") or 0.0),
                  "radius": float(prim["diameter"]) / 2.0}
        if construction:
            params["construction"] = True
        return Op("add_sketch_entity", params, "circle")
    if kind == "line":
        params = {"entity_type": "line",
                  "x1": float(prim["x1"]), "y1": float(prim["y1"]),
                  "x2": float(prim["x2"]), "y2": float(prim["y2"])}
        if construction:
            params["construction"] = True
        return Op("add_sketch_entity", params, "construction line" if construction else "line")
    if kind == "arc":
        # arc_center: exact centre + endpoints + sweep sense — radius-exact and stable for
        # shallow arcs (created with AddToDB, ADR on arc_center); mirrors analyze's 'dir'.
        params = {"entity_type": "arc_center",
                  "cx": float(prim["cx"]), "cy": float(prim["cy"]),
                  "x1": float(prim["x1"]), "y1": float(prim["y1"]),
                  "x2": float(prim["x2"]), "y2": float(prim["y2"]),
                  "direction": int(prim["dir"])}
        if construction:
            params["construction"] = True
        return Op("add_sketch_entity", params, "construction arc" if construction else "arc")
    raise ValueError("unsupported profile kind %r (ir_schema should have rejected it)" % kind)


def lower_extrude(node, up_to_face_index=None):
    """extrude -> extrude_feature on the active sketch.
    End condition: explicit 'end' field first (blind | through_all | up_to_surface — the latter
    needs the resolver-produced face index), else the v0-exp back-compat forms.
    'reversed': true (the recipe's flipped-direction flag) maps to the tool's 'reverse' param."""
    op = node["operation"]
    end = node.get("end")
    extra = {"reverse": True} if node.get("reversed") is True else {}
    if end == "up_to_surface":
        if up_to_face_index is None:
            raise ValueError("up_to_surface lowering requires a resolved face index")
        params = {"feature_type": op, "up_to_face_index": int(up_to_face_index)}
        params.update(extra)
        return [Op("extrude_feature", params,
                   "extrude %s (up to surface, face #%d)" % (op, up_to_face_index))]
    if end == "mid_plane":
        params = {"feature_type": op, "depth": float(node["depth"]), "mid_plane": True}
        params.update(extra)
        return [Op("extrude_feature", params, "extrude %s (mid-plane, total %g)" % (op, node["depth"]))]
    is_through = (end == "through_all" or node.get("through") is True
                  or node.get("depth") in ("through_all", "through"))
    if is_through:
        params = {"feature_type": op, "through": True}
        params.update(extra)
        return [Op("extrude_feature", params, "extrude %s (through-all)" % op)]
    params = {"feature_type": op, "depth": float(node["depth"])}
    params.update(extra)
    return [Op("extrude_feature", params, "extrude %s (blind)" % op)]


def lower_rib(node):
    """rib -> create_rib on the active sketch (single-line open profile, classic mid-plane rib).
    Non-default flags are emitted only when set — deterministic minimal params."""
    params = {"thickness": float(node["thickness"])}
    if node.get("two_sided") is False:
        params["two_sided"] = False
    if node.get("reverse_material_dir") is True:
        params["reverse_material_dir"] = True
    if node.get("is_norm_to_sketch") is True:
        params["is_norm_to_sketch"] = True
    return [Op("create_rib", params, "rib t=%g" % float(node["thickness"]))]


def lower_revolve(node):
    """revolve -> extrude_feature(revolve) on the active sketch. The axis segment (normally the
    profile's construction centerline) is passed as its exact 2D endpoints; the tool selects it
    at the segment midpoint while the sketch is still active and treats it as the centerline.
    Angle: IR carries RADIANS (the recipe's SI dimension, C4); the tool boundary takes DEGREES
    (the execution layer snaps near-360° to an exact full revolve)."""
    axis = node["axis"]
    angle_rad = float(node.get("angle") or (2.0 * math.pi))
    return [Op("extrude_feature",
               {"feature_type": "revolve",
                "angle": round(math.degrees(angle_rad), 6),
                "axis_x1": float(axis["x1"]), "axis_y1": float(axis["y1"]),
                "axis_x2": float(axis["x2"]), "axis_y2": float(axis["y2"])},
               "revolve %.6g rad about (%g,%g)-(%g,%g)"
               % (angle_rad, axis["x1"], axis["y1"], axis["x2"], axis["y2"]))]


def lower_circular_pattern(node):
    """circular_pattern -> reference axis from two datum planes + create_pattern(circular).
    Two runtime names flow through sentinels: the created AXIS (FROM_PREV_FEATURE — the
    immediately preceding op's response) and the SEED feature (node_feature(id) — recorded when
    that node executed). Both are localization-proof by construction."""
    d1, d2 = node["axis"]["datums"]
    return [
        Op("add_reference_geometry",
           {"type": "axis",
            "entity1_name": _PLANE[d1], "entity1_type": "PLANE",
            "entity2_name": _PLANE[d2], "entity2_type": "PLANE"},
           "pattern axis: %s ∩ %s" % (_PLANE[d1], _PLANE[d2])),
        Op("create_pattern",
           {"pattern_type": "circular",
            "feature_name": node_feature(node["feature"]),
            "axis_name": FROM_PREV_FEATURE,
            "count": int(node["count"]),
            "angle": float(node["angle_deg"]),
            "equal_spacing": node.get("equal_spacing", True) is not False},
           "circular pattern %dx @ %g deg (seed = node %s)"
           % (int(node["count"]), float(node["angle_deg"]), node["feature"])),
    ]


def lower_loft(node, profile_names):
    """loft -> ONE extrude_feature(loft) over 2+ profile sketches (COVERED since v0.5.4 — grown for
    level-2-2's Loft2 frustum). The profiles are referenced by their RUNTIME sketch names, which the
    compiler resolves from node_features (localization/numbering-proof, same principle as
    circular_pattern's seed) and passes here. They are serialized as a JSON-array STRING — the tool's
    contract. The profile sketches are ordinary earlier sketch nodes; the loft selects them by name,
    so they need not immediately precede it (grammar relaxation vs extrude/revolve/rib)."""
    return [Op("extrude_feature",
               {"feature_type": "loft", "profiles": json.dumps(profile_names)},
               "loft over %d profiles %s" % (len(profile_names), profile_names))]


def lower_sheet_metal(node):
    """sheet_metal (base flange) -> sheet_metal_feature(base_flange) on the active sketch (the
    profile sketch must immediately precede, like extrude). bend_radius omitted => the tool's
    default (= thickness); k_factor omitted => the tool default 0.5."""
    params = {"feature_type": "base_flange", "thickness": float(node["thickness"])}
    if node.get("bend_radius") is not None:
        params["bend_radius"] = float(node["bend_radius"])
    if node.get("k_factor") is not None:
        params["k_factor"] = float(node["k_factor"])
    if node.get("reverse_thickness") is True:
        # Thickness DIRECTION: reproduce the ORIGINAL base flange's own flag (the reader reports
        # it) — a wrong side puts every later bend-sketch plane off the sheet.
        params["reverse_thickness"] = True
    if node.get("symmetric_thickness") is True:
        # Material both ways off the sketch plane (±t/2). Reproduce the ORIGINAL's own flag: the
        # thickness-direction flags also define the intrinsic sheet orientation that downstream
        # bend directions are measured against (the 4-1 Bend1 mirror-fold lesson).
        params["symmetric_thickness"] = True
    return [Op("sheet_metal_feature", params,
               "base flange t=%g" % float(node["thickness"]))]


def lower_sketched_bend(node):
    """sketched_bend -> sheet_metal_feature(sketched_bend) on the active bend-line sketch.
    Angle: IR carries RADIANS (recipe SI, C4); the tool boundary takes DEGREES. radius OMITTED
    in the node => use_default_radius=true (the sheet's default — matches a recipe sketched_bend
    block with no radius). The fixed anchor's 3D point passes STRAIGHT THROUGH as fixed_x/y/z:
    the coordinate IS the side selector (SolidWorks derives which half stays put from the pick
    point), so unlike edge/face anchors there is NO index-resolution step."""
    angle_rad = float(node.get("angle") or (math.pi / 2.0))
    near = node["fixed"]["near"]
    params = {"feature_type": "sketched_bend",
              "angle": round(math.degrees(angle_rad), 6),
              "fixed_x": float(near[0]), "fixed_y": float(near[1]), "fixed_z": float(near[2])}
    if node.get("radius") is not None:
        params["bend_radius"] = float(node["radius"])
    else:
        params["use_default_radius"] = True
    if node.get("position") not in (None, "centerline"):
        params["bend_position"] = node["position"]
    if node.get("flip") is True:
        params["flip"] = True
    return [Op("sheet_metal_feature", params,
               "sketched bend %.6g rad, fixed near (%g,%g,%g)"
               % (angle_rad, near[0], near[1], near[2]))]


def lower_edge_flange(node, edge_index):
    """edge_flange (CUSTOM profile) -> SolidWorks' documented three-phase flow:
    (1) sheet_metal_feature(edge_flange_sketch): select the attach edge BY INDEX (the compiler
        resolves the node's edge anchor via resolve_edges_by_anchor — the coordinate pick misses
        real edges, KNOWN-LIMITATIONS #6, proven live on 4-1's slanted EF2 edge), generate the
        edge-linked profile sketch (an arbitrary user sketch is NOT accepted by the flange API),
        CLEAR its default content, leave it active. The op echoes the generated sketch's MEASURED
        frame and the compiler transforms every following 2D coordinate into it (node `frame` =
        the ORIGINAL profile sketch's frame — REQUIRED: the generated frame is unpredictable).
    (2) one add_sketch_entity per `profile` primitive (original coordinates, frame-transformed).
    (3) sheet_metal_feature(edge_flange_finish): exit + InsertSheetMetalEdgeFlange2 from the
        edited sketch + the same edge (same index — the sketch-gen op adds no solid geometry, so
        edge enumeration is stable between the two). radius/position replay the ORIGINAL's
        read-back values (IR-ADR-014); radius OMITTED => the sheet default; position OMITTED =>
        material_inside (the tool default). Angle: IR RADIANS -> tool-boundary DEGREES (C4)."""
    angle_deg = math.degrees(float(node.get("angle") or (math.pi / 2.0)))
    gen = {"feature_type": "edge_flange_sketch", "edge_index": int(edge_index),
           "angle": angle_deg}
    if node.get("flip") is True:
        gen["flip"] = True
    ops = [Op("sheet_metal_feature", gen,
              "edge flange: generate+clear the profile sketch on edge #%d" % int(edge_index))]
    for prim in node.get("profile", []):
        ops.append(_profile_op(prim))
    fin = {"feature_type": "edge_flange_finish", "edge_index": int(edge_index),
           "angle": angle_deg}
    if node.get("radius") is not None:
        fin["bend_radius"] = float(node["radius"])
    else:
        fin["use_default_radius"] = True
    if node.get("position") not in (None, "material_inside"):
        fin["bend_position"] = node["position"]
    ops.append(Op("sheet_metal_feature", fin, "edge flange: create from the edited profile"))
    return ops


def lower_mirror(node, feature_names):
    """mirror -> ONE create_pattern(mirror) over the referenced nodes' RUNTIME feature names
    (resolved by the compiler from node_features — localization/numbering-proof, same principle
    as circular_pattern's seed / loft's profiles). Serialized as a JSON-array STRING (ADR-022)."""
    d = node["plane"]["datum"]
    return [Op("create_pattern",
               {"pattern_type": "mirror",
                "features_json": json.dumps(feature_names),
                "plane": _PLANE[d]},
               "mirror %s about %s" % (feature_names, _PLANE[d]))]


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


def lower_fillet(node, edge_indices):
    """fillet -> ONE add_edge_feature over the resolver-matched edge indices.
    edge_indices is serialized as a JSON-array STRING — the tool's contract (ADR-022 idiom)."""
    idx = sorted(int(i) for i in edge_indices)
    return [Op("add_edge_feature",
               {"feature_type": "fillet",
                "radius_or_distance": float(node["radius"]),
                "edge_indices": "[%s]" % ",".join(str(i) for i in idx)},
               "fillet r=%g on edges %s" % (float(node["radius"]), idx))]


def lower_chamfer(node, edge_indices):
    """chamfer -> ONE add_edge_feature(chamfer) over the resolver-matched edge indices (same hybrid
    edge anchors as fillet, IR-ADR-008). Two modes:
      - distance_angle: setback D1 + angle. IR carries the angle in RADIANS (recipe SI, C4); the
        tool boundary takes DEGREES, so lowering converts (mirrors lower_revolve).
      - distance_distance: D1 + D2 (both meters). Directional — 'flip' swaps which face gets which
        setback (the tool's FlipDirection option, auto-recoverable like rib's reverse)."""
    idx = sorted(int(i) for i in edge_indices)
    ctype = node.get("chamfer_type", "distance_angle")
    params = {"feature_type": "chamfer",
              "chamfer_type": ctype,
              "radius_or_distance": float(node["distance"]),
              "edge_indices": "[%s]" % ",".join(str(i) for i in idx)}
    if node.get("flip") is True:
        params["chamfer_flip"] = True
    if ctype == "distance_distance":
        params["distance2"] = float(node["distance2"])
        note = "chamfer %g×%g on edges %s" % (float(node["distance"]), float(node["distance2"]), idx)
    else:
        angle_rad = float(node.get("angle") or (math.pi / 4.0))
        params["angle"] = round(math.degrees(angle_rad), 6)
        note = "chamfer d=%g @ %.6g rad on edges %s" % (float(node["distance"]), angle_rad, idx)
    return [Op("add_edge_feature", params, note)]
