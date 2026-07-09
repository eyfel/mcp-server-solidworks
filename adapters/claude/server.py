import hashlib
import json
import os
import uuid
from datetime import datetime, timezone
from typing import Annotated, Literal, Optional
from pydantic import Field
from fastmcp import FastMCP
from execution_client import call_tool, get_state, ensure_ready as _ensure_ready, ExecutionLayerError
from response_mapper import map_response
# from ir_execution_port import run_feature_graph  # only used by the disabled test tool below

# P0.4 MCP hardening: discriminators are `Literal[...]` (invalid values rejected at the
# MCP schema level, before any REST/COM round-trip) and numeric params carry Pydantic
# `Field` constraints (units/ranges). Coordinates stay plain float (negative/zero are
# legitimate); open-ended selector strings (entity1_type, plane/material names) stay str
# so valid selectors are never rejected. Editing this file requires reconnecting the
# `solidworks` MCP server (no hot-reload — KNOWN-LIMITATIONS #4).

mcp = FastMCP("solidworks-execution-adapter")

# Tracks state_version in memory. Starts at 0, updated after every response.
# Auto-resyncs from the execution layer on an INVALID_STATE_VERSION mismatch
# (e.g. the execution server was restarted after a rebuild).
_state_version: int = 0


def _next_operation_id() -> str:
    return str(uuid.uuid4())


def _update_state_version(response: dict) -> None:
    global _state_version
    sv = response.get("stateVersion")
    if sv is not None:
        _state_version = sv


def _is_state_mismatch(response: dict) -> bool:
    if response.get("status") != "FAILED":
        return False
    return (response.get("error") or {}).get("code") == "INVALID_STATE_VERSION"


def _call(tool_name: str, params: dict) -> str:
    """Send a tool call to the execution layer.

    If the server reports an INVALID_STATE_VERSION mismatch (it was restarted and
    its state_version reset while this adapter kept its old value), fetch the
    authoritative state_version and retry once with a fresh operation_id. This
    removes the need to restart the adapter after every execution-layer rebuild.
    """
    global _state_version
    response = call_tool(tool_name, _next_operation_id(),
                         _state_version, params)
    if _is_state_mismatch(response):
        _state_version = get_state()
        response = call_tool(
            tool_name, _next_operation_id(), _state_version, params)
    _update_state_version(response)
    return map_response(response)


# ---------------------------------------------------------------------------
# Tool: ensure_ready  (lifecycle / bootstrap — not a state-versioned CAD op)
# ---------------------------------------------------------------------------
@mcp.tool()
def ensure_ready() -> str:
    """Bring the SolidWorks environment up and confirm it is ready to use.

    Starts the execution server if it isn't running, and launches SolidWorks if it is closed
    (just attaches if it's already open). Does NOT open or create any document — use
    open_new_part / create_drawing for that (so assembly/drawing workflows aren't forced into a
    part). Safe to call anytime; idempotent. Call this first at the start of a session, or
    whenever a tool fails with a connection / COM-attach error."""
    global _state_version
    body = _ensure_ready()
    if not body.get("comAttached"):
        raise RuntimeError(
            "SolidWorks is not ready | "
            f"launch_error={body.get('launchError') or body.get('ensureError')}"
        )
    sv = body.get("stateVersion")
    if sv is not None:
        _state_version = sv
    return (
        f"READY | server=UP | "
        f"com_attached={body.get('comAttached')} | "
        f"sw_launched={body.get('swLaunched')} | "
        f"active_document={body.get('activeDocument')} | "
        f"sw_version={body.get('swVersion')} | "
        f"state_version={body.get('stateVersion')}"
    )


# ---------------------------------------------------------------------------
# Tool: open_new_part
# ---------------------------------------------------------------------------
@mcp.tool()
def open_new_part(template_path: str = "") -> str:
    """Open a new SolidWorks part document."""
    params = {}
    if template_path:
        params["template_path"] = template_path
    return _call("open_new_part", params)


# ---------------------------------------------------------------------------
# Tool: open_document
# ---------------------------------------------------------------------------
@mcp.tool()
def open_document(file_path: str) -> str:
    """Open an EXISTING document from disk (counterpart to open_new_part, which makes a BLANK doc).
    file_path: full path to the file.
      - Native .sldprt / .sldasm / .slddrw open directly.
      - Foreign .ipt (Inventor) / .CATPart (CATIA) / .step / .iges / .x_t import as a PART via SolidWorks
        3D Interconnect (must be enabled in Tools > Options > Import, and the translator licensed). If
        unsupported, returns a clear OPEN_FAILED so you can convert to STEP or defer that file.
    Makes the opened document active and bumps state_version. Use this to load a sample part before
    producing its drawing (create_drawing + add_drawing_view)."""
    return _call("open_document", {"file_path": file_path})


# ---------------------------------------------------------------------------
# Tool: create_sketch
# ---------------------------------------------------------------------------
@mcp.tool()
def create_sketch(
    plane: str = "",
    on_face: bool = False,
    face_index: int = -1,
    face_x: float = 0.0,
    face_y: float = 0.0,
    face_z: float = 0.0,
) -> str:
    """Create a new sketch on a plane OR on an existing model face.
    Default (on_face=False): provide plane (e.g. 'Front Plane', 'Top Plane', or a named ref plane like 'Plane1').
    On a model face (on_face=True), pick the face one of two ways:
      • face_index (PREFERRED) — the integer index of the target face from analyze_model(analysis_type='faces').
        Robust: it selects the face directly, so it works on a revolve end-cap / shaft end where a coordinate
        pick is ambiguous (the flat circular face's centre lies on the revolve axis → FACE_NOT_FOUND).
      • face_x/face_y/face_z — a point in METERS lying ON the target planar face (interior, NOT on an edge).
    Use on_face for features on existing geometry (recess/hub/keyway on a part face, a bore on a shaft end);
    plane names cannot reach model faces. face_index takes priority over face_x/y/z when >= 0."""
    params = {"plane": plane, "on_face": on_face,
              "face_x": face_x, "face_y": face_y, "face_z": face_z}
    if face_index >= 0:
        params["face_index"] = face_index
    return _call("create_sketch", params)


# ---------------------------------------------------------------------------
# Tool: add_sketch_entity
# ---------------------------------------------------------------------------
@mcp.tool()
def add_sketch_entity(
    entity_type: Literal["rectangle", "circle", "line", "arc", "arc_center", "ellipse", "spline", "fillet", "chamfer"],
    x1: float = 0.0,
    y1: float = 0.0,
    x2: float = 0.0,
    y2: float = 0.0,
    xm: float = 0.0,
    ym: float = 0.0,
    cx: float = 0.0,
    cy: float = 0.0,
    radius: float = 0.0,
    vx: float = 0.0,
    vy: float = 0.0,
    distance: float = 0.0,
    direction: float = 1.0,
    points: str = "[]",
    construction: bool = False,
) -> str:
    """Add a sketch entity to the active sketch.
    entity_type='rectangle':  uses x1,y1 (first corner) and x2,y2 (opposite corner).
    entity_type='circle':     uses cx,cy (center) and radius.
    entity_type='line':       uses x1,y1 (start) and x2,y2 (end).
    entity_type='arc':        uses x1,y1 (start), x2,y2 (end), xm,ym (mid-arc point) — a 3-point arc.
    entity_type='arc_center': uses cx,cy (exact center), x1,y1 (start), x2,y2 (end) and
                              direction (+1 CCW, -1 CW). Prefer this over 'arc' when the exact
                              center/radius are known (e.g. straight from analyze_model): it
                              guarantees the radius and is numerically stable for shallow/near-
                              collinear arcs where the 3-point circle-fit is unreliable.
    entity_type='ellipse':    uses cx,cy (center), x1,y1 (a point on the MAJOR axis), x2,y2 (a point
                              on the MINOR axis). Mirrors analyze_model's ellipse segment exactly.
    entity_type='spline':     uses points — a JSON array STRING of flat through-points
                              '[x1,y1,x2,y2,...]' (>= 2 points). Mirrors analyze_model's spline segment
                              (its 'points'). Round-trips its through-points exactly.
    entity_type='fillet':     uses vx,vy (vertex to round) and radius.
    entity_type='chamfer':    uses vx,vy (vertex to cut) and distance.
    direction: only for 'arc_center' — sweep sense from start to end (+1 CCW, -1 CW); pick the
               sign that yields the intended (usually minor) arc. Ignored by other types.
    points: only for 'spline' — a JSON array string of flat (x,y) through-points, e.g.
            '[-0.2,0.0,-0.18,0.04,-0.14,0.05]'. Ignored by other types.
    construction: True makes the created entity CONSTRUCTION/reference geometry (centerline,
            symmetry axis, hole-position scaffolding) — it guides the profile but adds no edge.
            Mirrors analyze_model's per-segment 'construction' flag, so an analyzed sketch's
            construction entities can be reproduced faithfully.
    This tool MIRRORS analyze_model: every sketch segment analyze_model emits (line, arc/circle,
    ellipse, spline, with cx/cy/x1/y1/x2/y2/radius/points/construction, partial arcs also 'dir'
    +1 CCW / -1 CW = arc_center's 'direction') maps 1:1 onto an entity_type here, so an
    analyzed sketch can be rebuilt without dropping/simplifying any curve.
    On COMPLETED the result includes result_geometry: the REAL geometry SolidWorks created (read back,
    not echoed) — use it to self-verify the radius/endpoints match your intent before moving on.
    Rebuilding from exact (frozen) coordinates: do NOT add coincident constraints between segments —
    segments that share identical endpoint coordinates already close the profile, so a cut/extrude finds
    the region automatically. Adding constraints is unnecessary and can fail and roll the sketch back.
    All coordinates in document units (meters)."""
    return _call(
        "add_sketch_entity",
        {
            "entity_type": entity_type,
            "x1": x1, "y1": y1, "x2": x2, "y2": y2,
            "xm": xm, "ym": ym,
            "cx": cx, "cy": cy, "radius": radius,
            "vx": vx, "vy": vy,
            "distance": distance,
            "direction": direction,
            "points": json.loads(points) if points else [],
            "construction": construction,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_sketch_constraint
# ---------------------------------------------------------------------------
@mcp.tool()
def add_sketch_constraint(
    constraint_type: Literal[
        "horizontal", "vertical", "coincident", "parallel",
        "perpendicular", "tangent", "equal", "midpoint",
    ],
    px1: float,
    py1: float,
    px2: float = 0.0,
    py2: float = 0.0,
    entity_type1: Literal["SKETCHSEGMENT", "SKETCHPOINT"] = "SKETCHSEGMENT",
    entity_type2: Literal["SKETCHSEGMENT", "SKETCHPOINT"] = "SKETCHSEGMENT",
) -> str:
    """Apply a geometric constraint to sketch entities in the active sketch.
    constraint_type: 'horizontal', 'vertical' — single entity (px1, py1 only).
    constraint_type: 'coincident', 'parallel', 'perpendicular', 'tangent', 'equal', 'midpoint' — requires two entities (px2, py2).
    px1/py1: point on the first entity. px2/py2: point on the second entity (two-entity constraints).
    entity_type1/2: 'SKETCHSEGMENT' (default) or 'SKETCHPOINT' for point-based constraints like coincident/midpoint.
    All coordinates in document units (meters)."""
    return _call(
        "add_sketch_constraint",
        {
            "constraint_type": constraint_type,
            "px1": px1, "py1": py1,
            "px2": px2, "py2": py2,
            "entity_type1": entity_type1,
            "entity_type2": entity_type2,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_dimension
# ---------------------------------------------------------------------------
@mcp.tool()
def add_dimension(px: float, py: float, value: Annotated[float, Field(gt=0, description="Dimension value in METERS (e.g. 0.05 = 50mm)")], label_offset_x: float = 0.0, label_offset_y: float = -0.015) -> str:
    """Apply a smart dimension to the sketch segment nearest to point (px, py).
    px/py should be on or very near the target segment — e.g. the midpoint of a line.
    label_offset_x/y control where the dimension label is placed relative to the segment point.
    Works for any sketch geometry (rectangles, polygons, complex profiles)."""
    return _call(
        "add_dimension",
        {"px": px, "py": py, "value": value, "label_offset_x": label_offset_x,
            "label_offset_y": label_offset_y},
    )


# ---------------------------------------------------------------------------
# Tool: extrude_feature
# ---------------------------------------------------------------------------
@mcp.tool()
def extrude_feature(
    depth: Annotated[float, Field(
        ge=0, description="Extrusion depth in METERS (required > 0 for boss/cut)")] = 0.0,
    feature_type: Literal["boss", "cut", "revolve", "sweep", "loft"] = "boss",
    angle: Annotated[float, Field(
        gt=0, le=360, description="Revolve angle in DEGREES")] = 360.0,
    axis_x1: float = 0.0,
    axis_y1: float = 0.0,
    axis_x2: float = 0.0,
    axis_y2: float = 0.001,
    path_sketch: str = "",
    profiles: str = "[]",
    reverse: bool = False,
    through: bool = False,
    up_to_face_index: int = -1,
    mid_plane: bool = False,
) -> str:
    """Extrude/feature the active sketch profile.
    feature_type='boss' (default): solid extrusion, requires depth.
    feature_type='cut': material removal, requires depth and existing solid body.
    feature_type='revolve': solid of revolution — angle in DEGREES (default 360 = full revolve); axis defined by axis_x1/y1 to axis_x2/y2 (midpoint of that segment selects the centerline).
    feature_type='sweep': sweeps profile along a path — requires path_sketch (name of the path sketch).
    feature_type='loft': lofts through multiple profiles — requires profiles (JSON array of sketch names, e.g. '[\"Sketch1\",\"Sketch2\"]').
    reverse (boss/cut): flip the feature direction. Needed e.g. for a cut/boss sketched on a part FACE, where the material is on the opposite side from the default.
    through (boss/cut): through-all end condition (depth is ignored). Use for through holes/cuts instead of guessing a depth.
    up_to_face_index (boss/cut): >= 0 selects the UP-TO-SURFACE end condition — the feature terminates
        exactly ON that model face (index from analyze_model(analysis_type='faces'), same indexing as
        create_sketch face_index). depth is ignored, like through. Use when the recipe says
        extrude end='up_to_surface', or to land a boss/cut precisely on existing geometry without
        computing a blind depth. -1 (default) = off.
    mid_plane (boss/cut): True selects the MID-PLANE end condition — the feature extrudes
        SYMMETRICALLY about the sketch plane; depth is the TOTAL width. Use when the recipe says
        extrude end='mid_plane'. Conflicts with through/up_to_face_index (pick one).
    depth is required only for a BLIND or MID-PLANE boss/cut (not for through, up_to_face_index, revolve, sweep, or loft).
    Sheet metal: a 'cut' on a sheet-metal body (incl. holes after a bend) is handled automatically — it
        applies a Normal Cut and inserts before the Flat-Pattern; no special params needed. Use
        analyze_model(analysis_type='edges') to get real edge midpoints for selection.
    On COMPLETED the result includes result_geometry {volume, faces, edges} of the body after the feature
        — verify the step from this instead of a separate analyze_model + manual volume math.
    Exits sketch mode automatically before executing."""
    return _call(
        "extrude_feature",
        {
            "depth": depth,
            "feature_type": feature_type,
            "angle": angle,
            "axis_x1": axis_x1, "axis_y1": axis_y1,
            "axis_x2": axis_x2, "axis_y2": axis_y2,
            "path_sketch": path_sketch,
            "profiles": profiles,
            "reverse": reverse,
            "through": through,
            "up_to_face_index": up_to_face_index,
            "mid_plane": mid_plane,
        },
    )


# ---------------------------------------------------------------------------
# Tool: create_rib
# ---------------------------------------------------------------------------
@mcp.tool()
def create_rib(
    thickness: Annotated[float, Field(gt=0, description="Rib thickness in METERS (e.g. 0.005 = 5mm)")],
    two_sided: bool = True,
    reverse_thickness_dir: bool = False,
    reverse_material_dir: bool = False,
    is_norm_to_sketch: bool = False,
) -> str:
    """Create a RIB from the ACTIVE sketch (consumes it, like extrude_feature). The rib profile
    is OPEN geometry — typically a single line (e.g. the diagonal between an L-bracket's legs);
    SolidWorks extends it to the surrounding walls and thickens it.
    thickness: rib thickness in METERS.
    two_sided: True (default) thickens symmetrically on both sides of the sketch; False = single
        side (then reverse_thickness_dir picks which side).
    reverse_material_dir: flip which side of the profile the rib material FILLS toward. Wrong
        direction is auto-recovered: if no feature results, the tool retries flipped (your
        explicit value is tried first).
    is_norm_to_sketch: False (default) = extrusion parallel to the sketch (the classic line-rib
        on a mid/symmetry plane); True = normal to the sketch.
    Draft is deliberately not exposed (grow on demand). On COMPLETED the result includes
    result_geometry {volume, faces, edges} — verify the step from this. Requires an existing
    solid body for the rib to attach to."""
    return _call(
        "create_rib",
        {
            "thickness": thickness,
            "two_sided": two_sided,
            "reverse_thickness_dir": reverse_thickness_dir,
            "reverse_material_dir": reverse_material_dir,
            "is_norm_to_sketch": is_norm_to_sketch,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_edge_feature
# ---------------------------------------------------------------------------
@mcp.tool()
def add_edge_feature(
    feature_type: Literal["fillet", "chamfer"],
    radius_or_distance: Annotated[float, Field(gt=0, description="Fillet radius / chamfer FIRST-face setback (D1) in METERS (e.g. 0.01 = 10mm)")],
    edge_indices: str = "",
    ex: float = 0.0,
    ey: float = 0.0,
    ez: float = 0.0,
    edges_json: str = "[]",
    chamfer_type: Literal["distance_angle", "distance_distance"] = "distance_angle",
    angle: Annotated[float, Field(gt=0, lt=90, description="Chamfer angle in DEGREES (distance_angle mode only; default 45)")] = 45.0,
    distance2: Annotated[float, Field(ge=0, description="Chamfer SECOND-face setback (D2) in METERS (distance_distance mode only; must be > 0 there)")] = 0.0,
    chamfer_flip: bool = False,
) -> str:
    """Apply a 3D edge modifier to solid body edges (post-extrusion). Distinct from sketch fillet/chamfer.
    feature_type='fillet': rounds selected edge(s) with given radius_or_distance.
    feature_type='chamfer': cuts a chamfer on selected edge(s). Two modes via chamfer_type:
      - 'distance_angle' (default): radius_or_distance = setback (D1), angle = the chamfer angle in DEGREES
        (default 45 → the classic equal 45° chamfer). angle is ignored in the other mode.
      - 'distance_distance': radius_or_distance = first-face setback (D1), distance2 = second-face setback
        (D2, METERS, required > 0). A distance-distance chamfer is DIRECTIONAL — if D1/D2 land on the wrong
        faces (the chamfer leans the wrong way), set chamfer_flip=True to swap the sides (FlipDirection).
    PREFERRED edge selection: edge_indices — a JSON array of integer indices from analyze_model(analysis_type='edges'),
    e.g. '[3,5]'. This selects edges directly (no coordinate pick), so it works on crowded or CONCAVE edges
    (inner-corner / small-radius step edges) that a coordinate pick can't disambiguate. Takes priority over
    edges_json / ex-ey-ez.
    Fallback — single edge: ex/ey/ez (3D point on the edge); multiple edges: edges_json e.g. '[{\"ex\":0.05,\"ey\":0.05,\"ez\":0.05}]'.
    All coordinates/distances in METERS. Requires an active part document with an existing solid body."""
    params = {
        "feature_type": feature_type,
        "radius_or_distance": radius_or_distance,
        "ex": ex, "ey": ey, "ez": ez,
        "edges_json": edges_json,
    }
    if edge_indices:
        params["edge_indices"] = edge_indices
    if feature_type == "chamfer":
        params["chamfer_type"] = chamfer_type
        params["angle"] = angle
        params["distance2"] = distance2
        params["chamfer_flip"] = chamfer_flip
    return _call("add_edge_feature", params)


# ---------------------------------------------------------------------------
# Tool: create_drawing
# ---------------------------------------------------------------------------
@mcp.tool()
def create_drawing(model_path: str = "") -> str:
    """Open a new SolidWorks drawing document (A3 sheet, 1:1 scale).
    model_path: optional path to the part/assembly to reference. If omitted, an empty drawing is created.
    Note: add_drawing_view requires the referenced part to be saved to disk."""
    return _call("create_drawing", {"model_path": model_path})


# ---------------------------------------------------------------------------
# Tool: add_drawing_view
# ---------------------------------------------------------------------------
@mcp.tool()
def add_drawing_view(
    view_type: Literal["front", "top", "right", "isometric", "back", "bottom", "left"],
    pos_x: float = 0.1,
    pos_y: float = 0.1,
    scale: Annotated[float, Field(
        gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
    model_path: str = "",
) -> str:
    """Add a model view to the active drawing sheet.
    view_type: 'front', 'top', 'right', 'isometric', 'back', 'bottom', 'left'.
    pos_x/pos_y: position on the drawing sheet in meters.
    scale: view scale (default 1.0 = 1:1).
    model_path: path to the part file; if omitted, uses the first open part document.
    Requires the part to be saved to disk (in-memory parts cannot be projected)."""
    return _call(
        "add_drawing_view",
        {"view_type": view_type, "pos_x": pos_x, "pos_y": pos_y,
            "scale": scale, "model_path": model_path},
    )


# ---------------------------------------------------------------------------
# Tool: add_flat_pattern_view
# ---------------------------------------------------------------------------
@mcp.tool()
def add_flat_pattern_view(
    pos_x: float = 0.1,
    pos_y: float = 0.1,
    scale: Annotated[float, Field(
        gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
    model_path: str = "",
    config_name: str = "",
    hide_bend_lines: bool = False,
    flip_view: bool = False,
) -> str:
    """Add a FLAT PATTERN view of a SHEET-METAL part to the active drawing — the unfolded blank
    with bend lines/notes. This is the correct, standard way to detail sheet metal: sheet metal is
    dimensioned on its flat pattern (overall blank size, hole positions, bend lines), NOT on the
    folded orthographic views. Use this instead of (or alongside an isometric of) the folded views
    for a sheet-metal part; then call auto_dimension_drawing to place the blank/hole dimensions.

    pos_x/pos_y: position on the drawing sheet in meters.
    scale: view scale (default 1.0 = 1:1).
    model_path: path to the sheet-metal part; if omitted, uses the first open part document.
    config_name: configuration to flatten; if omitted, the part's active configuration is used
        (falling back to 'Default').
    hide_bend_lines: hide the bend lines in the flat pattern (default False).
    flip_view: flip the flat pattern view (default False).

    The part must be sheet metal (have a Flat-Pattern feature) and saved to disk, else returns
    FLAT_PATTERN_VIEW_FAILED. Bumps state_version; result_geometry echoes {view_name, config}."""
    return _call(
        "add_flat_pattern_view",
        {"pos_x": pos_x, "pos_y": pos_y, "scale": scale, "model_path": model_path,
            "config_name": config_name, "hide_bend_lines": hide_bend_lines,
            "flip_view": flip_view},
    )


# ---------------------------------------------------------------------------
# Tool: add_drawing_dimension
# ---------------------------------------------------------------------------
@mcp.tool()
def add_drawing_dimension(
    px: float,
    py: float,
    value: Annotated[float, Field(gt=0, description="Dimension value in METERS")],
    label_offset_x: float = 0.0,
    label_offset_y: float = -0.015,
) -> str:
    """Add a smart dimension to a drawing view segment nearest to point (px, py).
    Works the same way as add_dimension but operates in a drawing document context.
    px/py should be on or very near the target segment in drawing sheet coordinates (meters).
    Requires an active drawing document."""
    return _call(
        "add_drawing_dimension",
        {"px": px, "py": py, "value": value, "label_offset_x": label_offset_x,
            "label_offset_y": label_offset_y},
    )


# ---------------------------------------------------------------------------
# Tool: auto_dimension_drawing
# ---------------------------------------------------------------------------
@mcp.tool()
def auto_dimension_drawing(
    all_views: bool = True,
    include_unmarked: bool = False,
    eliminate_duplicates: bool = True,
) -> str:
    """Automatically transfer the MODEL's driving dimensions into the active drawing's views
    (SolidWorks 'Insert Model Items > Dimensions'). This is the PREFERRED way to dimension a
    drawing — far more reliable than add_drawing_dimension's coordinate pick, because the
    dimensions come straight from the model's real parametric dimensions and are placed for you.
    Call it AFTER create_drawing + add_drawing_view(s); then verify with analyze_drawing
    (dimension_count should be > 0 and the values should match the model's driving dims).

    all_views: insert into all drawing views (True) or only the currently selected view (False). Default True.
    include_unmarked: also insert driving dimensions NOT marked for drawing (i.e. ALL driving dims), not just
        those flagged 'marked for drawing'. More complete but noisier. Default False — if a marked-only pass
        inserts 0 dimensions (the part's dims weren't marked for drawing), re-call with include_unmarked=True.
    eliminate_duplicates: avoid inserting the same model dimension into more than one view. Default True.

    Returns COMPLETED with result_geometry.inserted_count (the number of dimensions placed; 0 is a valid
    outcome). Requires an active drawing with at least one model view. Bumps state_version."""
    return _call(
        "auto_dimension_drawing",
        {"all_views": all_views, "include_unmarked": include_unmarked,
            "eliminate_duplicates": eliminate_duplicates},
    )


# ---------------------------------------------------------------------------
# Tool: auto_center_marks
# ---------------------------------------------------------------------------
@mcp.tool()
def auto_center_marks(
    include_slots: bool = True,
    extended_lines: bool = True,
) -> str:
    """Automatically place center marks (with extended centerlines) on every hole/slot in every
    model view of the active drawing (SolidWorks 'Auto Insert > Center Marks'). This is the robust,
    automatic way to add centerlines to a holed part's drawing — no coordinate picking. Call it
    after the views exist (create_drawing + add_drawing_view), typically alongside
    auto_dimension_drawing, for a difficulty-2-grade drawing of a bracket/flange with holes.

    include_slots: also add slot center marks/centerlines for linear & arc slots (not just round holes). Default True.
    extended_lines: draw the extended centerlines through each hole rather than a small cross. Default True.

    Returns COMPLETED with result_geometry.center_marks (total marks placed across all views; 0 if the
    part has no holes/slots). Bumps state_version. Requires an active drawing with model views."""
    return _call(
        "auto_center_marks",
        {"include_slots": include_slots, "extended_lines": extended_lines},
    )


# ---------------------------------------------------------------------------
# Tool: add_hole_callout
# ---------------------------------------------------------------------------
@mcp.tool()
def add_hole_callout(px: float, py: float) -> str:
    """Insert a hole callout (e.g. '4× Ø8 THRU') on the hole edge nearest the sheet point (px, py)
    in the active drawing. Coordinate-based selection — same fragile point pick as add_drawing_dimension
    (KNOWN-LIMITATIONS #6), so place px/py right on the hole's projected circular edge. For centerlines
    prefer auto_center_marks (robust/automatic); use this for explicit per-hole callouts where wanted.
    px/py: a point on the target hole's projected circle, in drawing sheet coordinates (meters).
    Requires an active drawing document. Bumps state_version."""
    return _call("add_hole_callout", {"px": px, "py": py})


# ---------------------------------------------------------------------------
# Tool: add_section_view
# ---------------------------------------------------------------------------
@mcp.tool()
def add_section_view(
    px: float,
    py: float,
    edge_x: Optional[float] = None,
    edge_y: Optional[float] = None,
    x1: Optional[float] = None,
    y1: Optional[float] = None,
    x2: Optional[float] = None,
    y2: Optional[float] = None,
    label: str = "A",
    flip: bool = False,
    scale: float = 0.0,
) -> str:
    """Create a SECTION VIEW that cuts the model so internal/blind features become visible, dimensionable
    edges. Use this when a feature's size can't be shown in a projected view — most importantly a BLIND
    POCKET's DEPTH (its floor is a hidden edge in an orthographic view, so auto_dimension_drawing can't
    place the depth).

    Two ways to define the cut (provide ONE pair-set):
    - EDGE mode (edge_x, edge_y): cut ALONG an existing straight edge/line already projected in a view —
      point at it. Best when a real edge lies on the plane you want (the cut runs collinear with it).
    - LINE mode (x1,y1 → x2,y2): draw a cut line. Best for cutting THROUGH a feature's interior (e.g. the
      MIDDLE of a pocket, where no edge exists). The line must fully cross the view through the feature.
    All coordinates are drawing sheet coordinates in meters (KNOWN-LIMITATIONS #6 — coordinate-based).

    px,py: where to place the section view (meters) — also picks which side the section projects toward.
    label: section label (default 'A' → 'SECTION A-A'). flip: reverse the cut/viewing direction.
    scale: optional section view scale; inherits the parent view's scale if 0/omitted.

    After the section lands, call auto_dimension_drawing or add_drawing_dimension to dimension the
    now-visible depth. Bumps state_version. Requires an active drawing with a parent model view. If it
    returns SECTION_VIEW_FAILED, the drawing state may be wedged by prior failed attempts — a fresh
    server/drawing state clears it."""
    params: dict = {"px": px, "py": py, "label": label, "flip": flip}
    for k, v in (("edge_x", edge_x), ("edge_y", edge_y),
                 ("x1", x1), ("y1", y1), ("x2", x2), ("y2", y2)):
        if v is not None:
            params[k] = v
    if scale and scale > 0:
        params["scale"] = scale
    return _call("add_section_view", params)


# ---------------------------------------------------------------------------
# Tool: export_document
# ---------------------------------------------------------------------------
@mcp.tool()
def export_document(format: Literal["STEP", "IGES", "STL", "PDF", "DWG", "DXF"], file_path: str) -> str:
    """Export the active SolidWorks document to a file. The document remains open after export.
    format: 'STEP', 'IGES', 'STL', 'PDF', 'DWG', 'DXF'.
    file_path: full output path including filename and extension (e.g. 'C:/output/part.step').
    Note: DWG and DXF require an active drawing document (use create_drawing first).
    PDF uses SolidWorks PDF export options. STEP/IGES/STL work with part or assembly documents.
    IGES must use a .igs extension (.iges is auto-corrected to .igs)."""
    # export does NOT change CAD state; the execution layer returns the same state_version.
    return _call("export_document", {"format": format, "file_path": file_path})


# ---------------------------------------------------------------------------
# Tool: batch_export
# ---------------------------------------------------------------------------
@mcp.tool()
def batch_export(file_path_base: str, formats_json: str) -> str:
    """Export the active document to multiple formats in one call.
    file_path_base: output path without extension (e.g. 'C:/output/mypart').
    formats_json: JSON array of format strings (e.g. '[\"STEP\",\"STL\",\"PDF\"]').
    Each format is exported as file_path_base.<ext>. DWG/DXF skipped if active doc is not a drawing.
    Partial success is reported in the response Features list."""
    return _call("batch_export", {"file_path_base": file_path_base, "formats_json": formats_json})


# ---------------------------------------------------------------------------
# Tool: verify_state
# ---------------------------------------------------------------------------
@mcp.tool()
def verify_state() -> str:
    """Read and return the current CAD state without modifying it."""
    return _call("verify_state", {})


# ---------------------------------------------------------------------------
# Tool: close_document
# ---------------------------------------------------------------------------
@mcp.tool()
def close_document(save: bool = False) -> str:
    """Close the active SolidWorks document. Set save=True to save before closing."""
    return _call("close_document", {"save": save})


# ---------------------------------------------------------------------------
# Tool: save_document
# ---------------------------------------------------------------------------
@mcp.tool()
def save_document(file_path: str = "") -> str:
    """Save the active SolidWorks document to disk.
    file_path: full output path including the document extension
    (.sldprt for parts, .sldasm for assemblies, .slddrw for drawings).
    If omitted, saves in place (only works if the document was saved before).
    A part must be saved to disk before it can be referenced by a drawing view."""
    return _call("save_document", {"file_path": file_path})


# ---------------------------------------------------------------------------
# Tool: analyze_model
# ---------------------------------------------------------------------------
@mcp.tool()
def analyze_model(
    analysis_type: Literal["mass_properties", "geometry", "edges", "faces", "features", "sketch", "feature_map"],
    name: str = "",
    from_feature: str = "",
    to_feature: str = "",
) -> str:
    """Analyze the active SolidWorks part document (read-only, does NOT change state).
    analysis_type='mass_properties': returns volume, surface_area, center of gravity (cx, cy, cz) in document units.
    analysis_type='geometry': returns bodies, faces, edges, vertices counts of all solid bodies.
    analysis_type='edges': returns a JSON object listing EVERY solid edge with its start/end/MIDPOINT 3D coords
        in meters (rounded to 6 decimals; closed/circular edges also report length) and a stable index `i`. Use
        the index `i` for add_edge_feature(edge_indices=...) — robust for crowded/concave edges — or a MIDPOINT
        for coordinate-based selection (e.g. edge_flange's ex/ey/ez). On a very large part (many hundreds of edges)
        this is the slowest mode; prefer 'features' for a general understanding.
    analysis_type='faces': returns a JSON object listing EVERY solid face with a stable index `i`; planar faces
        also carry normal, area, and a representative on-plane point (meters, rounded to 6 decimals). Use the
        index `i` for create_sketch(on_face=True, face_index=i) — robust where a coordinate pick is ambiguous
        (e.g. a revolve end-cap / shaft end whose centre lies on the axis).
    analysis_type='features': the part's COMPACT RECIPE — this is the default "understand the part" read. Returns
        the ordered feature tree (name/type; suppressed shown only when true), each feature's driving dimensions
        (deduped, SI meters/radians, rounded to 6 decimals), a per-sketch SUMMARY (segment counts + an inline
        {cx,cy,r} for single-full-circle profiles), pattern semantics (instances / spacing_deg / distinct_instances
        / wraps), and equations / globals. Each sketch reports its plane as {ref, offset} where ref is the
        canonical English default plane ('Front Plane'/'Top Plane'/'Right Plane') and offset is the signed
        distance along its normal (offset 0 = that default plane itself; offset != 0 = a parallel face/plane
        at that height — sketch on the face there). Each extrude/cut reports extrude:{end (blind/through_all/
        ...), depth (blind only), reversed (present only when the direction is flipped)} so you know HOW it
        was built; for direction use the sketch's plane.offset (offset != 0 ⇒ a face, so a cut goes into the
        part). Features are listed in tree (history) ORDER — reproduce them in that exact order (order
        matters wherever features overlap). It does NOT dump every sketch segment's coordinates — that keeps the payload small
        and is enough to understand the part and build dimension/pattern variants. Use this first.
    analysis_type='sketch' (requires name=...): ONE sketch's FULL geometry — every segment's coordinates (rounded
        to 6 decimals) plus its plane. Use this only when you actually need exact sketch geometry (e.g. to reproduce
        an irregular profile); get the sketch name from the 'features' read first. name = the sketch feature name,
        e.g. 'Sketch2'.
    analysis_type='feature_map': per-feature geometry ATTRIBUTION — deterministically answers "which edges/faces
        did each feature act on?" (e.g. WHICH edge a fillet/chamfer was applied to — never guess an anchor). Walks
        the tree base→end with the rollback bar and diffs the topology between stops INSIDE the tool; returns one
        compact JSON {feature_count, map:[{feature, type, delta:{faces,edges,vertices}, consumed_edges:[{mid,len}],
        created_faces:[{point, normal?, planar, area}]}]} (6-decimal meters). consumed_edges = edges that existed
        BEFORE the feature and were consumed by it (both endpoints gone; merely trimmed neighbours excluded) — use
        a consumed edge's `mid` as the fillet/chamfer anchor `near` point (it references the PRE-feature geometry,
        exactly what the IR anchor needs). created_faces = genuinely new surfaces (trimmed existing planes excluded).
        Sketches/planes are listed without a delta; suppressed features are skipped. Optional from_feature/to_feature
        (tree names) limit the walk to a range. NON-DESTRUCTIVE: the rollback bar is restored to the end, nothing is
        saved — but it does rebuild the model feature-by-feature, so it is the slowest mode on big trees.
    Does NOT increment state_version."""
    return _call("analyze_model", {"analysis_type": analysis_type, "name": name,
                                   "from_feature": from_feature, "to_feature": to_feature})


# ---------------------------------------------------------------------------
# Tool: analyze_drawing
# ---------------------------------------------------------------------------
@mcp.tool()
def analyze_drawing(include_geometry: bool = False) -> str:
    """Analyze the ACTIVE drawing document (read-only, does NOT change state) — the drawing-side sibling
    of analyze_model. Returns a JSON object {view_count, dimension_count, views:[{name, type, scale, pos,
    dimensions:[{name, value_si}]}]}: each view's name, type (swDrawingViewTypes_e int), scale, sheet
    position [x,y] in meters, and its display dimensions (full-name + SI value, meters/radians, rounded to
    6 decimals). The FIRST view is the drawing SHEET (interpret accordingly). Use it to (a) check a drawing
    you produced — do its dimensions match the model? — and (b) read a drawing back for re-modeling.

    include_geometry (default False): also return each view's PROJECTED 2D GEOMETRY as clean primitives —
        geometry:{lines:[{x1,y1,x2,y2}], curves:[{n,x1,y1,xm,ym,x2,y2}], frame}. This is the CLEAN SHAPE
        for reverse-engineering a part from its drawing, independent of dimension-line clutter (you read
        the shape as vectors, not from a cluttered raster). Coordinates are in MODEL-scale METERS centered
        on each view's centroid (z=0). Crucially, the line segments carry the UP/DOWN / which-face profile
        structure that a dimension VALUE alone cannot (e.g. on a revolved flange's side view you can read
        directly that a recess is on the BOTTOM face and a raised face is on the TOP). 'curves' are
        tessellated arcs/circles reduced to start/mid/end (best-effort). Source: IView.GetPolylines7.
        Heavier payload — use it when you need the shape to rebuild, not for a quick dim check.

    Requires an active drawing document (call create_drawing first)."""
    return _call("analyze_drawing", {"include_geometry": include_geometry})


# ---------------------------------------------------------------------------
# Tool: get_selection
# ---------------------------------------------------------------------------
@mcp.tool()
def get_selection() -> str:
    """Report what the USER currently has selected in the SolidWorks GUI — the inverse of index-based selection.
    When the user clicks geometry in the SolidWorks window (e.g. while telling you what to do — "put a hole on
    THIS face"), call this to learn exactly which entity they mean. Each selected item is mapped to the SAME
    stable index analyze_model(faces/edges) reports, so you can act on it immediately:
      • a FACE  → {type:'face', i, planar, normal?, area, point?} → create_sketch(on_face=True, face_index=i)
      • an EDGE → {type:'edge', i, start, end, mid}              → add_edge_feature(edge_indices='[i]')
      • a VERTEX → {type:'vertex', point:[x,y,z]};  a reference PLANE → {type:'plane', name}
    Returns {selected_count, selection:[...]}. i = -1 if a selected face/edge couldn't be matched to a
    solid-body index. Read-only: does NOT change state_version and does NOT clear the selection — but call it
    BEFORE any tool that clears the selection (create_sketch, add_edge_feature, extrude cut, etc.), otherwise
    the user's pick is gone. If selected_count is 0, ask the user to click the face/edge they mean."""
    return _call("get_selection", {})


# ---------------------------------------------------------------------------
# Tool: edit_sketch
# ---------------------------------------------------------------------------
@mcp.tool()
def edit_sketch(sketch_name: str) -> str:
    """Reopen an existing sketch for editing. Counterpart to create_sketch.
    sketch_name: exact name of the sketch feature in the feature tree (e.g. 'Sketch1').
    Returns COMPLETED with ActiveSketch set to sketch_name.
    After editing, call extrude_feature (or any feature) to exit the sketch."""
    return _call("edit_sketch", {"sketch_name": sketch_name})


# ---------------------------------------------------------------------------
# Tool: add_reference_geometry
# ---------------------------------------------------------------------------
@mcp.tool()
def add_reference_geometry(
    type: Literal["plane", "axis", "point"],
    ref_plane_name: str = "",
    offset: float = 0.0,
    entity1_name: str = "",
    entity1_type: str = "PLANE",
    entity2_name: str = "",
    entity2_type: str = "PLANE",
    px: float = 0.0,
    py: float = 0.0,
    pz: float = 0.0,
) -> str:
    """Create reference geometry (plane, axis, or point) in the active part.
    type='plane': offset plane from ref_plane_name by offset (meters). E.g. ref_plane_name='Front Plane', offset=0.05.
    type='axis': axis at intersection of two entities. Provide entity1_name, entity1_type, entity2_name, entity2_type (e.g. 'Top Plane'/'PLANE' and 'Right Plane'/'PLANE').
    type='point': reference point at vertex (px, py, pz). Coordinates must match an existing vertex exactly.
    Returns the created feature name (e.g. 'Plane1', 'Axis1', 'Point1')."""
    return _call(
        "add_reference_geometry",
        {
            "type": type,
            "ref_plane_name": ref_plane_name,
            "offset": offset,
            "entity1_name": entity1_name,
            "entity1_type": entity1_type,
            "entity2_name": entity2_name,
            "entity2_type": entity2_type,
            "px": px, "py": py, "pz": pz,
        },
    )


# ---------------------------------------------------------------------------
# Tool: create_pattern
# ---------------------------------------------------------------------------
@mcp.tool()
def create_pattern(
    pattern_type: Literal["linear", "circular", "mirror"],
    feature_name: str = "",
    spacing: Annotated[float, Field(
        gt=0, description="Linear pattern spacing in METERS")] = 0.01,
    count: Annotated[int, Field(
        ge=2, description="Total instances incl. seed (>= 2)")] = 2,
    direction: Literal["X", "Y", "Z"] = "X",
    count2: Annotated[int, Field(
        ge=1, description="Second-direction instances (1 = off)")] = 1,
    spacing2: Annotated[float, Field(
        gt=0, description="Second-direction spacing in METERS")] = 0.01,
    axis_name: str = "",
    angle: Annotated[float, Field(
        gt=0, le=360, description="Circular pattern angle in DEGREES")] = 90.0,
    equal_spacing: bool = True,
    features_json: str = "[]",
    plane: str = "",
    geometry_pattern: bool = False,
) -> str:
    """Create a linear, circular, or mirror feature pattern.
    pattern_type='linear': repeats feature_name along direction ('X', 'Y', or 'Z') with spacing (meters) and count instances.
        Optional second direction: count2>1 with spacing2.
        Direction maps to default planes: X→Right Plane normal, Y→Top Plane normal, Z→Front Plane normal.
    pattern_type='circular': repeats feature_name around axis_name (reference axis, e.g. 'Axis1') with count instances.
        Create the axis first with add_reference_geometry(type='axis', ...).
        equal_spacing=True (default): angle is the TOTAL spread (use 360 for a full ring); count instances are
            evenly divided across it and NEVER overlap (count distinct == count).
        equal_spacing=False: angle is the spacing BETWEEN adjacent instances. count*angle can exceed 360, in which
            case later instances WRAP and overlap earlier ones, so the number of DISTINCT instances < count.
    Round-trip with analyze_model: a CirPattern reports instances, equal_spacing, spacing_deg, plus the
        EFFECTIVE distinct_instances and a wraps flag. To reproduce a pattern faithfully, EITHER replay the
        stored form verbatim (count=instances, angle=spacing_deg, equal_spacing as reported) OR, when it wraps,
        use the simpler equivalent full ring: count=distinct_instances, angle=360, equal_spacing=True.
        (Example: a gear analyzed as 30 @ 15° equal_spacing=False has distinct_instances=24 → reproduce as
        either 30 @ 15° False or 24 @ 360° True; both yield the same 24 teeth.)
    pattern_type='mirror': mirrors one or more FEATURES about a plane. features_json = a JSON array of
        feature tree names, e.g. '["Edge-Flange1","Sketched Bend2"]' (feature_name works for a single
        feature). plane = the mirror plane name ('Right Plane' default; canonical English default-plane
        names work on any localization, or a created reference plane's name). geometry_pattern=True
        mirrors the geometry without solving each feature (faster; default False = SW default).
        Round-trip: analyze_model(features) reports a MirrorPattern's mirror:{plane, features} — replay
        those values verbatim. feature_name/spacing/count/direction/axis params are ignored for mirror.
    On COMPLETED the result includes result_geometry {volume, faces, edges} after the pattern — verify from
        this instead of a separate analyze_model.
    Returns the created pattern feature name."""
    return _call(
        "create_pattern",
        {
            "pattern_type": pattern_type,
            "feature_name": feature_name,
            "spacing": spacing,
            "count": count,
            "direction": direction,
            "count2": count2,
            "spacing2": spacing2,
            "axis_name": axis_name,
            "angle": angle,
            "equal_spacing": equal_spacing,
            "features_json": features_json,
            "plane": plane,
            "geometry_pattern": geometry_pattern,
        },
    )


# ---------------------------------------------------------------------------
# Tool: set_part_material
# ---------------------------------------------------------------------------
@mcp.tool()
def set_part_material(
    material_name: str,
    library: str = "SolidWorks Materials",
) -> str:
    """Assign a material to the active part document. Applied to all configurations.
    material_name: exact library name (e.g. '1060 Alloy', 'AISI 1020', 'ABS') — must match the SOLIDWORKS Materials database exactly (e.g. '1060 Alloy', NOT 'Aluminum 1060 Alloy').
    library: material library name (default 'SolidWorks Materials').
    The applied material is visible in Mass Properties and the feature tree.
    Requires an active part document — not a drawing."""
    return _call("set_part_material", {"material_name": material_name, "library": library})


# ---------------------------------------------------------------------------
# Tool: sheet_metal_feature
# ---------------------------------------------------------------------------
@mcp.tool()
def sheet_metal_feature(
    feature_type: Literal["base_flange", "edge_flange", "edge_flange_sketch", "edge_flange_finish",
                          "flat_pattern", "sketched_bend"],
    thickness: Annotated[float, Field(
        gt=0, description="Sheet thickness in METERS")] = 0.001,
    bend_radius: Annotated[float, Field(
        gt=0, description="Bend radius in METERS")] = 0.001,
    k_factor: Annotated[float, Field(
        gt=0, lt=1, description="Neutral-axis K-factor (0..1)")] = 0.5,
    ex: float = 0.0,
    ey: float = 0.0,
    ez: float = 0.0,
    flange_length: Annotated[float, Field(
        gt=0, description="Edge flange length in METERS; use >= 2*thickness+1mm (KNOWN-LIMITATIONS #11)")] = 0.02,
    angle: Annotated[float, Field(
        gt=0, le=180, description="Bend angle in DEGREES (edge_flange and sketched_bend; default 90)")] = 90.0,
    use_default_radius: bool = False,
    flip: bool = False,
    bend_position: Literal["centerline", "material_inside", "material_outside", "bend_outside"] = "centerline",
    fixed_face_index: int = -1,
    fixed_x: float = 0.0,
    fixed_y: float = 0.0,
    fixed_z: float = 0.0,
    reverse_thickness: bool = False,
    symmetric_thickness: bool = False,
    clear_profile: bool = True,
    edge_index: int = -1,
) -> str:
    """Create sheet metal features on the active part.
    feature_type='base_flange': creates a sheet metal base from the active sketch profile.
        thickness: sheet thickness (meters). bend_radius: bend radius (default = thickness). k_factor: default 0.5.
        reverse_thickness: thicken to the OPPOSITE side of the sketch plane (default False). Direction matters
        for reproduction — when rebuilding an analyzed part, derive it from which side of the sketch plane the
        original blank's big faces sit (e.g. feature_map's SMBaseFlange created faces).
        symmetric_thickness: thicken BOTH ways off the sketch plane (±t/2, mid-plane style; default False).
        When rebuilding, reproduce the original's own flag (analyze_model(features) reports it on the
        SMBaseFlange) — the flags also set the intrinsic sheet orientation downstream bends fold against.
        Exits sketch mode automatically.
    feature_type='edge_flange': adds a DEFAULT-profile flange to an existing sheet metal edge at (ex, ey, ez).
        flange_length: flange length (meters). angle: bend angle in degrees (default 90).
    feature_type='edge_flange_sketch' + 'edge_flange_finish': the CUSTOM-profile edge flange, two calls.
        edge_flange_sketch selects the attach edge — pass edge_index (from analyze_model(edges),
        PREFERRED; a coordinate pick can miss a real edge) or ex/ey/ez — generates the edge-linked
        profile sketch (the flange API accepts ONLY a sketch it generated), clears its default content
        (clear_profile=True) and leaves it ACTIVE, echoing the sketch's MEASURED frame in the result —
        express your profile coordinates in THAT frame. Draw the profile with add_sketch_entity, then
        call edge_flange_finish with the SAME edge_index/coords (+ angle, bend_radius or
        use_default_radius, bend_position from the original's edge_flange recipe block) to create the
        flange. The custom profile itself defines the flange outline/length (flange_length is ignored).
    feature_type='sketched_bend': bends the sheet about the bend LINE(S) in the ACTIVE sketch (draw the
        line(s) on a sheet face with create_sketch + add_sketch_entity first — the sketch must still be
        active). The side of the sheet that stays PUT is the fixed face: pass fixed_face_index (from
        analyze_model(faces), PREFERRED — index-robust) or fixed_x/y/z (a 3D point ON that face, meters).
        angle: bend angle in DEGREES (default 90). bend_radius: bend radius in meters, or set
        use_default_radius=True to use the sheet's default (bend_radius is then ignored). flip: reverse
        the bend direction (up vs down). bend_position: where the bend sits relative to the line —
        'centerline' (default), 'material_inside', 'material_outside', or 'bend_outside' — matches the
        `position` value analyze_model(features) reports on an SM3dBend, so a recipe value replays as-is.
        A sketch with MULTIPLE bend lines creates one feature with one bend per line (like 4-1's Sketch6).
    feature_type='flat_pattern': unfolds all bends to create the flat pattern view.
        No additional parameters required. Requires an existing base_flange feature."""
    params = {
        "feature_type": feature_type,
        "thickness": thickness,
        "bend_radius": bend_radius,
        "k_factor": k_factor,
        "ex": ex, "ey": ey, "ez": ez,
        "flange_length": flange_length,
        "angle": angle,
        "use_default_radius": use_default_radius,
        "flip": flip,
        "bend_position": bend_position,
        "fixed_x": fixed_x, "fixed_y": fixed_y, "fixed_z": fixed_z,
        "reverse_thickness": reverse_thickness,
        "symmetric_thickness": symmetric_thickness,
        "clear_profile": clear_profile,
    }
    if fixed_face_index >= 0:
        params["fixed_face_index"] = fixed_face_index
    if edge_index >= 0:
        params["edge_index"] = edge_index
    return _call("sheet_metal_feature", params)


# ---------------------------------------------------------------------------
# Tool: modify_dimension
# ---------------------------------------------------------------------------
@mcp.tool()
def modify_dimension(
    name: str,
    value: float,
) -> str:
    """Change a named display dimension's value — the UNIVERSAL parametric edit (variant keystone).
    name: the dimension full-name exactly as analyze_model(features) reports it, e.g.
          'D1@Boss-Extrude1@Part.Part' or 'D1@Sketch1'.
    value: the new value in SI units — METERS for a length/distance, RADIANS for an angle
          (e.g. 0.036 = 36mm; 0.5236 = 30°). NOT mm/degrees.
    Workflow: analyze_model(analysis_type='features') → read a feature's dimension name + value (SI) →
          modify_dimension(name, new_value) → analyze again to confirm. Bumps state_version (geometry
          changes, unlike analyze). On COMPLETED the result carries result_geometry
          {dimension, requested, effective}: the value read back from SolidWorks AFTER rebuild — verify
          it matches your intent (an equation/relation may have driven it elsewhere). This is how you
          build variants (e.g. resize a boss, widen a slot, change a gear's tooth-spacing)."""
    return _call("modify_dimension", {"name": name, "value": value})


# ---------------------------------------------------------------------------
# Tool: edit_feature
# ---------------------------------------------------------------------------
@mcp.tool()
def edit_feature(
    feature_name: str,
    action: Literal["suppress", "unsuppress", "delete", "rename"],
    new_name: str = "",
) -> str:
    """Structurally edit an existing feature by name (suppress / unsuppress / delete / rename).
    feature_name: exact feature-tree name, e.g. 'Boss-Extrude1', 'Cut-Extrude1', 'CirPattern1'
          (get the names from analyze_model(analysis_type='features')).
    action='suppress':   removes the feature (and anything dependent on it) from the build WITHOUT deleting it.
    action='unsuppress': brings a suppressed feature back into the build.
    action='delete':     permanently removes the feature (works for ANY feature type, incl. sketches and reference geometry).
    action='rename':     renames the feature; requires new_name.
    new_name: required only for action='rename' (ignored otherwise).
    Bumps state_version. WARNING: suppress and delete CHANGE the model topology, so any coordinate /
    edge / face selection captured earlier may no longer resolve — re-run analyze_model
    (analysis_type='edges' or 'features') after a structural edit before selecting geometry again."""
    return _call(
        "edit_feature",
        {"feature_name": feature_name, "action": action, "new_name": new_name},
    )


# ---------------------------------------------------------------------------
# Tool: activate_document
# ---------------------------------------------------------------------------
@mcp.tool()
def activate_document(title: str) -> str:
    """Switch the ACTIVE SolidWorks document to an already-open one by its title (e.g. 'gear' or
    'gear.SLDPRT'). Use this to read/compare another open part — e.g. activate the original, analyze_model
    it, then activate your copy — without OS window switching. Does NOT change geometry or state_version.
    The document must already be OPEN. Returns the now-active document title."""
    return _call("activate_document", {"title": title})


# ---------------------------------------------------------------------------
# Tool: save_analysis  (Phase A analysis pipeline — ADAPTER-ONLY, no C#; ADR-040)
# ---------------------------------------------------------------------------
ANALYSIS_SCHEMA_VERSION = "0.1.0-draft"  # cad-planner/contracts/analysis-artifact.schema.json


def _call_raw(tool_name: str, params: dict) -> dict:
    """Like _call() but returns the RAW ExecutionResponse dict (no string mapping, no raise).

    Used by adapter-side orchestration (save_analysis) that needs the structured payloads the
    analyze tools carry in `result_geometry`. Same one-shot resync-retry on INVALID_STATE_VERSION."""
    global _state_version
    response = call_tool(tool_name, _next_operation_id(), _state_version, params)
    if _is_state_mismatch(response):
        _state_version = get_state()
        response = call_tool(tool_name, _next_operation_id(), _state_version, params)
    _update_state_version(response)
    return response


def _analysis_items(response: dict) -> list:
    """Return the `cadState.features` list an analyze_model call carries its payload in.

    Payload shapes (C#-owned): 'features' mode = ONE item holding the whole recipe as a JSON
    string; 'mass_properties'/'geometry' modes = 'key=value' strings. Raises RuntimeError on a
    FAILED response (mirrors map_response's error discipline)."""
    if response.get("status") != "COMPLETED":
        err = response.get("error") or {}
        raise RuntimeError(f"{err.get('code')}: {err.get('message')}")
    return (response.get("cadState") or {}).get("features") or []


def _kv_dict(items: list) -> dict:
    """Parse ['volume=4,22503E-06', 'faces=12', ...] into a dict with real numbers.

    Numeric values may use a COMMA decimal separator (localized SolidWorks formatting);
    normalize to float, and to int for plain integer counts."""
    out = {}
    for item in items:
        if not isinstance(item, str) or "=" not in item:
            continue
        key, _, raw = item.partition("=")
        raw = raw.strip()
        try:
            num = float(raw.replace(",", "."))
            is_plain_int = raw.isdigit() or (raw.startswith("-") and raw[1:].isdigit())
            out[key.strip()] = int(num) if is_plain_int else num
        except ValueError:
            out[key.strip()] = raw
    return out


def _collect_parameters(node, out: list, feature_name: str = "") -> None:
    """Walk the features recipe and lift dimension entries into the flat named-parameter table.

    A dimension entry is a dict whose 'name' is a FULL dimension name (contains '@', e.g.
    'D1@Boss-Extrude1@Part1.Part' — the modify_dimension target) with a numeric value under
    'value_si' or 'value'. Tolerant by design: the exact recipe keys are C#-owned; anything that
    doesn't match the shape is simply not lifted."""
    if isinstance(node, dict):
        owner = node.get("name") if node.get("type") else None
        value = node.get("value_si", node.get("value"))
        name = node.get("name")
        if isinstance(name, str) and "@" in name and isinstance(value, (int, float)):
            out.append({"name": name, "value_si": value, "feature": feature_name})
            return
        for child in node.values():
            _collect_parameters(child, out, owner or feature_name)
    elif isinstance(node, list):
        for child in node:
            _collect_parameters(child, out, feature_name)


@mcp.tool()
def save_analysis(file_path: str) -> str:
    """Analyze a part FILE and persist its analysis ARTIFACT — the entry tool of the analysis
    pipeline. Opens the part (activates it if already open), runs the standard reads (features
    recipe + mass_properties + geometry), computes the file's sha256, and writes
    `<folder>/.solidpilot/<filename>.analysis.json` per
    cad-planner/contracts/analysis-artifact.schema.json.

    The artifact is a CACHE of the file's state at analysis time: consumers must compare
    identity.source_hash against the current file and re-analyze on a mismatch. The `ir` block
    is left null here (the AI/IR pass fills it later — see cad-planner/recipe.md). The part is
    left OPEN and ACTIVE for follow-up work.

    file_path: absolute path of the .SLDPRT to analyze (v0 is parts-only; drawing/assembly
        artifacts arrive with later pipeline steps).
    Returns a token-frugal summary (artifact path + counts) — the artifact JSON stays on disk;
    read it from there when the full content is needed."""
    src = os.path.abspath(file_path)
    if not os.path.isfile(src):
        return f"FAILED | FILE_NOT_FOUND | {src}"
    if not src.lower().endswith(".sldprt"):
        return ("FAILED | UNSUPPORTED_TYPE | save_analysis v0 analyzes .SLDPRT parts only "
                "(drawing/assembly artifacts come with later pipeline steps)")
    with open(src, "rb") as fh:
        sha = hashlib.sha256(fh.read()).hexdigest()

    # Open (or activate, if it is already open under its title).
    opened = _call_raw("open_document", {"file_path": src})
    if opened.get("status") == "FAILED":
        activated = _call_raw("activate_document", {"title": os.path.basename(src)})
        if activated.get("status") == "FAILED":
            err = opened.get("error") or {}
            return f"FAILED | OPEN_FAILED | {err.get('code')}: {err.get('message')}"

    try:
        features_items = _analysis_items(_call_raw("analyze_model", {"analysis_type": "features", "name": ""}))
        mass_kv = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
        geometry = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "geometry", "name": ""})))
    except RuntimeError as ex:
        return f"FAILED | ANALYZE_FAILED | {ex}"

    notes = []
    if not features_items:
        return "FAILED | NO_PAYLOAD | analyze_model(features) returned an empty payload"
    try:
        features = json.loads(features_items[0])
    except (ValueError, TypeError):
        features = {"raw": features_items}
        notes.append("features payload was not parseable JSON — stored raw (reader refinement pending)")

    mass = {
        "volume_m3": mass_kv.get("volume"),
        "surface_area_m2": mass_kv.get("surface_area"),
        "cg": {"x": mass_kv.get("cx"), "y": mass_kv.get("cy"), "z": mass_kv.get("cz")},
    }

    parameters: list = []
    _collect_parameters(features, parameters)

    # V1 relationships: drawing files next to the part whose stem is the part's stem, optionally
    # followed by a suffix word (e.g. 'part-1 drawing.SLDDRW' for 'part-1.SLDPRT').
    folder = os.path.dirname(src)
    stem = os.path.splitext(os.path.basename(src))[0].lower()
    drawings = sorted(
        os.path.join(folder, f) for f in os.listdir(folder)
        if f.lower().endswith(".slddrw")
        and (os.path.splitext(f)[0].lower() == stem
             or os.path.splitext(f)[0].lower().startswith(stem + " "))
    )

    artifact = {
        "identity": {
            "source_path": src,
            "source_filename": os.path.basename(src),
            "source_hash": "sha256:" + sha,
            "source_mtime": datetime.fromtimestamp(os.path.getmtime(src), timezone.utc).isoformat(),
            "schema_version": ANALYSIS_SCHEMA_VERSION,
            "analyzed_at": datetime.now(timezone.utc).isoformat(),
            "generator": {"kind": "deterministic"},
        },
        "document_type": "part",
        "recipe": {
            "features": features,
            "mass_properties": mass,
            "geometry": geometry,
            "bbox": None,
            "material": None,
            "equations": [],
        },
        "parameters": parameters,
        "ir": None,
        "relationships": {"drawings": drawings, "category": None, "cluster_signals": {}},
        "notes": notes + ["bbox/material/equations extraction pending refinement (A2 live pass)"],
    }

    out_dir = os.path.join(folder, ".solidpilot")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, os.path.basename(src) + ".analysis.json")
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(artifact, fh, ensure_ascii=False, indent=2)

    geo = geometry if isinstance(geometry, dict) else {}
    feature_count = len(features.get("features", [])) if isinstance(features, dict) else "?"
    return (f"COMPLETED | artifact={out_path} | features={feature_count} | "
            f"parameters={len(parameters)} | geometry={{bodies:{geo.get('bodies')},"
            f"faces:{geo.get('faces')},edges:{geo.get('edges')},vertices:{geo.get('vertices')}}} | "
            f"drawings_linked={len(drawings)} | hash=sha256:{sha[:12]}")


# ---------------------------------------------------------------------------
# Tool: rebuild_from_ir  (adapter-only — the analysis pipeline's IR door, IR-ADR-005)
# ---------------------------------------------------------------------------
@mcp.tool()
def rebuild_from_ir(artifact_path: str, fresh_document: bool = True) -> str:
    """Rebuild a part from its analysis artifact's Feature Graph IR — the verification half of
    the round-trip ("the LLM proposes, the round-trip decides", IR-ADR-006). Reads
    `<...>.analysis.json`, takes `ir.graph`, and executes it through the SAME deterministic
    pycompiler the test tool uses (two doors, ONE compiler — never forked).

    fresh_document (default True): open a NEW blank part first — the normal round-trip flow
        (rebuild fresh, then compare_parts against the original). Pass False only if you have
        already prepared the target document yourself.

    The rebuild reproduces the part AS-ANALYZED. If the source file changed since the artifact
    was written (hash mismatch) the result carries source_stale=true — re-run save_analysis and
    regenerate the IR rather than trusting a stale graph.

    Returns the compiler's per-node summary (COMPLETED n/n, or the feature-level error and how
    far it got — partial geometry may remain; CAD ops are not transactional). Afterwards, verify
    with compare_parts and only then label the artifact's ir.verification."""
    global _state_version
    path = os.path.abspath(artifact_path)
    if not os.path.isfile(path):
        return f"FAILED | ARTIFACT_NOT_FOUND | {path}"
    try:
        # utf-8-sig: tolerate a BOM — artifacts hand-edited or written by other Windows tools
        # (e.g. PowerShell 5.1 Out-File) may carry one; save_analysis itself writes without.
        with open(path, "r", encoding="utf-8-sig") as fh:
            artifact = json.load(fh)
    except (ValueError, OSError) as ex:
        return f"FAILED | ARTIFACT_UNREADABLE | {ex}"

    ir = artifact.get("ir") or {}
    graph = ir.get("graph")
    if not isinstance(graph, dict) or not graph.get("nodes"):
        status = (ir.get("verification") or {}).get("status")
        reason = ((ir.get("verification") or {}).get("detail") or {}).get("reason")
        return (f"FAILED | NO_IR_GRAPH | the artifact carries no executable ir.graph"
                f"{f' (verification: {status} | {reason})' if status else ''} — run the AI/IR "
                f"pass per cad-planner/recipe.md first")

    # Stale-source signal (cache discipline, ADR-040): informative, not blocking — the graph
    # legitimately rebuilds the part AS-ANALYZED.
    source_stale = ""
    src = (artifact.get("identity") or {}).get("source_path")
    recorded = (artifact.get("identity") or {}).get("source_hash")
    if src and recorded and os.path.isfile(src):
        with open(src, "rb") as fh:
            current = "sha256:" + hashlib.sha256(fh.read()).hexdigest()
        if current != recorded:
            source_stale = " | source_stale=true (file changed since analysis — regenerate the artifact)"

    if fresh_document:
        opened = _call_raw("open_new_part", {})
        if opened.get("status") != "COMPLETED":
            err = opened.get("error") or {}
            return f"FAILED | OPEN_NEW_PART_FAILED | {err.get('code')}: {err.get('message')}"

    # Lazy import: pycompiler lives in the hyphenated solidworks-compiler/ dir; ir_execution_port
    # wires sys.path + the ExecutionPort. Imported here so a missing compiler tree degrades to a
    # clean tool error instead of killing the whole MCP server at startup.
    try:
        from ir_execution_port import run_feature_graph
    except Exception as ex:  # noqa: BLE001
        return f"FAILED | COMPILER_UNAVAILABLE | {ex}"

    try:
        result = run_feature_graph(graph)
    finally:
        # One rebuild performs MANY state-bumping sub-ops outside _call() — resync so the next
        # normal tool call can't hit INVALID_STATE_VERSION (KNOWN-LIMITATIONS #5).
        try:
            _state_version = get_state()
        except Exception:  # noqa: BLE001
            pass

    return f"{result.summary()} | artifact={os.path.basename(path)}{source_stale}"


# ---------------------------------------------------------------------------
# Tool: compare_parts  (adapter-only — the objective round-trip verifier, ADR-040/A0)
# ---------------------------------------------------------------------------
@mcp.tool()
def compare_parts(doc_a: str, doc_b: str) -> str:
    """Objectively diff two part documents — the round-trip verifier behind the artifact's
    `verified` label (and a general-purpose "are these the same part?" check).

    doc_a / doc_b: EITHER an absolute .SLDPRT path (opened/activated from disk) OR the TITLE of
        an already-open document (e.g. 'Part4' for an unsaved rebuild). doc_a is the REFERENCE
        (deltas are relative to it — normally the original; doc_b = the rebuild).

    For each doc it runs analyze_model(geometry + mass_properties) and reports topology
    (bodies/faces/edges/vertices), volume, surface area and CG side by side with deltas, plus
    the DECIDED verified-criteria verdict (analysis-artifact.schema.json): topology EXACT AND
    |ΔV| <= 1% AND |ΔA| <= 1%. The verdict is a REPORT — writing ir.verification into the
    artifact stays the caller's job. Read-only geometry-wise (activation may switch the active
    document; doc_b is left active). bbox comparison: pending (analyze doesn't expose it yet)."""
    def _read(doc: str, label: str):
        if os.path.isfile(doc):
            r = _call_raw("open_document", {"file_path": os.path.abspath(doc)})
            if r.get("status") != "COMPLETED":
                r = _call_raw("activate_document", {"title": os.path.splitext(os.path.basename(doc))[0]})
        else:
            r = _call_raw("activate_document", {"title": doc})
        if r.get("status") != "COMPLETED":
            err = r.get("error") or {}
            raise RuntimeError(f"DOC_{label}_UNAVAILABLE | {doc} | {err.get('code')}: {err.get('message')}")
        geo = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "geometry", "name": ""})))
        mass = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
        return geo, mass

    try:
        geo_a, mass_a = _read(doc_a, "A")
        geo_b, mass_b = _read(doc_b, "B")
    except RuntimeError as ex:
        return f"FAILED | {ex}"

    topo_keys = ("bodies", "faces", "edges", "vertices")
    topo_a = [geo_a.get(k) for k in topo_keys]
    topo_b = [geo_b.get(k) for k in topo_keys]
    topology_exact = topo_a == topo_b and None not in topo_a

    def _delta_pct(a, b):
        if not isinstance(a, (int, float)) or not isinstance(b, (int, float)) or a == 0:
            return None
        return (b - a) / a * 100.0

    dv = _delta_pct(mass_a.get("volume"), mass_b.get("volume"))
    da = _delta_pct(mass_a.get("surface_area"), mass_b.get("surface_area"))
    cg_dist = None
    if all(isinstance(mass_x.get(k), (int, float)) for mass_x in (mass_a, mass_b) for k in ("cx", "cy", "cz")):
        cg_dist = ((mass_a["cx"] - mass_b["cx"]) ** 2 + (mass_a["cy"] - mass_b["cy"]) ** 2
                   + (mass_a["cz"] - mass_b["cz"]) ** 2) ** 0.5

    verified = (topology_exact and dv is not None and da is not None
                and abs(dv) <= 1.0 and abs(da) <= 1.0)

    fmt = lambda v, spec=".6g": ("?" if v is None else format(v, spec))  # noqa: E731
    return (f"COMPLETED | verified_criteria={'PASS' if verified else 'FAIL'} | "
            f"topology A={'-'.join(str(t) for t in topo_a)} B={'-'.join(str(t) for t in topo_b)} "
            f"{'EXACT' if topology_exact else 'DIFFER'} | "
            f"volume A={fmt(mass_a.get('volume'))} B={fmt(mass_b.get('volume'))} dV={fmt(dv, '.4f')}% | "
            f"area A={fmt(mass_a.get('surface_area'))} B={fmt(mass_b.get('surface_area'))} dA={fmt(da, '.4f')}% | "
            f"cg_distance={fmt(cg_dist)} m | reference=A ({doc_a})")


# ---------------------------------------------------------------------------
# TEST TOOL (disabled): submit_feature_graph — Feature Graph IR + deterministic compiler
# ---------------------------------------------------------------------------
# The mainline IR path is the analysis pipeline's rebuild_from_ir (logs.md ADR-040, Phase A;
# logs-ir.md IR-ADR-005). This direct-IR entry point is kept as a DEVELOPMENT/TEST tool only and is
# commented out so it never appears on the MCP surface (zero token cost). The old experimental
# gates (SOLIDPILOT_ENABLE_IR env switch + i_understand_this_is_experimental param) were DELETED
# (IR-ADR-005) — the block below is the simplified, gate-free version.
#
# TO RE-ENABLE (3 steps):
#   1. Uncomment this whole block AND the `from ir_execution_port import run_feature_graph`
#      import at the top of this file.
#   2. Re-add the following entry to solidworks-execution/contracts/tool-schemas.json (the
#      contract test tests/test_schema_contract.py fails otherwise):
#
#          "submit_feature_graph": {
#              "description": "[TEST TOOL — P1.4/P1.7] Feature Graph IR + deterministic compiler path. NOT a COM/execution tool: it has NO ToolController/SolidWorksService case. The adapter's pycompiler (solidworks-compiler/pycompiler) lowers a CAD-neutral Feature Graph IR into ordered calls to the EXISTING low-level tools (create_sketch / add_sketch_entity / extrude_feature / analyze_model) and resolves semantic refs (top_face/center) against live geometry. Coexists with the low-level tools WITHOUT changing them. v0-exp vocabulary: box, sketch+extrude (boss/cut), hole-on-face (selector 'top', position 'center', depth 'through_all'). The adapter resyncs its local state_version from GET /state after a run (one submit performs many state-bumping sub-ops).",
#              "input": {
#                  "operation_id": "n/a at this level — each lowered sub-op carries its own operation_id (uuid4) and state_version on /api/tool/execute",
#                  "state_version": "n/a at this level — the adapter resyncs its local state_version from GET /state after the run",
#                  "params": {
#                      "graph": "string (required — JSON Feature Graph IR per cad-planner/contracts/feature-graph.schema.json v0-exp; all lengths in meters)",
#                      "user_request": "string (optional — the user's request phrase, recorded for the logs)"
#                  }
#              },
#              "output": "Per-node report string: COMPLETED (nodes built) or FAILED (feature-level error + how far it got). Partial geometry may remain on failure (CAD ops are not transactional). Not itself in the state_version/idempotency envelope; its sub-ops are."
#          }
#
#   3. Reconnect the MCP server (no hot-reload — KNOWN-LIMITATIONS #4).
#
# def _resync_state_version() -> int:
#     """Realign the adapter's local state_version with the authoritative GET /state.
#
#     A submit_feature_graph run performs MANY execution ops (each bumping state_version) OUTSIDE the
#     normal _call() path, so afterwards — success OR failure — we resync the local value, or the NEXT
#     normal tool call would fail INVALID_STATE_VERSION (KNOWN-LIMITATIONS #5)."""
#     global _state_version
#     try:
#         _state_version = get_state()
#     except Exception:
#         pass
#     return _state_version
#
#
# @mcp.tool()
# def submit_feature_graph(graph: str, user_request: str = "") -> str:
#     """[TEST TOOL] Build a part from a CAD-neutral Feature Graph IR via the deterministic
#     compiler (ONE IR -> MANY low-level tool calls).
#
#     graph: a JSON-string Feature Graph (see cad-planner/contracts/feature-graph.schema.json,
#         v0-exp subset). Covered v0 vocabulary: 'box' (rectangular boss), 'sketch'+'extrude'
#         (boss/cut), and 'hole' on a face with semantic refs (selector 'top', position 'center',
#         depth 'through_all'). All lengths in METERS. The box is built centred on the datum origin.
#     user_request: optional — the user's request phrase, recorded for the logs.
#
#     Returns a per-node report (COMPLETED, or FAILED with how far it got + a feature-level error).
#     CAD ops are not transactional: on failure partial geometry may remain (reported, not hidden);
#     the adapter's state_version is resynced either way so subsequent normal tools keep working."""
#     # Parse the graph (structural validation happens inside the compiler).
#     try:
#         graph_obj = json.loads(graph)
#     except Exception as ex:
#         return f"FAILED | INVALID_JSON | the graph is not valid JSON: {ex}"
#     # Compile + run. NEVER let an exception crash the MCP server; resync state_version regardless.
#     try:
#         result = run_feature_graph(graph_obj)
#     except Exception as ex:
#         sv = _resync_state_version()
#         return f"FAILED | UNEXPECTED | {type(ex).__name__}: {ex} | state_version resynced to {sv}"
#     sv = _resync_state_version()  # first-class resync, success OR failure
#     return result.summary() + f" | state_version={sv}"


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    mcp.run()
